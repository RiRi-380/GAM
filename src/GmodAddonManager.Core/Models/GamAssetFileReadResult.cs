using System;

namespace GmodAddonManager.Core.Models
{
    public enum GamAssetFileContentKind
    {
        SingleAsset = 0,
        Bundle = 1
    }

    /// <summary>
    /// Discriminated result returned by the compatibility reader for v1/v2
    /// single-Asset documents and v3/v4 bundles.
    /// </summary>
    public sealed class GamAssetFileReadResult
    {
        private GamAssetFileReadResult(
            GamAssetFileContentKind kind,
            GamAssetDocument? singleAsset,
            GamAssetBundleDocument? bundle)
        {
            Kind = kind;
            SingleAsset = singleAsset;
            Bundle = bundle;
        }

        public GamAssetFileContentKind Kind { get; }

        public GamAssetDocument? SingleAsset { get; }

        public GamAssetBundleDocument? Bundle { get; }

        public int SourceFormatVersion => Kind == GamAssetFileContentKind.SingleAsset
            ? SingleAsset!.SourceFormatVersion
            : Bundle!.SourceFormatVersion;

        public static GamAssetFileReadResult FromSingleAsset(GamAssetDocument document)
        {
            return new GamAssetFileReadResult(
                GamAssetFileContentKind.SingleAsset,
                document ?? throw new ArgumentNullException(nameof(document)),
                null);
        }

        public static GamAssetFileReadResult FromBundle(GamAssetBundleDocument document)
        {
            return new GamAssetFileReadResult(
                GamAssetFileContentKind.Bundle,
                null,
                document ?? throw new ArgumentNullException(nameof(document)));
        }
    }
}
