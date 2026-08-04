using GmodAddonManager.Core.Models;
using GmodAddonManager.Core.Services;
using SkiaSharp;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonManagerSafetyBoundaryTests : IDisposable
{
    private readonly string rootPath;
    private readonly string workshopPath;
    private readonly string appDataPath;
    private readonly string gmodRootPath;

    public AddonManagerSafetyBoundaryTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-safety-boundary-tests-" + Guid.NewGuid().ToString("N"));
        workshopPath = Path.Combine(rootPath, "workshop");
        appDataPath = Path.Combine(rootPath, "appdata");
        gmodRootPath = Path.Combine(rootPath, "GarrysMod");
        Directory.CreateDirectory(workshopPath);
        Directory.CreateDirectory(appDataPath);
        Directory.CreateDirectory(Path.Combine(gmodRootPath, "garrysmod", "addons"));
    }

    [Fact]
    public void ValidatePath_RequiresDirectoryBoundaryAndAllowsDotsInsideSegmentNames()
    {
        using var manager = CreateManager();
        var prefixSibling = workshopPath + "-evil";

        Assert.Throws<ArgumentException>(
            () => manager.ValidatePath(prefixSibling, "candidate"));
        Assert.Throws<ArgumentException>(
            () => manager.ValidatePath(
                Path.Combine(prefixSibling, "payload.gma"),
                "candidate"));
        Assert.Throws<ArgumentException>(
            () => manager.ValidatePath(
                Path.Combine(
                    workshopPath,
                    "..",
                    Path.GetFileName(prefixSibling),
                    "payload.gma"),
                "candidate"));

        manager.ValidatePath(workshopPath, "candidate");
        manager.ValidatePath(
            Path.Combine(workshopPath, "games..backup", "payload.gma"),
            "candidate");
        manager.ValidatePath(
            Path.Combine(workshopPath, "child", "..", "payload.gma"),
            "candidate");
    }

    [Fact]
    public void ResolveLocalMountPath_RejectsPrefixSiblingDirectories()
    {
        using var manager = CreateManager();
        var localAddonsPath = Path.Combine(gmodRootPath, "garrysmod", "addons");
        var directoryAddon = new WorkshopAddon(
            "local_directory",
            Path.Combine(localAddonsPath + "-evil", "payload"))
        {
            IsLocal = true,
            IsGmaFile = false,
            LocalMountPath = Path.Combine(localAddonsPath + "-evil", "payload")
        };
        var rootGmaAddon = new WorkshopAddon(
            "local_gma",
            Path.Combine(gmodRootPath, "garrysmod-evil", "payload.gma"))
        {
            IsLocal = true,
            IsGmaFile = true,
            LocalMountPath = Path.Combine(
                gmodRootPath,
                "garrysmod-evil",
                "payload.gma")
        };
        var invalidAddon = new WorkshopAddon("local_invalid", string.Empty)
        {
            IsLocal = true,
            LocalMountPath = "invalid\0path"
        };
        var validDirectoryPath = Path.Combine(localAddonsPath, "valid-payload");
        var validDirectoryAddon = new WorkshopAddon(
            "local_valid_directory",
            validDirectoryPath)
        {
            IsLocal = true,
            LocalMountPath = validDirectoryPath
        };
        var validRootGmaPath = Path.Combine(
            gmodRootPath,
            "garrysmod",
            "valid-payload.gma");
        var validRootGmaAddon = new WorkshopAddon(
            "local_valid_gma",
            validRootGmaPath)
        {
            IsLocal = true,
            IsGmaFile = true,
            LocalMountPath = validRootGmaPath
        };

        Assert.Null(manager.ResolveLocalMountPath(directoryAddon));
        Assert.Null(manager.ResolveLocalMountPath(rootGmaAddon));
        Assert.Null(manager.ResolveLocalMountPath(invalidAddon));
        Assert.Equal(
            validDirectoryPath,
            manager.ResolveLocalMountPath(validDirectoryAddon));
        Assert.Equal(
            validRootGmaPath,
            manager.ResolveLocalMountPath(validRootGmaAddon));
    }

    [Fact]
    public async Task AssetImageResolvers_AllowOnlyOwnedImageDirectory()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("Image Asset");
        var group = await manager.CreateAssetGroupAsync(
            "Image Group",
            Array.Empty<string>());
        var outsideAssetPath = Path.Combine(workshopPath, "outside-asset.png");
        var outsideGroupPath = Path.Combine(workshopPath, "outside-group.png");
        asset.ImagePath = outsideAssetPath;
        group.ImagePath = outsideGroupPath;

        Assert.Null(manager.ResolveAssetImagePath(asset));
        Assert.Null(manager.ResolveAssetGroupImagePath(group));

        var imageDirectory = Path.Combine(appDataPath, "asset-images");
        var relativeAssetPath = Path.Combine("asset-images", "inside-asset.png");
        var absoluteGroupPath = Path.Combine(imageDirectory, "inside-group.png");
        asset.ImagePath = relativeAssetPath;
        group.ImagePath = absoluteGroupPath;

        Assert.Equal(
            Path.Combine(appDataPath, relativeAssetPath),
            manager.ResolveAssetImagePath(asset));
        Assert.Equal(
            absoluteGroupPath,
            manager.ResolveAssetGroupImagePath(group));
    }

    [Fact]
    public async Task RemovingConfiguredImages_NeverDeletesFilesOutsideOwnedDirectory()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("External Image Asset");
        var group = await manager.CreateAssetGroupAsync(
            "External Image Group",
            Array.Empty<string>());
        var outsideAssetPath = Path.Combine(workshopPath, "outside-asset.png");
        var outsideGroupPath = Path.Combine(workshopPath, "outside-group.png");
        await File.WriteAllBytesAsync(outsideAssetPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(outsideGroupPath, [4, 5, 6]);
        asset.ImagePath = outsideAssetPath;
        group.ImagePath = outsideGroupPath;

        manager.RemoveAssetImage(asset.Id);
        Assert.True(await manager.ApplyAssetGroupEditAsync(
            group.Id,
            group.Name,
            sourceImagePath: null,
            crop: null,
            removeImage: true));

        Assert.Null(asset.ImagePath);
        Assert.Null(group.ImagePath);
        Assert.True(File.Exists(outsideAssetPath));
        Assert.True(File.Exists(outsideGroupPath));
    }

    [Fact]
    public async Task SetAssetImageFromFile_RejectsDimensionOverDecodeLimit()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("Oversized Image Asset");
        var sourcePath = WriteImage(
            "oversized.png",
            GamAssetDocumentImageNormalizer.MaximumDimension + 1,
            1,
            SKEncodedImageFormat.Png);

        Assert.Throws<InvalidOperationException>(
            () => manager.SetAssetImageFromFile(asset.Id, sourcePath, crop: null));
        Assert.Null(asset.ImagePath);
    }

    [Fact]
    public void AssetImageDecodeBudget_CoversPixelAndDecodedByteLimits()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AddonManager.ValidateAssetImageDecodeInfo(new SKImageInfo(
                4097,
                4097,
                SKColorType.Rgba8888,
                SKAlphaType.Premul)));
        Assert.Throws<InvalidOperationException>(() =>
            AddonManager.ValidateAssetImageDecodeInfo(new SKImageInfo(
                4096,
                4096,
                SKColorType.RgbaF16,
                SKAlphaType.Premul)));

        AddonManager.ValidateAssetImageDecodeInfo(new SKImageInfo(
            4096,
            4096,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
    }

    [Fact]
    public async Task SetAssetImageFromFile_StillAcceptsJpeg()
    {
        using var manager = CreateManager();
        await manager.InitializeAsync();
        var asset = await manager.CreateAssetAsync("JPEG Image Asset");
        var sourcePath = WriteImage(
            "source.jpg",
            32,
            16,
            SKEncodedImageFormat.Jpeg);

        var relativePath = manager.SetAssetImageFromFile(
            asset.Id,
            sourcePath,
            crop: null);
        var storedPath = manager.ResolveAssetImagePath(asset);

        Assert.EndsWith(".png", relativePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(storedPath);
        Assert.True(File.Exists(storedPath));
        using var stored = SKBitmap.Decode(storedPath);
        Assert.NotNull(stored);
        Assert.Equal(512, stored.Width);
        Assert.Equal(512, stored.Height);
    }

    private AddonManager CreateManager()
    {
        return new AddonManager(new AddonManagerOptions
        {
            CustomWorkshopPath = workshopPath,
            CustomAppDataPath = appDataPath,
            CustomGmodInstallPath = gmodRootPath,
            DisableCacheScan = true,
            DisableMode = DisableMode.Soft,
            ScanCacheTtl = TimeSpan.Zero
        });
    }

    private string WriteImage(
        string fileName,
        int width,
        int height,
        SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality: 90);
        Assert.NotNull(encoded);
        var path = Path.Combine(rootPath, fileName);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
