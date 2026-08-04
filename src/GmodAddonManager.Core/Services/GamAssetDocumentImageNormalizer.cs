using System;
using System.IO;
using GmodAddonManager.Core.Models;
using SkiaSharp;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Decodes an untrusted embedded asset image within conservative limits and
    /// converts it to GAM's fixed portable representation.
    /// </summary>
    public static class GamAssetDocumentImageNormalizer
    {
        public const int OutputWidth = 512;
        public const int OutputHeight = 512;
        public const int MaximumInputBytes = 4 * 1024 * 1024;
        public const int MaximumDimension = 8192;
        public const long MaximumPixelCount = 16L * 1024L * 1024L;
        public const long MaximumDecodedBytes = 64L * 1024L * 1024L;
        public const int MaximumOutputBytes = 2 * 1024 * 1024;

        // Keep portable images visually identical to AddonManager's product image
        // normalization so export/import does not subtly change the thumbnail.
        private const float CornerRadiusRatio = 0.15625f;

        public static byte[] Normalize(byte[] encodedImage)
        {
            return Normalize(encodedImage, requirePng: false);
        }

        internal static byte[] NormalizePortablePng(byte[] encodedImage)
        {
            return Normalize(encodedImage, requirePng: true);
        }

        private static byte[] Normalize(byte[] encodedImage, bool requirePng)
        {
            if (encodedImage == null)
            {
                throw new ArgumentNullException(nameof(encodedImage));
            }

            if (encodedImage.Length == 0)
            {
                throw new GamAssetDocumentException("The embedded asset image is empty.");
            }

            if (encodedImage.Length > MaximumInputBytes)
            {
                throw new GamAssetDocumentException(
                    $"The embedded asset image exceeds the {MaximumInputBytes}-byte input limit.");
            }

            try
            {
                using var stream = new MemoryStream(encodedImage, writable: false);
                using var codec = SKCodec.Create(stream);
                if (codec == null)
                {
                    throw new GamAssetDocumentException("The embedded asset image cannot be decoded.");
                }
                if (requirePng && codec.EncodedFormat != SKEncodedImageFormat.Png)
                {
                    throw new GamAssetDocumentException(
                        "The embedded asset image data must be encoded as PNG.");
                }

                var sourceInfo = codec.Info;
                ValidateDimensions(sourceInfo.Width, sourceInfo.Height);

                var decodeInfo = new SKImageInfo(
                    sourceInfo.Width,
                    sourceInfo.Height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul);
                using var source = new SKBitmap(decodeInfo);
                var decodeResult = codec.GetPixels(decodeInfo, source.GetPixels());
                if (decodeResult != SKCodecResult.Success)
                {
                    throw new GamAssetDocumentException(
                        $"The embedded asset image is incomplete or invalid ({decodeResult}).");
                }

                using var normalized = new SKBitmap(
                    OutputWidth,
                    OutputHeight,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul);
                using (var canvas = new SKCanvas(normalized))
                using (var paint = new SKPaint
                {
                    FilterQuality = SKFilterQuality.High,
                    IsAntialias = true
                })
                using (var clipPath = new SKPath())
                {
                    canvas.Clear(SKColors.Transparent);
                    var radius = OutputWidth * CornerRadiusRatio;
                    clipPath.AddRoundRect(
                        new SKRect(0, 0, OutputWidth, OutputHeight),
                        radius,
                        radius);
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);

                    var cropSize = Math.Min(source.Width, source.Height);
                    var sourceLeft = (source.Width - cropSize) / 2f;
                    var sourceTop = (source.Height - cropSize) / 2f;
                    var sourceRect = new SKRect(
                        sourceLeft,
                        sourceTop,
                        sourceLeft + cropSize,
                        sourceTop + cropSize);
                    var destinationRect = new SKRect(0, 0, OutputWidth, OutputHeight);
                    canvas.DrawBitmap(source, sourceRect, destinationRect, paint);
                }

                using var data = normalized.Encode(SKEncodedImageFormat.Png, quality: 100);
                if (data == null)
                {
                    throw new GamAssetDocumentException("The embedded asset image could not be encoded as PNG.");
                }

                var result = data.ToArray();
                if (result.Length == 0 || result.Length > MaximumOutputBytes)
                {
                    throw new GamAssetDocumentException(
                        $"The normalized asset image exceeds the {MaximumOutputBytes}-byte output limit.");
                }

                return result;
            }
            catch (GamAssetDocumentException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is InvalidOperationException ||
                ex is OverflowException)
            {
                throw new GamAssetDocumentException("The embedded asset image is invalid.", ex);
            }
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new GamAssetDocumentException("The embedded asset image has invalid dimensions.");
            }

            if (width > MaximumDimension || height > MaximumDimension)
            {
                throw new GamAssetDocumentException(
                    $"The embedded asset image exceeds the {MaximumDimension}-pixel dimension limit.");
            }

            var pixelCount = (long)width * height;
            if (pixelCount > MaximumPixelCount || pixelCount * 4L > MaximumDecodedBytes)
            {
                throw new GamAssetDocumentException("The embedded asset image is too large to decode safely.");
            }
        }
    }
}
