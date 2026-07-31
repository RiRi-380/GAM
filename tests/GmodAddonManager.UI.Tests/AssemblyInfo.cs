using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestProcessIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var runtimeLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "gam-ui-test-runtime-logs",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(
            "GAM_RUNTIME_LOG_DIR",
            runtimeLogDirectory);
    }
}
