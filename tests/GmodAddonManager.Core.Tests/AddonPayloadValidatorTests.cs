using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class AddonPayloadValidatorTests
{
    [Fact]
    public void Validate_EmptyDirectory_IsInvalid()
    {
        using var env = new TestDirectory();

        var result = AddonPayloadValidator.Validate(env.Path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ZeroByteGma_IsInvalid()
    {
        using var env = new TestDirectory();
        File.WriteAllBytes(Path.Combine(env.Path, "123.gma"), Array.Empty<byte>());

        var result = AddonPayloadValidator.Validate(env.Path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MarkerOnlyDirectory_IsInvalid()
    {
        using var env = new TestDirectory();
        File.WriteAllText(Path.Combine(env.Path, ".gam_disabled"), "disabled");
        File.WriteAllText(Path.Combine(env.Path, ".gam_owner.json"), "{}");

        var result = AddonPayloadValidator.Validate(env.Path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ContentDirectoryWithFile_IsValid()
    {
        using var env = new TestDirectory();
        var luaPath = Path.Combine(env.Path, "lua");
        Directory.CreateDirectory(luaPath);
        File.WriteAllText(Path.Combine(luaPath, "autorun.lua"), "print('ok')");

        var result = AddonPayloadValidator.Validate(env.Path);

        Assert.True(result.IsValid);
        Assert.Equal(AddonPayloadKind.FolderAddon, result.Kind);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gam-payload-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
