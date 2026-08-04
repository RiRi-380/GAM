using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GmodAddonManager.Core.Models
{
    [JsonConverter(typeof(AssetMembershipRuleKindJsonConverter))]
    public enum AssetMembershipRuleKind
    {
        Unknown,
        Type,
        Tag
    }

    /// <summary>
    /// Reads unknown current-schema values as Unknown so the reconciliation layer
    /// can freeze the Asset instead of making the whole profile unreadable.
    /// </summary>
    public sealed class AssetMembershipRuleKindJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) =>
            objectType == typeof(AssetMembershipRuleKind) ||
            objectType == typeof(AssetMembershipRuleKind?);

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String &&
                Enum.TryParse(
                    reader.Value?.ToString(),
                    ignoreCase: true,
                    out AssetMembershipRuleKind parsed) &&
                (parsed == AssetMembershipRuleKind.Type ||
                 parsed == AssetMembershipRuleKind.Tag))
            {
                return parsed;
            }

            if (reader.TokenType == JsonToken.Integer &&
                int.TryParse(
                    reader.Value?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var numeric) &&
                (numeric == (int)AssetMembershipRuleKind.Type ||
                 numeric == (int)AssetMembershipRuleKind.Tag))
            {
                return (AssetMembershipRuleKind)numeric;
            }

            if (reader.TokenType is JsonToken.StartArray or JsonToken.StartObject)
            {
                reader.Skip();
            }
            return AssetMembershipRuleKind.Unknown;
        }

        public override void WriteJson(
            JsonWriter writer,
            object? value,
            JsonSerializer serializer)
        {
            var kind = value is AssetMembershipRuleKind parsed
                ? parsed
                : AssetMembershipRuleKind.Unknown;
            writer.WriteValue(kind.ToString());
        }
    }

    /// <summary>
    /// A versioned, single-condition rule that owns a Smart Asset's materialized
    /// membership. Addons remains the runtime resolver input; this rule is the
    /// source used to rebuild that list on an authoritative refresh.
    /// </summary>
    public sealed class AssetMembershipRule
    {
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("kind")]
        public AssetMembershipRuleKind Kind { get; set; }

        [JsonProperty("value")]
        public string Value { get; set; } = string.Empty;

        public AssetMembershipRule()
        {
        }

        public AssetMembershipRule(AssetMembershipRuleKind kind, string value)
        {
            Kind = kind;
            Value = value ?? string.Empty;
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum SmartAssetAutomationStatus
    {
        Active,
        FrozenInvalidRule
    }

    /// <summary>
    /// Persisted fail-safe state for Smart Asset automation. Its schema is
    /// independent from the application configuration schema so future rule
    /// engines can migrate this contract without guessing from membership.
    /// </summary>
    public sealed class SmartAssetAutomationState
    {
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("status")]
        public SmartAssetAutomationStatus Status { get; set; } =
            SmartAssetAutomationStatus.Active;

        [JsonProperty("message")]
        public string? Message { get; set; }
    }
}
