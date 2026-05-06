using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services;

public interface IDisableManifestParser
{
    DisableManifest Parse(string text);
    Task<DisableManifest> ParseFileAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class DisableManifestParser : IDisableManifestParser
{
    public DisableManifest Parse(string text)
    {
        var addonIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var invalidLines = new List<DisableManifestInvalidLine>();
        var duplicateCount = 0;
        var hasMagicHeader = false;
        var hasAction = false;
        var schemaVersion = string.Empty;
        var appId = DisableManifest.SupportedAppId;
        var action = string.Empty;
        var mode = DisableManifestMode.Merge;
        var name = DisableManifest.DefaultName;
        var source = string.Empty;

        var lines = SplitLines(text ?? string.Empty);
        for (var index = 0; index < lines.Count; index++)
        {
            var lineNumber = index + 1;
            var rawLine = lines[index];
            var trimmed = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var headerText = trimmed.Substring(1).Trim();
                if (headerText.Equals(DisableManifest.SupportedSchemaVersion, StringComparison.OrdinalIgnoreCase))
                {
                    hasMagicHeader = true;
                    schemaVersion = DisableManifest.SupportedSchemaVersion;
                    continue;
                }

                var separatorIndex = headerText.IndexOf(':');
                if (separatorIndex < 0)
                {
                    continue;
                }

                var key = headerText.Substring(0, separatorIndex).Trim();
                var value = headerText.Substring(separatorIndex + 1).Trim();

                if (key.Equals("appid", StringComparison.OrdinalIgnoreCase))
                {
                    appId = value;
                }
                else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    action = value;
                    hasAction = true;
                }
                else if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseMode(value, out var parsedMode))
                    {
                        mode = parsedMode;
                    }
                    else
                    {
                        invalidLines.Add(new DisableManifestInvalidLine
                        {
                            LineNumber = lineNumber,
                            Text = rawLine,
                            Reason = "Unsupported mode"
                        });
                    }
                }
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        name = value;
                    }
                }
                else if (key.Equals("source", StringComparison.OrdinalIgnoreCase))
                {
                    source = value;
                }

                continue;
            }

            var bodyText = StripInlineComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                continue;
            }

            var workshopId = SteamUrlParser.ExtractWorkshopId(bodyText);
            if (string.IsNullOrWhiteSpace(workshopId) || !workshopId.All(char.IsDigit))
            {
                invalidLines.Add(new DisableManifestInvalidLine
                {
                    LineNumber = lineNumber,
                    Text = rawLine,
                    Reason = "Workshop ID not found"
                });
                continue;
            }

            if (!seenIds.Add(workshopId))
            {
                duplicateCount++;
                continue;
            }

            addonIds.Add(workshopId);
        }

        return new DisableManifest
        {
            HasMagicHeader = hasMagicHeader,
            HasAction = hasAction,
            SchemaVersion = schemaVersion,
            AppId = appId,
            Action = action,
            Mode = mode,
            Name = name,
            Source = source,
            AddonIds = addonIds,
            InvalidLines = invalidLines,
            DuplicateCount = duplicateCount
        };
    }

    public async Task<DisableManifest> ParseFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Manifest path is required.", nameof(path));
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return Parse(text);
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string StripInlineComment(string line)
    {
        var commentIndex = line.IndexOf('#');
        return commentIndex < 0 ? line : line.Substring(0, commentIndex);
    }

    private static bool TryParseMode(string value, out DisableManifestMode mode)
    {
        if (value.Equals("merge", StringComparison.OrdinalIgnoreCase))
        {
            mode = DisableManifestMode.Merge;
            return true;
        }

        if (value.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            mode = DisableManifestMode.Replace;
            return true;
        }

        if (value.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            mode = DisableManifestMode.New;
            return true;
        }

        mode = DisableManifestMode.Merge;
        return false;
    }
}
