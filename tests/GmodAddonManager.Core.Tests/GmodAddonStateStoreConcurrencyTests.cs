using System.Text;
using GmodAddonManager.Core.Services;

namespace GmodAddonManager.Core.Tests;

public sealed class GmodAddonStateStoreConcurrencyTests : IDisposable
{
    private readonly string rootPath;
    private readonly string gmodRootPath;
    private readonly string noMountPath;

    public GmodAddonStateStoreConcurrencyTests()
    {
        rootPath = Path.Combine(
            Path.GetTempPath(),
            "gam-addon-state-store-concurrency-tests-" +
            Guid.NewGuid().ToString("N"));
        gmodRootPath = Path.Combine(rootPath, "GarrysMod");
        noMountPath = Path.Combine(
            gmodRootPath,
            "garrysmod",
            "cfg",
            "addonnomount.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(noMountPath)!);
    }

    [Fact]
    public void SetEnabled_ExternalChangeBeforeCommit_RereadsAndPreservesUnknownId()
    {
        WriteNoMountFile("100");
        var store = new GmodAddonStateStore(gmodRootPath);
        var injected = false;
        store.BeforeMergeCommitForTesting = () =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            WriteNoMountFile("100", "999");
        };

        var persisted = store.SetEnabled("100", enabled: true);

        Assert.True(injected);
        Assert.True(persisted);
        var snapshot = store.ReadSnapshot();
        Assert.True(snapshot.IsValidFormat);
        Assert.DoesNotContain("100", snapshot.DisabledIds);
        Assert.Contains("999", snapshot.DisabledIds);
    }

    [Fact]
    public void ReadSnapshot_ContinuouslyChangingFile_ThrowsInsteadOfReturningUnverifiedData()
    {
        WriteNoMountFile("100");
        var store = new GmodAddonStateStore(gmodRootPath);
        store.DuringStableReadForTesting = _ =>
            File.AppendAllText(noMountPath, " ", Encoding.UTF8);

        var error = Assert.Throws<IOException>(() => store.ReadSnapshot());

        Assert.Contains(
            "stable addonnomount.txt snapshot",
            error.Message,
            StringComparison.Ordinal);
    }

    private void WriteNoMountFile(params string[] disabledIds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"addonnomount\"");
        builder.AppendLine("{");
        for (var index = 0; index < disabledIds.Length; index++)
        {
            builder.Append("\t\"")
                .Append(index + 1)
                .Append("\"\t\t\"")
                .Append(disabledIds[index])
                .AppendLine("\"");
        }
        builder.AppendLine("}");
        File.WriteAllText(noMountPath, builder.ToString(), new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
