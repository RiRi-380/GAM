using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GmodAddonManager.Core.Services
{
    internal static class BoundedHttpContentReader
    {
        internal const int DefaultImageLimitBytes = 8 * 1024 * 1024;
        private const int BufferSize = 81920;

        internal static async Task<byte[]> ReadAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            var declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > maximumBytes)
            {
                throw new InvalidDataException(
                    $"HTTP content length {declaredLength.Value} exceeds the {maximumBytes}-byte limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var destination = new MemoryStream(
                declaredLength.HasValue && declaredLength.Value >= 0
                    ? (int)Math.Min(declaredLength.Value, maximumBytes)
                    : 0);

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(BufferSize, maximumBytes));
            try
            {
                var totalBytes = 0;
                while (true)
                {
                    var remainingBytes = maximumBytes - totalBytes;
                    var requestedBytes = remainingBytes >= buffer.Length
                        ? buffer.Length
                        : remainingBytes + 1;
                    var bytesRead = await source.ReadAsync(
                        buffer.AsMemory(0, requestedBytes),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (bytesRead > maximumBytes - totalBytes)
                    {
                        throw new InvalidDataException(
                            $"HTTP content exceeds the {maximumBytes}-byte limit.");
                    }

                    destination.Write(buffer, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                return destination.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
