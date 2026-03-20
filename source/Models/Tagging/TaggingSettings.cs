using System;
using System.Collections.Generic;

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

        // Individual tag config properties for WPF binding convenience
        public TagConfig HasAchievementsConfig => GetOrCreateTagConfig(TagType.HasAchievements);
        public TagConfig InProgressConfig => GetOrCreateTagConfig(TagType.InProgress);
        public TagConfig CompletedConfig => GetOrCreateTagConfig(TagType.Completed);
        public TagConfig NoAchievementsConfig => GetOrCreateTagConfig(TagType.NoAchievements);
        public TagConfig ExcludedConfig => GetOrCreateTagConfig(TagType.Excluded);
        public TagConfig ExcludedFromSummariesConfig => GetOrCreateTagConfig(TagType.ExcludedFromSummaries);

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
                    IsEnabled = true
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
                        IsEnabled = true
                    };
                }
                else if (string.IsNullOrWhiteSpace(TagConfigs[tagType].DisplayName))
                {
                    // Fill in missing display names
                    TagConfigs[tagType].DisplayName = getDefaultName?.Invoke(tagType) ?? GetDefaultDisplayName(tagType);
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
                TagType.Excluded => "[PA] Excluded",
                TagType.ExcludedFromSummaries => "[PA] Excluded from Summaries",
                _ => $"[PA] {tagType}"
            };
        }

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
                        IsEnabled = true
                    };
                }
            }

            return clone;
        }
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
