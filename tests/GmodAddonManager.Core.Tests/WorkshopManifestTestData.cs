using System.Text;

namespace GmodAddonManager.Core.Tests;

internal static class WorkshopManifestTestData
{
    public static string Write(
        string directory,
        params string[] subscribedAddonIds)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appworkshop_4000.acf");
        var ids = subscribedAddonIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("\"AppWorkshop\"");
        builder.AppendLine("{");
        builder.AppendLine("  \"WorkshopItemDetails\"");
        builder.AppendLine("  {");
        foreach (var id in ids)
        {
            builder.Append("    \"").Append(id).AppendLine("\"");
            builder.AppendLine("    {");
            builder.AppendLine("      \"subscribedby\" \"76561198000000000\"");
            builder.AppendLine("    }");
        }
        builder.AppendLine("  }");
        builder.AppendLine("  \"WorkshopItemsInstalled\"");
        builder.AppendLine("  {");
        foreach (var id in ids)
        {
            builder.Append("    \"").Append(id).AppendLine("\"");
            builder.AppendLine("    {");
            builder.AppendLine("      \"size\" \"1\"");
            builder.AppendLine("    }");
        }
        builder.AppendLine("  }");
        builder.AppendLine("}");

        File.WriteAllText(path, builder.ToString());
        return path;
    }
}
