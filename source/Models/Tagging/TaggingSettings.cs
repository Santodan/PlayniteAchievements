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
        private bool _enableFeatureSync = false;
        private bool _setCompletionStatus = false;
        private bool _completionStatusSwitchSeeded = false;
        private Guid? _completionStatusId;
        private Dictionary<TagType, TagConfig> _tagConfigs;
        private Dictionary<TagType, TagConfig> _featureConfigs;

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
        /// Master switch for Playnite feature syncing, the same way as <see cref="EnableTagging"/>
        /// for tags. When disabled, all PA features are removed from games.
        /// </summary>
        public bool EnableFeatureSync
        {
            get => _enableFeatureSync;
            set => SetValue(ref _enableFeatureSync, value);
        }

        /// <summary>
        /// Whether to automatically set Playnite's completion status for completed games.
        /// Independent of <see cref="EnableTagging"/>.
        /// </summary>
        public bool SetCompletionStatus
        {
            get => _setCompletionStatus;
            set => SetValue(ref _setCompletionStatus, value);
        }

        /// <summary>
        /// Set once <see cref="SetCompletionStatus"/> has been reconciled with the version in
        /// which it stopped depending on <see cref="EnableTagging"/>: a saved file from before
        /// then can hold the switch on with tagging off, a state the UI never showed.
        /// </summary>
        public bool CompletionStatusSwitchSeeded
        {
            get => _completionStatusSwitchSeeded;
            set => SetValue(ref _completionStatusSwitchSeeded, value);
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

        /// <summary>
        /// Configuration for each Playnite feature the plugin maintains, keyed by the same kinds
        /// as <see cref="TagConfigs"/> but independent of it: its own names, enable flags and
        /// Playnite ids (<see cref="TagConfig.TagId"/> holds the feature's id here). Only the
        /// kinds in <see cref="FeatureKinds"/> are used.
        /// </summary>
        [Newtonsoft.Json.JsonConverter(typeof(KnownTagConfigsConverter))]
        public Dictionary<TagType, TagConfig> FeatureConfigs
        {
            get => _featureConfigs;
            set => SetValue(ref _featureConfigs, value);
        }

        /// <summary>The kinds feature sync covers: where a game's achievements come from.</summary>
        public static readonly IReadOnlyList<TagType> FeatureKinds = new[]
        {
            TagType.HasAchievements,
            TagType.CustomAchievements,
            TagType.ManualAchievements
        };

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
        /// The status kinds as settings rows: achievements (platform, custom, manual, none),
        /// progress and exclusion.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagRow> StatusRows => new[]
        {
            Row("LOCPlayAch_Achievements", TagType.HasAchievements, TagType.CustomAchievements, TagType.ManualAchievements, TagType.NoAchievements),
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

        /// <summary>
        /// The feature kinds as one settings row, over <see cref="FeatureConfigs"/>: platform,
        /// custom and manual achievements.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagRow> FeatureStatusRows => new[]
        {
            Row(FeatureConfigsOrNew(), "LOCPlayAch_Achievements", TagType.HasAchievements, TagType.CustomAchievements, TagType.ManualAchievements, null)
        };

        private TagRow Row(string labelKey, TagType? first, TagType? second, TagType? third, TagType? fourth)
        {
            return Row(TagConfigsOrNew(), labelKey, first, second, third, fourth);
        }

        private static TagRow Row(
            Dictionary<TagType, TagConfig> configs,
            string labelKey,
            TagType? first,
            TagType? second,
            TagType? third,
            TagType? fourth)
        {
            TagConfig Config(TagType? tagType) => tagType.HasValue ? GetOrCreateConfig(configs, tagType.Value) : null;
            return new TagRow(labelKey, Config(first), Config(second), Config(third), Config(fourth));
        }

        private TagConfig GetOrCreateTagConfig(TagType tagType) => GetOrCreateConfig(TagConfigsOrNew(), tagType);

        private Dictionary<TagType, TagConfig> TagConfigsOrNew() =>
            TagConfigs ?? (TagConfigs = new Dictionary<TagType, TagConfig>());

        private Dictionary<TagType, TagConfig> FeatureConfigsOrNew() =>
            FeatureConfigs ?? (FeatureConfigs = new Dictionary<TagType, TagConfig>());

        private static TagConfig GetOrCreateConfig(Dictionary<TagType, TagConfig> configs, TagType tagType)
        {
            if (!configs.TryGetValue(tagType, out var config) || config == null)
            {
                config = new TagConfig
                {
                    DisplayName = GetDefaultDisplayName(tagType),
                    DefaultDisplayName = GetDefaultDisplayName(tagType),
                    IsEnabled = IsEnabledByDefault(tagType)
                };
                configs[tagType] = config;
            }

            return config;
        }

        /// <summary>
        /// Creates a new TaggingSettings instance with default values.
        /// </summary>
        public TaggingSettings()
        {
            TagConfigs = new Dictionary<TagType, TagConfig>();
            FeatureConfigs = new Dictionary<TagType, TagConfig>();
        }

        /// <summary>
        /// Initializes default tag and feature configurations with localized display names.
        /// Should be called after the settings are loaded to ensure all kinds exist.
        /// </summary>
        /// <param name="getDefaultName">Function to get the localized default name for a kind.</param>
        public void InitializeDefaults(Func<TagType, string> getDefaultName)
        {
            FillDefaults(TagConfigsOrNew(), (TagType[])System.Enum.GetValues(typeof(TagType)), getDefaultName);
            FillDefaults(FeatureConfigsOrNew(), FeatureKinds, getDefaultName);
        }

        /// <summary>
        /// Gives every kind in <paramref name="kinds"/> a config and sets each config's
        /// <see cref="TagConfig.DefaultDisplayName"/>. Existing names, blank ones included, and
        /// enable flags are kept: a blank name stands for the default.
        /// </summary>
        private static void FillDefaults(
            Dictionary<TagType, TagConfig> configs,
            IEnumerable<TagType> kinds,
            Func<TagType, string> getDefaultName)
        {
            foreach (var tagType in kinds)
            {
                var defaultName = getDefaultName?.Invoke(tagType) ?? GetDefaultDisplayName(tagType);
                if (!configs.TryGetValue(tagType, out var config) || config == null)
                {
                    configs[tagType] = new TagConfig
                    {
                        DisplayName = defaultName,
                        DefaultDisplayName = defaultName,
                        IsEnabled = IsEnabledByDefault(tagType)
                    };
                }
                else
                {
                    config.DefaultDisplayName = defaultName;
                }
            }
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
                TagType.ManualAchievements => "[PA] Custom Tracking",
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

        /// <summary>The feature config for a kind, created with its defaults when missing.</summary>
        public TagConfig GetFeatureConfig(TagType tagType) => GetOrCreateConfig(FeatureConfigsOrNew(), tagType);

        /// <summary>
        /// Creates a deep copy of this TaggingSettings instance.
        /// </summary>
        public TaggingSettings Clone()
        {
            return new TaggingSettings
            {
                EnableTagging = EnableTagging,
                EnableFeatureSync = EnableFeatureSync,
                SetCompletionStatus = SetCompletionStatus,
                CompletionStatusSwitchSeeded = CompletionStatusSwitchSeeded,
                CompletionStatusId = CompletionStatusId,
                TagConfigs = CloneConfigs(TagConfigs),
                FeatureConfigs = CloneConfigs(FeatureConfigs)
            };
        }

        private static Dictionary<TagType, TagConfig> CloneConfigs(Dictionary<TagType, TagConfig> configs)
        {
            var clone = new Dictionary<TagType, TagConfig>();
            if (configs == null)
            {
                return clone;
            }

            foreach (var kvp in configs)
            {
                clone[kvp.Key] = kvp.Value?.Clone() ?? new TagConfig
                {
                    DisplayName = GetDefaultDisplayName(kvp.Key),
                    DefaultDisplayName = GetDefaultDisplayName(kvp.Key),
                    IsEnabled = IsEnabledByDefault(kvp.Key)
                };
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
        private string _defaultDisplayName;
        private bool _isEnabled = true;
        private Guid? _tagId;

        /// <summary>
        /// The name the user typed. Blank stands for <see cref="DefaultDisplayName"/>; the name
        /// written to Playnite is <see cref="ResolvedName"/>.
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set
            {
                SetValue(ref _displayName, value);
                OnPropertyChanged(nameof(ResolvedName));
            }
        }

        /// <summary>
        /// The kind's default name in the current language, shown behind a blank name box.
        /// Set on load, not saved.
        /// </summary>
        [DontSerialize]
        public string DefaultDisplayName
        {
            get => _defaultDisplayName;
            set
            {
                SetValue(ref _defaultDisplayName, value);
                OnPropertyChanged(nameof(ResolvedName));
            }
        }

        /// <summary>
        /// The name applied in Playnite's library: <see cref="DisplayName"/>, or
        /// <see cref="DefaultDisplayName"/> when that is blank.
        /// </summary>
        [DontSerialize]
        public string ResolvedName => string.IsNullOrWhiteSpace(DisplayName) ? DefaultDisplayName : DisplayName;

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
                DefaultDisplayName = DefaultDisplayName,
                IsEnabled = IsEnabled,
                TagId = TagId
            };
        }
    }
}
