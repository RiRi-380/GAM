using System;
using System.Reflection;

namespace GmodAddonManager.UI.Services;

internal static class ApplicationVersionProvider
{
    private const string FallbackVersion = "1.0.5";

    public static string GetUpdateVersion()
    {
        return NormalizeVersion(GetRawVersion());
    }

    public static string GetDisplayVersion()
    {
        return $"v{GetUpdateVersion()}";
    }

    private static string GetRawVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? FallbackVersion;
    }

    private static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(1);
        }

        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized.Substring(0, metadataIndex);
        }

        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            normalized = normalized.Substring(0, prereleaseIndex);
        }

        if (Version.TryParse(normalized, out var parsed))
        {
            if (parsed.Build < 0)
            {
                return $"{parsed.Major}.{parsed.Minor}";
            }

            if (parsed.Revision == 0)
            {
                return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
            }

            return parsed.ToString();
        }

        return FallbackVersion;
    }
}
