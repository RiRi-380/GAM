using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// File I/O for legacy/current single-Asset documents and archive-backed v3/v4 bundles.
    /// Writes use a same-directory temporary file followed by an atomic replace/move.
    /// </summary>
    public sealed class GamAssetFileService
    {
        private const int BufferSize = 81_920;

        /// <summary>
        /// Reads a supported v1, v2, or v3 single-Asset document. Use
        /// <see cref="ReadAnyAsync"/> when a bundle is also accepted.
        /// </summary>
        public async Task<GamAssetDocument> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var fullPath = ValidateGamPath(path);
            using var stream = OpenRead(fullPath);
            return await ReadSingleAssetAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads supported single-Asset documents and v3/v4 bundles without guessing from
        /// the file extension (all supported versions intentionally use .gam).
        /// </summary>
        public async Task<GamAssetFileReadResult> ReadAnyAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var fullPath = ValidateGamPath(path);
            using var stream = OpenRead(fullPath);
            EnsureNotEmpty(stream);

            var signature = new byte[4];
            var signatureLength = 0;
            while (signatureLength < signature.Length)
            {
                var read = await stream.ReadAsync(
                    signature,
                    signatureLength,
                    signature.Length - signatureLength,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                signatureLength += read;
            }

            stream.Position = 0;
            if (signatureLength >= 2 && signature[0] == (byte)'P' && signature[1] == (byte)'K')
            {
                var bundle = await Task.Run(
                    () => GamAssetBundleCodec.Deserialize(stream, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return GamAssetFileReadResult.FromBundle(
                    bundle);
            }

            return GamAssetFileReadResult.FromSingleAsset(
                await ReadSingleAssetAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        public async Task<GamAssetBundleDocument> ReadBundleAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var fullPath = ValidateGamPath(path);
            using var stream = OpenRead(fullPath);
            var firstByte = new byte[1];
            var read = await stream.ReadAsync(
                firstByte,
                0,
                1,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new GamAssetDocumentException("The .gam document is empty.");
            }

            stream.Position = 0;
            return await Task.Run(
                () => GamAssetBundleCodec.Deserialize(stream, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAsync(
            string path,
            GamAssetDocument document,
            bool overwrite = true,
            CancellationToken cancellationToken = default)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var destination = ValidateWriteDestination(path, overwrite);
            var bytes = await Task.Run(
                () => GamAssetDocumentCodec.Serialize(document),
                cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(
                destination,
                overwrite,
                async stream =>
                {
                    await stream.WriteAsync(
                        bytes,
                        0,
                        bytes.Length,
                        cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteBundleAsync(
            string path,
            GamAssetBundleDocument document,
            bool overwrite = true,
            CancellationToken cancellationToken = default)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var destination = ValidateWriteDestination(path, overwrite);
            await WriteAtomicallyAsync(
                destination,
                overwrite,
                stream =>
                {
                    return Task.Run(
                        () => GamAssetBundleCodec.Serialize(stream, document, cancellationToken),
                        cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }

        private static FileStream OpenRead(string fullPath)
        {
            return new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private static void EnsureNotEmpty(Stream stream)
        {
            if (stream.Length <= 0)
            {
                throw new GamAssetDocumentException("The .gam document is empty.");
            }
        }

        private static async Task<GamAssetDocument> ReadSingleAssetAsync(
            FileStream stream,
            CancellationToken cancellationToken)
        {
            EnsureNotEmpty(stream);
            if (stream.Length > GamAssetDocumentCodec.MaximumDocumentBytes)
            {
                throw new GamAssetDocumentException(
                    $"This single-Asset .gam document exceeds the " +
                    $"{GamAssetDocumentCodec.MaximumDocumentBytes}-byte safety limit. " +
                    "Use the streaming .gam bundle format for very large exports.");
            }

            var expectedLength = checked((int)stream.Length);
            var bytes = new byte[expectedLength];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(
                    bytes,
                    offset,
                    bytes.Length - offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new GamAssetDocumentException(
                        "The .gam document ended before it could be read completely.");
                }

                offset += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1)
            {
                throw new GamAssetDocumentException(
                    "The .gam document changed while it was being read.");
            }

            return await Task.Run(
                () => GamAssetDocumentCodec.Deserialize(bytes, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        private static string ValidateWriteDestination(string path, bool overwrite)
        {
            var fullPath = ValidateGamPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    $"The destination directory does not exist: {directory}");
            }

            if (!overwrite && File.Exists(fullPath))
            {
                throw new IOException($"The destination .gam file already exists: {fullPath}");
            }

            return fullPath;
        }

        private static async Task WriteAtomicallyAsync(
            string fullPath,
            bool overwrite,
            Func<FileStream, Task> write,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await write(stream).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(fullPath))
                {
                    if (!overwrite)
                    {
                        throw new IOException(
                            $"The destination .gam file already exists: {fullPath}");
                    }

                    try
                    {
                        File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
                    }
                    catch (FileNotFoundException) when (!File.Exists(fullPath))
                    {
                        File.Move(temporaryPath, fullPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Preserve the original exception. The uniquely named temp file
                        // contains only the just-serialized portable document.
                    }
                }
            }
        }

        private static string ValidateGamPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("The .gam path cannot be empty.", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".gam", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The asset document path must use the .gam extension.", nameof(path));
            }

            return fullPath;
        }
    }
}
