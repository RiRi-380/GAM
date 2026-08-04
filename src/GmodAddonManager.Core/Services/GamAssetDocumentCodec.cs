using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GmodAddonManager.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GmodAddonManager.Core.Services
{
    /// <summary>
    /// Strict codec for one portable .gam asset document. Versions 1 and 2 are
    /// import-only; serialization always emits the JSON version 3 format. Asset names, rules,
    /// IDs, and images retain field-level validation, but non-image membership data
    /// has no arbitrary aggregate product cap.
    /// </summary>
    public static class GamAssetDocumentCodec
    {
        public const string FormatIdentifier = "gam-asset";
        public const int CurrentFormatVersion = 3;
        public const int MaximumAssetNameLength = 200;
        public const int MaximumRuleValueLength = 64;
        public const int MaximumMemoLength = 4096;
        public const int MaximumDocumentBytes = 64 * 1024 * 1024;
        public const int MaximumAddonIdCount = 5_000_000;

        private const string LegacyHeader = "# GAM Collection Export v1";
        private const int MaximumJsonDepth = 16;
        private const int MaximumLegacyLineLength = 4096;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public static byte[] Serialize(GamAssetDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var validated = ValidateForWrite(document);
            var membership = new JObject
            {
                ["kind"] = validated.Membership.Kind == GamAssetDocumentMembershipKind.Fixed
                    ? "fixed"
                    : "smart"
            };

            if (validated.Membership.Kind == GamAssetDocumentMembershipKind.Fixed)
            {
                membership["addonIds"] = ToJsonArray(validated.Membership.AddonIds);
            }
            else
            {
                var rule = validated.Membership.Rule!;
                membership["rule"] = new JObject
                {
                    ["kind"] = rule.Kind == GamAssetDocumentRuleKind.Type ? "type" : "tag",
                    ["value"] = rule.Value
                };
                membership["snapshotAddonIds"] = ToJsonArray(validated.Membership.SnapshotAddonIds);
            }

            var root = new JObject
            {
                ["format"] = FormatIdentifier,
                ["version"] = CurrentFormatVersion,
                ["asset"] = new JObject
                {
                    ["name"] = validated.Name,
                    ["state"] = StateToWireValue(validated.State),
                    ["membership"] = membership
                }
            };

            if (validated.Memo != null)
            {
                ((JObject)root["asset"]!)["memo"] = validated.Memo;
            }

            if (validated.ImageBytes != null)
            {
                root["image"] = new JObject
                {
                    ["mediaType"] = "image/png",
                    ["sha256"] = ComputeSha256(validated.ImageBytes),
                    ["data"] = Convert.ToBase64String(validated.ImageBytes)
                };
            }

            var json = root.ToString(Formatting.Indented) + Environment.NewLine;
            var bytes = StrictUtf8.GetBytes(json);
            if (bytes.Length > MaximumDocumentBytes)
            {
                throw new GamAssetDocumentException(
                    $"The single-Asset .gam document exceeds the {MaximumDocumentBytes}-byte safety limit.");
            }

            return bytes;
        }

        public static GamAssetDocument Deserialize(byte[] documentBytes)
        {
            return Deserialize(documentBytes, CancellationToken.None);
        }

        public static GamAssetDocument Deserialize(
            byte[] documentBytes,
            CancellationToken cancellationToken)
        {
            if (documentBytes == null)
            {
                throw new ArgumentNullException(nameof(documentBytes));
            }

            if (documentBytes.Length == 0)
            {
                throw new GamAssetDocumentException("The .gam document is empty.");
            }

            if (documentBytes.Length > MaximumDocumentBytes)
            {
                throw new GamAssetDocumentException(
                    $"The single-Asset .gam document exceeds the {MaximumDocumentBytes}-byte safety limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = StrictUtf8.GetString(documentBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new GamAssetDocumentException("The .gam document is not valid UTF-8.", ex);
            }

            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            var trimmedStart = text.TrimStart();
            if (trimmedStart.StartsWith(LegacyHeader, StringComparison.Ordinal))
            {
                return DeserializeLegacyV1(text, cancellationToken);
            }

            if (!trimmedStart.StartsWith("{", StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException("The .gam document format is not recognized.");
            }

            return DeserializeJson(text, cancellationToken);
        }

        public static string CanonicalizeRuleValue(
            GamAssetDocumentRuleKind kind,
            string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaximumRuleValueLength)
            {
                throw new GamAssetDocumentException("The smart asset rule value is invalid.");
            }

            var supportedValues = kind switch
            {
                GamAssetDocumentRuleKind.Type => AddonClassificationService.SupportedTypes,
                GamAssetDocumentRuleKind.Tag => AddonClassificationService.SupportedTags,
                _ => throw new GamAssetDocumentException("The smart asset rule kind is invalid.")
            };

            var canonical = supportedValues.FirstOrDefault(candidate =>
                string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                throw new GamAssetDocumentException(
                    $"The smart asset rule value '{trimmed}' is not supported for {kind}.");
            }

            return canonical;
        }

        private static GamAssetDocument DeserializeJson(
            string text,
            CancellationToken cancellationToken)
        {
            JObject root;
            try
            {
                using var stringReader = new StringReader(text);
                using var strictReader = new GamAssetStrictJsonTextReader(
                    stringReader,
                    cancellationToken);
                using var jsonReader = new JsonTextReader(strictReader)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = MaximumJsonDepth
                };
                root = JObject.Load(
                    jsonReader,
                    new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        LineInfoHandling = LineInfoHandling.Load
                    });

                if (jsonReader.Read())
                {
                    throw new GamAssetDocumentException("The .gam document contains trailing JSON content.");
                }
            }
            catch (GamAssetDocumentException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new GamAssetDocumentException("The JSON .gam document is not valid.", ex);
            }

            RequireOnlyProperties(root, "format", "version", "asset", "image");
            var format = GetRequiredString(root, "format");
            if (!string.Equals(format, FormatIdentifier, StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException("The .gam document identifier is invalid.");
            }

            var versionToken = GetRequiredToken(root, "version");
            if (versionToken.Type != JTokenType.Integer)
            {
                throw new GamAssetDocumentException("The .gam document version must be an integer.");
            }

            int version;
            try
            {
                version = versionToken.Value<int>();
            }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException)
            {
                throw new GamAssetDocumentException("The .gam document version is invalid.", ex);
            }

            if (version > CurrentFormatVersion)
            {
                throw new GamAssetDocumentException(
                    $"This .gam document uses unsupported future version {version}.");
            }

            if (version != 2 && version != CurrentFormatVersion)
            {
                throw new GamAssetDocumentException(
                    $"JSON .gam version {version} is not supported. Legacy v1 must use its text format.");
            }

            var asset = GetRequiredObject(root, "asset");
            if (version == 2)
            {
                RequireOnlyProperties(asset, "name", "state", "membership");
            }
            else
            {
                RequireOnlyProperties(asset, "name", "state", "membership", "memo");
            }
            var name = NormalizeAndValidateName(GetRequiredString(asset, "name"));
            var state = ParseState(GetRequiredString(asset, "state"));
            var membership = ParseMembership(GetRequiredObject(asset, "membership"));
            var memo = version >= 3 ? ParseOptionalMemo(asset, "memo") : null;

            byte[]? image = null;
            var imageToken = root["image"];
            if (imageToken != null)
            {
                if (imageToken.Type != JTokenType.Object)
                {
                    throw new GamAssetDocumentException("The .gam image entry must be an object.");
                }

                image = ParseImage((JObject)imageToken);
            }

            return new GamAssetDocument(name, state, membership, image, version, memo);
        }

        private static GamAssetDocument DeserializeLegacyV1(
            string text,
            CancellationToken cancellationToken)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var headerSeen = false;
            string? title = null;
            int? declaredCount = null;
            var parsedIds = new List<string>();

            foreach (var rawLine in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rawLine.Length > MaximumLegacyLineLength)
                {
                    throw new GamAssetDocumentException("A legacy .gam line is too long.");
                }

                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!headerSeen)
                {
                    if (!string.Equals(line, LegacyHeader, StringComparison.Ordinal))
                    {
                        throw new GamAssetDocumentException("The legacy .gam header is invalid.");
                    }

                    headerSeen = true;
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith("# Title:", StringComparison.Ordinal))
                    {
                        if (title != null)
                        {
                            throw new GamAssetDocumentException("The legacy .gam document has duplicate Title metadata.");
                        }

                        title = line.Substring("# Title:".Length).Trim();
                    }
                    else if (line.StartsWith("# Count:", StringComparison.Ordinal))
                    {
                        if (declaredCount.HasValue)
                        {
                            throw new GamAssetDocumentException("The legacy .gam document has duplicate Count metadata.");
                        }

                        var countText = line.Substring("# Count:".Length).Trim();
                        if (!int.TryParse(
                                countText,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var count) ||
                            count < 0)
                        {
                            throw new GamAssetDocumentException("The legacy .gam Count metadata is invalid.");
                        }

                        declaredCount = count;
                    }

                    continue;
                }

                if (parsedIds.Count >= MaximumAddonIdCount)
                {
                    throw new GamAssetDocumentException(
                        $"The .gam membership exceeds the {MaximumAddonIdCount}-ID safety limit.");
                }

                parsedIds.Add(ValidateWorkshopId(line));
            }

            if (!headerSeen)
            {
                throw new GamAssetDocumentException("The legacy .gam header is missing.");
            }

            if (declaredCount.HasValue && declaredCount.Value != parsedIds.Count)
            {
                throw new GamAssetDocumentException(
                    "The legacy .gam Count metadata does not match its addon ID lines.");
            }

            var uniqueIds = DeduplicatePreservingOrder(parsedIds);
            var normalizedTitle = string.IsNullOrWhiteSpace(title)
                ? "Imported Asset"
                : NormalizeAndValidateName(title);
            return new GamAssetDocument(
                normalizedTitle,
                GamAssetDocumentState.Enabled,
                GamAssetDocumentMembership.Fixed(uniqueIds),
                imageBytes: null,
                sourceFormatVersion: 1);
        }

        private static GamAssetDocumentMembership ParseMembership(JObject membership)
        {
            var kind = GetRequiredString(membership, "kind");
            if (string.Equals(kind, "fixed", StringComparison.Ordinal))
            {
                RequireOnlyProperties(membership, "kind", "addonIds");
                var ids = ParseAddonIdArray(membership, "addonIds");
                return GamAssetDocumentMembership.Fixed(ids);
            }

            if (string.Equals(kind, "smart", StringComparison.Ordinal))
            {
                RequireOnlyProperties(membership, "kind", "rule", "snapshotAddonIds");
                var ruleObject = GetRequiredObject(membership, "rule");
                RequireOnlyProperties(ruleObject, "kind", "value");
                var ruleKindText = GetRequiredString(ruleObject, "kind");
                var ruleKind = ruleKindText switch
                {
                    "type" => GamAssetDocumentRuleKind.Type,
                    "tag" => GamAssetDocumentRuleKind.Tag,
                    _ => throw new GamAssetDocumentException("The smart asset rule kind is invalid.")
                };
                var ruleValue = CanonicalizeRuleValue(
                    ruleKind,
                    GetRequiredString(ruleObject, "value"));
                var snapshotIds = ParseAddonIdArray(membership, "snapshotAddonIds");
                return GamAssetDocumentMembership.Smart(
                    new GamAssetDocumentRule(ruleKind, ruleValue),
                    snapshotIds);
            }

            throw new GamAssetDocumentException("The .gam membership kind is invalid.");
        }

        private static byte[] ParseImage(JObject image)
        {
            RequireOnlyProperties(image, "mediaType", "sha256", "data");
            if (!string.Equals(GetRequiredString(image, "mediaType"), "image/png", StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException("Only embedded PNG asset images are supported.");
            }

            var encoded = GetRequiredString(image, "data");
            var maximumBase64Length = ((GamAssetDocumentImageNormalizer.MaximumInputBytes + 2) / 3) * 4;
            if (encoded.Length == 0 || encoded.Length > maximumBase64Length)
            {
                throw new GamAssetDocumentException("The embedded asset image data is too large.");
            }

            byte[] imageBytes;
            try
            {
                imageBytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new GamAssetDocumentException("The embedded asset image is not valid base64.", ex);
            }

            if (!string.Equals(Convert.ToBase64String(imageBytes), encoded, StringComparison.Ordinal))
            {
                throw new GamAssetDocumentException("The embedded asset image base64 is not canonical.");
            }

            var expectedHash = GetRequiredString(image, "sha256");
            var actualHash = ComputeSha256(imageBytes);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new GamAssetDocumentException("The embedded asset image checksum does not match.");
            }

            return GamAssetDocumentImageNormalizer.NormalizePortablePng(imageBytes);
        }

        private static GamAssetDocument ValidateForWrite(GamAssetDocument document)
        {
            var name = NormalizeAndValidateName(document.Name);
            if (!Enum.IsDefined(typeof(GamAssetDocumentState), document.State))
            {
                throw new GamAssetDocumentException("The asset state is invalid.");
            }

            GamAssetDocumentMembership membership;
            switch (document.Membership.Kind)
            {
                case GamAssetDocumentMembershipKind.Fixed:
                    if (document.Membership.Rule != null || document.Membership.SnapshotAddonIds.Count != 0)
                    {
                        throw new GamAssetDocumentException("A fixed asset can contain only fixed addon IDs.");
                    }

                    membership = GamAssetDocumentMembership.Fixed(
                        ValidateAddonIds(document.Membership.AddonIds));
                    break;

                case GamAssetDocumentMembershipKind.Smart:
                    if (document.Membership.Rule == null || document.Membership.AddonIds.Count != 0)
                    {
                        throw new GamAssetDocumentException("A smart asset must contain exactly one rule.");
                    }

                    var canonicalValue = CanonicalizeRuleValue(
                        document.Membership.Rule.Kind,
                        document.Membership.Rule.Value);
                    membership = GamAssetDocumentMembership.Smart(
                        new GamAssetDocumentRule(document.Membership.Rule.Kind, canonicalValue),
                        ValidateAddonIds(document.Membership.SnapshotAddonIds));
                    break;

                default:
                    throw new GamAssetDocumentException("The asset membership kind is invalid.");
            }

            var sourceImage = document.ImageBytes;
            var image = sourceImage == null
                ? null
                : GamAssetDocumentImageNormalizer.Normalize(sourceImage);
            var memo = NormalizeAndValidateMemo(document.Memo);
            return new GamAssetDocument(
                name,
                document.State,
                membership,
                image,
                CurrentFormatVersion,
                memo);
        }

        private static IReadOnlyList<string> ParseAddonIdArray(JObject owner, string propertyName)
        {
            var token = GetRequiredToken(owner, propertyName);
            if (token.Type != JTokenType.Array)
            {
                throw new GamAssetDocumentException($"The {propertyName} entry must be an array.");
            }

            var array = (JArray)token;
            if (array.Count > MaximumAddonIdCount)
            {
                throw new GamAssetDocumentException(
                    $"The {propertyName} entry exceeds the {MaximumAddonIdCount}-ID safety limit.");
            }

            var values = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item.Type != JTokenType.String)
                {
                    throw new GamAssetDocumentException($"Every {propertyName} entry must be a string.");
                }

                values.Add(ValidateWorkshopId(item.Value<string>() ?? string.Empty));
            }

            EnsureNoDuplicates(values, propertyName);
            return values;
        }

        private static IReadOnlyList<string> ValidateAddonIds(IReadOnlyList<string> addonIds)
        {
            if (addonIds.Count > MaximumAddonIdCount)
            {
                throw new GamAssetDocumentException(
                    $"The addonIds entry exceeds the {MaximumAddonIdCount}-ID safety limit.");
            }

            var result = new List<string>(addonIds.Count);
            foreach (var addonId in addonIds)
            {
                result.Add(ValidateWorkshopId(addonId));
            }

            EnsureNoDuplicates(result, "addonIds");
            return result;
        }

        private static string ValidateWorkshopId(string addonId)
        {
            if (string.IsNullOrEmpty(addonId) ||
                addonId.Length > 20 ||
                addonId[0] == '0')
            {
                throw new GamAssetDocumentException(
                    $"Invalid Workshop addon ID '{addonId ?? string.Empty}'.");
            }

            foreach (var character in addonId)
            {
                if (character < '0' || character > '9')
                {
                    throw new GamAssetDocumentException($"Invalid Workshop addon ID '{addonId}'.");
                }
            }

            if (!ulong.TryParse(
                    addonId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                parsed == 0)
            {
                throw new GamAssetDocumentException($"Invalid Workshop addon ID '{addonId}'.");
            }

            return addonId;
        }

        private static void EnsureNoDuplicates(IEnumerable<string> addonIds, string propertyName)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addonId in addonIds)
            {
                if (!seen.Add(addonId))
                {
                    throw new GamAssetDocumentException(
                        $"The {propertyName} entry contains duplicate addon ID '{addonId}'.");
                }
            }
        }

        private static IReadOnlyList<string> DeduplicatePreservingOrder(IEnumerable<string> addonIds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var addonId in addonIds)
            {
                if (seen.Add(addonId))
                {
                    result.Add(addonId);
                }
            }

            return result;
        }

        private static string NormalizeAndValidateName(string name)
        {
            var normalized = name.Trim();
            if (normalized.Length == 0 || normalized.Length > MaximumAssetNameLength)
            {
                throw new GamAssetDocumentException("The asset name is empty or too long.");
            }

            if (normalized.Any(char.IsControl))
            {
                throw new GamAssetDocumentException("The asset name contains control characters.");
            }

            return normalized;
        }

        internal static string? NormalizeAndValidateMemo(string? memo)
        {
            if (memo == null)
            {
                return null;
            }

            var normalized = memo.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }
            if (normalized.Length > MaximumMemoLength)
            {
                throw new GamAssetDocumentException("The Memo is too long.");
            }
            if (normalized.Any(character =>
                    char.IsControl(character) && character != '\n' && character != '\t'))
            {
                throw new GamAssetDocumentException("The Memo contains unsupported control characters.");
            }

            return normalized;
        }

        private static string? ParseOptionalMemo(JObject owner, string propertyName)
        {
            var token = owner[propertyName];
            if (token == null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new GamAssetDocumentException(
                    $"The .gam field '{propertyName}' must be a string.");
            }

            return NormalizeAndValidateMemo(token.Value<string>());
        }

        private static GamAssetDocumentState ParseState(string state)
        {
            return state switch
            {
                "enabled" => GamAssetDocumentState.Enabled,
                "disabled" => GamAssetDocumentState.Disabled,
                "excluded" => GamAssetDocumentState.Excluded,
                _ => throw new GamAssetDocumentException("The .gam asset state is invalid.")
            };
        }

        private static string StateToWireValue(GamAssetDocumentState state)
        {
            return state switch
            {
                GamAssetDocumentState.Enabled => "enabled",
                GamAssetDocumentState.Disabled => "disabled",
                GamAssetDocumentState.Excluded => "excluded",
                _ => throw new GamAssetDocumentException("The asset state is invalid.")
            };
        }

        private static JArray ToJsonArray(IEnumerable<string> values)
        {
            return new JArray(values.Select(value => new JValue(value)));
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void RequireOnlyProperties(JObject value, params string[] allowedNames)
        {
            var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
            foreach (var property in value.Properties())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new GamAssetDocumentException(
                        $"The .gam document contains unsupported field '{property.Name}'.");
                }
            }
        }

        private static JToken GetRequiredToken(JObject owner, string propertyName)
        {
            var token = owner[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new GamAssetDocumentException(
                    $"The .gam document is missing required field '{propertyName}'.");
            }

            return token;
        }

        private static string GetRequiredString(JObject owner, string propertyName)
        {
            var token = GetRequiredToken(owner, propertyName);
            if (token.Type != JTokenType.String)
            {
                throw new GamAssetDocumentException(
                    $"The .gam field '{propertyName}' must be a string.");
            }

            return token.Value<string>() ?? string.Empty;
        }

        private static JObject GetRequiredObject(JObject owner, string propertyName)
        {
            var token = GetRequiredToken(owner, propertyName);
            if (token.Type != JTokenType.Object)
            {
                throw new GamAssetDocumentException(
                    $"The .gam field '{propertyName}' must be an object.");
            }

            return (JObject)token;
        }
    }

    internal sealed class GamAssetStrictJsonTextReader : TextReader
    {
        private readonly TextReader inner;
        private readonly CancellationToken cancellationToken;
        private bool inString;
        private bool escaped;
        private char lastSignificantCharacter;

        public GamAssetStrictJsonTextReader(
            TextReader inner,
            CancellationToken cancellationToken = default)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.cancellationToken = cancellationToken;
        }

        public override int Peek()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Peek();
        }

        public override int Read()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = inner.Read();
            if (value >= 0)
            {
                ValidateCharacter((char)value);
            }

            return value;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, index, count);
            for (var offset = 0; offset < read; offset++)
            {
                if ((offset & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ValidateCharacter(buffer[index + offset]);
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ValidateCharacter(char character)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                    lastSignificantCharacter = character;
                }

                return;
            }

            if (character == '"')
            {
                inString = true;
                return;
            }

            if (character == ' ' || character == '\t' || character == '\r' || character == '\n')
            {
                return;
            }

            if (char.IsWhiteSpace(character))
            {
                throw new GamAssetDocumentException(
                    "The JSON .gam document contains non-standard whitespace.");
            }

            if (character == '/' || character == '\'')
            {
                throw new GamAssetDocumentException(
                    "The JSON .gam document contains non-standard JSON syntax.");
            }

            if ((character == '}' || character == ']') && lastSignificantCharacter == ',')
            {
                throw new GamAssetDocumentException(
                    "The JSON .gam document cannot contain a trailing comma.");
            }

            if (character == ':' && lastSignificantCharacter != '"')
            {
                throw new GamAssetDocumentException(
                    "The JSON .gam document property names must use double quotes.");
            }

            lastSignificantCharacter = character;
        }
    }
}
