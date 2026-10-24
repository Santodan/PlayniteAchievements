using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayniteAchievements.Models.Tagging
{
    using ObservableObject = Common.ObservableObject;

    /// <summary>
    /// Settings for Playnite tag integration, allowing games to be tagged
    /// based on their achievement status for filtering and organization.
    /// </summary>
    public class TaggingSettings : ObservableObject
    {
        /// <summary>
        /// Reads <see cref="TagConfigs"/> keeping only keys that name a current tag type, so a
        /// removed kind in saved settings does not stop the settings from loading.
        /// </summary>
        internal sealed class KnownTagConfigsConverter : Newtonsoft.Json.JsonConverter
        {
            public override bool CanWrite => false;

            public override bool CanConvert(Type objectType) =>
                objectType == typeof(Dictionary<TagType, TagConfig>);

            public override object ReadJson(
                Newtonsoft.Json.JsonReader reader,
                Type objectType,
                object existingValue,
                Newtonsoft.Json.JsonSerializer serializer)
            {
                if (reader.TokenType == Newtonsoft.Json.JsonToken.Null)
                {
                    return null;
                }

                var configs = new Dictionary<TagType, TagConfig>();
                foreach (var property in Newtonsoft.Json.Linq.JObject.Load(reader).Properties())
                {
                    if (Enum.TryParse(property.Name, out TagType tagType) &&
                        Enum.IsDefined(typeof(TagType), tagType))
                    {
                        configs[tagType] = property.Value.ToObject<TagConfig>(serializer);
                    }
                }

                return configs;
            }

            public override void WriteJson(
                Newtonsoft.Json.JsonWriter writer,
                object value,
                Newtonsoft.Json.JsonSerializer serializer) =>
                throw new NotSupportedException();
        }

        private bool _enableTagging = false;
        private bool _setCompletionStatus = false;
        private Guid? _completionStatusId;
        private Dictionary<TagType, TagConfig> _tagConfigs;

        /// <summary>
        /// Master switch to enable or disable tag syncing.
        /// When disabled, all PA tags will be removed from games.
        /// </summary>
        public bool EnableTagging
        {
            get => _enableTagging;
            set => SetValue(ref _enableTagging, value);
        }

        /// <summary>
        /// Whether to automatically set Playnite's completion status for completed games.
        /// </summary>
        public bool SetCompletionStatus
        {
            get => _setCompletionStatus;
            set => SetValue(ref _setCompletionStatus, value);
        }

        /// <summary>
        /// The ID of the completion status to set for completed games.
        /// If null, the default "Completed" status is used.
        /// </summary>
        public Guid? CompletionStatusId
        {
            get => _completionStatusId;
            set => SetValue(ref _completionStatusId, value);
        }

        /// <summary>
        /// Configuration for each tag type, keyed by TagType enum.
        /// Contains display name and enabled state for each tag.
        /// Saved keys that no longer name a tag type are skipped on load.
        /// </summary>
        [Newtonsoft.Json.JsonConverter(typeof(KnownTagConfigsConverter))]
        public Dictionary<TagType, TagConfig> TagConfigs
        {
            get => _tagConfigs;
            set => SetValue(ref _tagConfigs, value);
        }

        // Individual tag config properties for WPF binding convenience.
        // Not serialized: TagConfigs is the single persisted source of truth.
        [DontSerialize]
        public TagConfig HasAchievementsConfig => GetOrCreateTagConfig(TagType.HasAchievements);
        [DontSerialize]
        public TagConfig InProgressConfig => GetOrCreateTagConfig(TagType.InProgress);
        [DontSerialize]
        public TagConfig CompletedConfig => GetOrCreateTagConfig(TagType.Completed);
        [DontSerialize]
        public TagConfig NoAchievementsConfig => GetOrCreateTagConfig(TagType.NoAchievements);
        [DontSerialize]
        public TagConfig CustomizedConfig => GetOrCreateTagConfig(TagType.Customized);
        [DontSerialize]
        public TagConfig NotCustomizedConfig => GetOrCreateTagConfig(TagType.NotCustomized);
        [DontSerialize]
        public TagConfig ExcludedConfig => GetOrCreateTagConfig(TagType.Excluded);
        [DontSerialize]
        public TagConfig ExcludedFromSummariesConfig => GetOrCreateTagConfig(TagType.ExcludedFromSummaries);

        /// <summary>
        /// The status kinds as settings rows: achievements (platform, custom, none), progress and
        /// exclusion.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagRow> StatusRows => new[]
        {
            Row("LOCPlayAch_Achievements", TagType.HasAchievements, TagType.CustomAchievements, TagType.NoAchievements, null),
            Row("LOCPlayAch_Progress", TagType.InProgress, TagType.Completed, null, null),
            Row("LOCPlayAch_ManageAchievements_Status_Excluded", TagType.Excluded, TagType.ExcludedFromSummaries, null, null)
        };

        /// <summary>
        /// The customization kinds as settings rows: the Customized pair, then one row per area.
        /// Columns are fixed so each layer lines up across rows: Platform, Metadata, Custom, None.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagRow> CustomizationRows =>
            new[] { Row("LOCPlayAch_Tagging_Customized", null, null, TagType.Customized, TagType.NotCustomized) }
                .Concat(CustomizationTagAreas.All.Select(area => Row(
                    area.NounResourceKey,
                    area.Tag(TagLayer.Platform),
                    area.Tag(TagLayer.Metadata),
                    area.CustomTag,
                    area.Tag(TagLayer.None))))
                .ToList();

        private TagRow Row(string labelKey, TagType? first, TagType? second, TagType? third, TagType? fourth)
        {
            TagConfig Config(TagType? tagType) => tagType.HasValue ? GetOrCreateTagConfig(tagType.Value) : null;
            return new TagRow(labelKey, Config(first), Config(second), Config(third), Config(fourth));
        }

        private TagConfig GetOrCreateTagConfig(TagType tagType)
        {
            if (TagConfigs == null)
            {
                TagConfigs = new Dictionary<TagType, TagConfig>();
            }

            if (!TagConfigs.ContainsKey(tagType))
            {
                TagConfigs[tagType] = new TagConfig
                {
                    DisplayName = GetDefaultDisplayName(tagType),
                    IsEnabled = IsEnabledByDefault(tagType)
                };
            }

            return TagConfigs[tagType];
        }

        /// <summary>
        /// Creates a new TaggingSettings instance with default values.
        /// </summary>
        public TaggingSettings()
        {
            TagConfigs = new Dictionary<TagType, TagConfig>();
        }

        /// <summary>
        /// Initializes default tag configurations with localized display names.
        /// Should be called after the settings are loaded to ensure all tag types exist.
        /// </summary>
        /// <param name="getDefaultName">Function to get the localized default name for a tag type.</param>
        public void InitializeDefaults(Func<TagType, string> getDefaultName)
        {
            if (TagConfigs == null)
            {
                TagConfigs = new Dictionary<TagType, TagConfig>();
            }

            // Ensure all tag types have a config
            foreach (TagType tagType in System.Enum.GetValues(typeof(TagType)))
            {
                if (!TagConfigs.ContainsKey(tagType))
                {
                    TagConfigs[tagType] = new TagConfig
                    {
                        DisplayName = getDefaultName?.Invoke(tagType) ?? GetDefaultDisplayName(tagType),
                        IsEnabled = IsEnabledByDefault(tagType)
                    };
                }
                else if (string.IsNullOrWhiteSpace(TagConfigs[tagType].DisplayName))
                {
                    // Fill in missing display names
                    TagConfigs[tagType].DisplayName = getDefaultName?.Invoke(tagType) ?? GetDefaultDisplayName(tagType);
                }            }
        }

        /// <summary>
        /// Gets the hardcoded default display name for a tag type.
        /// Used as fallback when localization is not available.
        /// </summary>
        public static string GetDefaultDisplayName(TagType tagType)
        {
            return tagType switch
            {
                TagType.HasAchievements => "[PA] Platform Achievements",
                TagType.CustomAchievements => "[PA] Custom Achievements",
                TagType.InProgress => "[PA] In Progress",
                TagType.Completed => "[PA] Completed",
                TagType.NoAchievements => "[PA] No Achievements",
                TagType.Customized => "[PA] Customized",
                TagType.NotCustomized => "[PA] Not Customized",
                TagType.Excluded => "[PA] Excluded",
                TagType.ExcludedFromSummaries => "[PA] Excluded from Summaries",
                // Customization kinds have no English here: their names exist only in the locale
                // files, so a missing string shows as its key rather than as hidden English.
                _ => CustomizationTagAreas.TryGetArea(tagType, out _, out _)
                    ? $"[PA] {CustomizationTagAreas.NameResourceKey(tagType)}"
                    : $"[PA] {tagType}"
            };
        }

        /// <summary>
        /// Whether a tag kind starts enabled. The customization area kinds start disabled, for
        /// new installs and for settings saved before they existed.
        /// </summary>
        public static bool IsEnabledByDefault(TagType tagType)
        {
            return !CustomizationTagAreas.TryGetArea(tagType, out _, out _);
        }

        /// <summary>
        /// The config for a tag kind, created with its defaults when missing. For the per-area
        /// settings rows, which look kinds up by value rather than through a named property.
        /// </summary>
        public TagConfig GetConfig(TagType tagType) => GetOrCreateTagConfig(tagType);

        /// <summary>
        /// Creates a deep copy of this TaggingSettings instance.
        /// </summary>
        public TaggingSettings Clone()
        {
            var clone = new TaggingSettings
            {
                EnableTagging = EnableTagging,
                SetCompletionStatus = SetCompletionStatus,
                CompletionStatusId = CompletionStatusId
            };

            if (TagConfigs != null)
            {
                clone.TagConfigs = new Dictionary<TagType, TagConfig>();
                foreach (var kvp in TagConfigs)
                {
                    clone.TagConfigs[kvp.Key] = kvp.Value?.Clone() ?? new TagConfig
                    {
                        DisplayName = GetDefaultDisplayName(kvp.Key),
                        IsEnabled = IsEnabledByDefault(kvp.Key)
                    };
                }
            }

            return clone;
        }
    }

    /// <summary>
    /// One settings row of tag kinds that belong together, in four fixed columns. An empty
    /// column is null, so a kind keeps its column whatever the row's other kinds are.
    /// </summary>
    public sealed class TagRow
    {
        public TagRow(string labelResourceKey, TagConfig first, TagConfig second, TagConfig third, TagConfig fourth)
        {
            LabelResourceKey = labelResourceKey;
            First = first;
            Second = second;
            Third = third;
            Fourth = fourth;
        }

        public string LabelResourceKey { get; }

        public TagConfig First { get; }

        public TagConfig Second { get; }

        public TagConfig Third { get; }

        public TagConfig Fourth { get; }

        /// <summary>The row's configs, without the empty columns. A method so no serializer writes it.</summary>
        public IEnumerable<TagConfig> GetConfigs() => new[] { First, Second, Third, Fourth }.Where(config => config != null);

        /// <summary>The row label in the current language.</summary>
        public string Label => ResourceProvider.GetString(LabelResourceKey);
    }

    /// <summary>
    /// Configuration for a single tag type, including its display name and enabled state.
    /// </summary>
    public class TagConfig : ObservableObject
    {
        private string _displayName;
        private bool _isEnabled = true;
        private Guid? _tagId;

        /// <summary>
        /// The display name shown in Playnite's library.
        /// This is the actual tag name that will be applied to games.
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetValue(ref _displayName, value);
        }

        /// <summary>
        /// Whether this tag type should be synced to games.
        /// When disabled, the tag will be removed from all games.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetValue(ref _isEnabled, value);
        }

        /// <summary>
        /// The ID of the tag in Playnite's database.
        /// Tracked so we can remove tags by ID even if the name changes.
        /// </summary>
        public Guid? TagId
        {
            get => _tagId;
            set => SetValue(ref _tagId, value);
        }

        /// <summary>
        /// Creates a deep copy of this TagConfig.
        /// </summary>
        public TagConfig Clone()
        {
            return new TagConfig
            {
                DisplayName = DisplayName,
                IsEnabled = IsEnabled,
                TagId = TagId
            };
        }
    }
}
