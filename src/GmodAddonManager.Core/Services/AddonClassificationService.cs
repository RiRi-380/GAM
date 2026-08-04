using System;
using System.Collections.Generic;
using System.Linq;
using GmodAddonManager.Core.Models;

namespace GmodAddonManager.Core.Services
{
    public enum AddonClassificationMatch
    {
        Unknown,
        Match,
        NoMatch
    }

    /// <summary>
    /// Canonical classification contract shared by the filter UI and Smart Assets.
    /// Matching intentionally mirrors the existing filter behavior, including tag
    /// aliases and singular/plural matching.
    /// </summary>
    public static class AddonClassificationService
    {
        private static readonly string[] TypeValues =
        {
            "Gamemode",
            "Map",
            "Weapon",
            "Vehicle",
            "NPC",
            "Tool",
            "Entity",
            "Effects",
            "Model",
            "ServerContent"
        };

        private static readonly string[] TagValues =
        {
            "Build",
            "Cartoon",
            "Comic",
            "Fun",
            "Movie",
            "Roleplay",
            "Scenic",
            "Realism",
            "Water"
        };

        private static readonly Dictionary<string, string> TagAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scenery"] = "scenic",
                ["roleplaying"] = "roleplay",
                ["rp"] = "roleplay",
                ["pose"] = "posed"
            };

        private static readonly (string Tag, string Type)[] TypeTagMappings =
        {
            ("gamemode", "Gamemode"),
            ("map", "Map"),
            ("weapon", "Weapon"),
            ("vehicle", "Vehicle"),
            ("npc", "NPC"),
            ("tool", "Tool"),
            ("entity", "Entity"),
            ("effect", "Effects"),
            ("effects", "Effects"),
            ("model", "Model"),
            ("servercontent", "ServerContent")
        };

        public static IReadOnlyList<string> SupportedTypes { get; } =
            Array.AsReadOnly(TypeValues);

        public static IReadOnlyList<string> SupportedTags { get; } =
            Array.AsReadOnly(TagValues);

        public static bool TryNormalizeRule(
            AssetMembershipRule? rule,
            out AssetMembershipRule normalizedRule,
            out string? error)
        {
            normalizedRule = new AssetMembershipRule();
            error = null;

            if (rule == null)
            {
                error = "The Smart Asset membership rule is missing.";
                return false;
            }

            if (rule.SchemaVersion != AssetMembershipRule.CurrentSchemaVersion)
            {
                error =
                    $"Unsupported Smart Asset rule schema {rule.SchemaVersion}; " +
                    $"supported schema is {AssetMembershipRule.CurrentSchemaVersion}.";
                return false;
            }

            if (rule.Kind != AssetMembershipRuleKind.Type &&
                rule.Kind != AssetMembershipRuleKind.Tag)
            {
                error = $"Unsupported Smart Asset rule kind: {(int)rule.Kind}.";
                return false;
            }

            var supported = rule.Kind == AssetMembershipRuleKind.Type
                ? TypeValues
                : TagValues;
            var valueKey = NormalizeToken(rule.Value);
            var canonical = supported.FirstOrDefault(value =>
                string.Equals(NormalizeToken(value), valueKey, StringComparison.Ordinal));
            if (canonical == null)
            {
                error =
                    $"Unsupported Smart Asset {rule.Kind.ToString().ToLowerInvariant()} " +
                    $"value: {rule.Value ?? string.Empty}.";
                return false;
            }

            normalizedRule = new AssetMembershipRule(rule.Kind, canonical)
            {
                SchemaVersion = AssetMembershipRule.CurrentSchemaVersion
            };
            return true;
        }

