using System;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
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
