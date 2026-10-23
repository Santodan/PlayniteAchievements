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
        /// </summary>
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
        /// The status kinds as paired settings rows: achievements, progress and exclusion.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagPairRow> StatusRows => new[]
        {
            Pair("LOCPlayAch_Achievements", TagType.HasAchievements, TagType.NoAchievements),
            Pair("LOCPlayAch_Progress", TagType.InProgress, TagType.Completed),
            Pair("LOCPlayAch_ManageAchievements_Status_Excluded", TagType.Excluded, TagType.ExcludedFromSummaries)
        };

        /// <summary>
        /// The customization kinds as settings rows: the Customized pair, then one row per area.
        /// </summary>
        [DontSerialize]
        public IReadOnlyList<TagPairRow> CustomizationRows =>
            new[] { Pair("LOCPlayAch_Tagging_Customized", TagType.Customized, TagType.NotCustomized) }
                .Concat(CustomizationTagAreas.All.Select(area => Pair(area.NounResourceKey, area.CustomTag, area.DefaultTag)))
                .ToList();

        private TagPairRow Pair(string labelKey, TagType first, TagType? second)
        {
            return new TagPairRow(
                labelKey,
                GetOrCreateTagConfig(first),
                second.HasValue ? GetOrCreateTagConfig(second.Value) : null);
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
                }

                // A retired kind saved as enabled is turned off, so the next sync removes its tag.
                if (CustomizationTagAreas.Retired.Contains(tagType) && TagConfigs[tagType] != null)
                {
                    TagConfigs[tagType].IsEnabled = false;
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
                TagType.HasAchievements => "[PA] Has Achievements",
                TagType.InProgress => "[PA] In Progress",
                TagType.Completed => "[PA] Completed",
                TagType.NoAchievements => "[PA] No Achievements",
                TagType.Customized => "[PA] Customized",
                TagType.NotCustomized => "[PA] Not Customized",
                TagType.Excluded => "[PA] Excluded",
                TagType.ExcludedFromSummaries => "[PA] Excluded from Summaries",
                TagType.DefaultSource => "[PA] Default Source",
                TagType.DefaultManualTracking => "[PA] Default Manual Tracking",
                _ => !CustomizationTagAreas.TryGetArea(tagType, out var area, out var isCustom)
                    ? $"[PA] {tagType}"
                    : isCustom && area.CustomEnglishName != null
                        ? $"[PA] {area.CustomEnglishName}"
                        : $"[PA] {(isCustom ? "Custom" : "Default")} {area.EnglishNoun}"
            };
        }

        /// <summary>
        /// Whether a tag kind starts enabled. The customization area kinds start disabled, for
        /// new installs and for settings saved before they existed; retired kinds never enable.
        /// </summary>
        public static bool IsEnabledByDefault(TagType tagType)
        {
            return !CustomizationTagAreas.TryGetArea(tagType, out _, out _) &&
                   !CustomizationTagAreas.Retired.Contains(tagType);
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
    /// One settings row of tag kinds that belong together, such as a custom tag and its default.
    /// </summary>
    public sealed class TagPairRow
    {
        public TagPairRow(string labelResourceKey, TagConfig first, TagConfig second)
        {
            LabelResourceKey = labelResourceKey;
            First = first;
            Second = second;
        }

        public string LabelResourceKey { get; }

        public TagConfig First { get; }

        /// <summary>Null for a one-sided row.</summary>
        public TagConfig Second { get; }

        public bool HasSecond => Second != null;

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