        public static AddonClassificationMatch Evaluate(
            WorkshopAddon? addon,
            AssetMembershipRule? rule)
        {
            if (addon == null ||
                !TryNormalizeRule(rule, out var normalizedRule, out _))
            {
                return AddonClassificationMatch.Unknown;
            }

            var addonTags = BuildTagSet(addon.Tags);
            if (normalizedRule.Kind == AssetMembershipRuleKind.Tag)
            {
                var selectedTag = NormalizeToken(normalizedRule.Value);
                if (ContainsMatch(addonTags, selectedTag))
                {
                    return AddonClassificationMatch.Match;
                }

                return IsTagsKnown(addon)
                    ? AddonClassificationMatch.NoMatch
                    : AddonClassificationMatch.Unknown;
            }

            var selectedType = NormalizeToken(normalizedRule.Value);
            var addonType = NormalizeToken(addon.Type);
            if ((!string.IsNullOrEmpty(addonType) &&
                 (string.Equals(addonType, selectedType, StringComparison.Ordinal) ||
                  ContainsMatch(
                      new HashSet<string>(StringComparer.Ordinal) { addonType },
                      selectedType))) ||
                ContainsMatch(addonTags, selectedType))
            {
                return AddonClassificationMatch.Match;
            }

            // Tags may positively infer a Type (handled above), but a tag-only
            // metadata result does not prove that the independent Type
            // classification completed. Preserve existing membership until the
            // Type source itself is authoritative.
            return IsTypeKnown(addon)
                ? AddonClassificationMatch.NoMatch
                : AddonClassificationMatch.Unknown;
        }

        public static string[] NormalizeTags(IEnumerable<string>? values)
        {
            return BuildTagSet(values)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Returns the same comparison key used by both filter and Smart Asset
        /// matching, including whitespace/separator removal and aliases.
        /// </summary>
        public static string Canonicalize(string? value) => NormalizeToken(value);

        /// <summary>
        /// Mirrors the existing filter metadata supplement's Type inference.
        /// This is positive inference only; a null result is not a confirmed
        /// Type non-match.
        /// </summary>
        public static string? InferTypeFromTags(IEnumerable<string>? tags)
        {
            var tagSet = BuildTagSet(tags);
            if (tagSet.Count == 0)
            {
                return null;
            }

            foreach (var mapping in TypeTagMappings)
            {
                if (ContainsMatch(tagSet, NormalizeToken(mapping.Tag)))
                {
                    return mapping.Type;
                }
            }

            return null;
        }

        private static bool IsTypeKnown(WorkshopAddon addon)
        {
            return addon.TypeMetadataStatus == AddonClassificationMetadataStatus.Known ||
                   !string.IsNullOrWhiteSpace(addon.Type);
        }

        private static bool IsTagsKnown(WorkshopAddon addon)
        {
            return addon.TagsMetadataStatus == AddonClassificationMetadataStatus.Known ||
                   (addon.Tags?.Any(tag => !string.IsNullOrWhiteSpace(tag)) ?? false);
        }

        private static HashSet<string> BuildTagSet(IEnumerable<string>? tags)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tags == null)
            {
                return result;
            }

            foreach (var tag in tags)
            {
                foreach (var part in SplitTagValue(tag))
                {
                    var normalized = NormalizeToken(part);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        result.Add(normalized);
                    }
                }
            }

            return result;
        }

        private static IEnumerable<string> SplitTagValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var separators = value.Contains(',') || value.Contains(';')
                ? new[] { ',', ';' }
                : new[] { ' ', '\t', '\r', '\n' };
            foreach (var part in value.Split(
                         separators,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }

        private static bool ContainsMatch(HashSet<string> values, string key)
        {
            if (values.Contains(key))
            {
                return true;
            }

            if (key.EndsWith("s", StringComparison.Ordinal) && key.Length > 1)
            {
                return values.Contains(key.Substring(0, key.Length - 1));
            }

            return values.Contains(key + "s");
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var buffer = new char[value.Length];
            var length = 0;
            foreach (var ch in value.Trim())
            {
                if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/')
                {
                    continue;
                }

                buffer[length++] = char.ToLowerInvariant(ch);
            }

            var normalized = new string(buffer, 0, length);
            return TagAliases.TryGetValue(normalized, out var alias)
                ? alias
                : normalized;
        }
    }
}
