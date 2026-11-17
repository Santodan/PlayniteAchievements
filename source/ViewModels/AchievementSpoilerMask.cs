using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The hidden and locked display settings that decide what a locked achievement shows, for
    /// windows that list achievements outside the achievement grids.
    /// </summary>
    public sealed class SpoilerSettings
    {
        public bool ShowHiddenIcon { get; set; }
        public bool ShowHiddenTitle { get; set; }
        public bool ShowHiddenDescription { get; set; }
        public bool ShowLockedIcon { get; set; } = true;
        public bool ShowLockedTitle { get; set; } = true;
        public bool ShowLockedDescription { get; set; } = true;

        /// <summary>Everything shown; for callers without settings, such as tests.</summary>
        public static SpoilerSettings ShowAll => new SpoilerSettings
        {
            ShowHiddenIcon = true,
            ShowHiddenTitle = true,
            ShowHiddenDescription = true
        };

        public static SpoilerSettings From(PersistedSettings persisted) => new SpoilerSettings
        {
            ShowHiddenIcon = persisted?.ShowHiddenIcon ?? false,
            ShowHiddenTitle = persisted?.ShowHiddenTitle ?? false,
            ShowHiddenDescription = persisted?.ShowHiddenDescription ?? false,
            ShowLockedIcon = persisted?.ShowLockedIcon ?? true,
            ShowLockedTitle = persisted?.ShowLockedTitle ?? true,
            ShowLockedDescription = persisted?.ShowLockedDescription ?? true
        };
    }

    /// <summary>
    /// What a locked achievement shows under the hidden and locked display settings, unrevealed:
    /// the same rules as the achievement grids' rows (AchievementDisplayItem), for name,
    /// description and icon only.
    /// </summary>
    public sealed class AchievementSpoilerMask
    {
        private AchievementSpoilerMask(string name, string description, string icon, bool isTitleMasked, bool isMasked)
        {
            Name = name;
            Description = description;
            Icon = icon;
            IsTitleMasked = isTitleMasked;
            IsMasked = isMasked;
        }

        public string Name { get; }

        public string Description { get; }

        public string Icon { get; }

        public bool IsTitleMasked { get; }

        /// <summary>Whether anything about the achievement is masked: its name, description or icon.</summary>
        public bool IsMasked { get; }

        public static AchievementSpoilerMask Apply(AchievementDetail achievement, SpoilerSettings settings, System.Func<string, string> localize)
        {
            settings = settings ?? SpoilerSettings.ShowAll;
            var name = string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName;
            var locked = !achievement.Unlocked;
            var hidden = achievement.Hidden;

            var titleMasked = locked && (!settings.ShowLockedTitle || (hidden && !settings.ShowHiddenTitle));
            var descriptionMasked = locked && (!settings.ShowLockedDescription || (hidden && !settings.ShowHiddenDescription));
            var iconHidden = locked && hidden && !settings.ShowHiddenIcon;
            var lockedIconHidden = locked && !settings.ShowLockedIcon;
            var icon = AchievementIconResolver.ResolveRowDisplayIcon(
                isIconHidden: iconHidden,
                isLockedIconHidden: lockedIconHidden,
                unlocked: !locked,
                unlockedIconPath: achievement.UnlockedIconPath,
                lockedIconPath: achievement.LockedIconPath);

            return new AchievementSpoilerMask(
                titleMasked
                    ? localize(hidden ? "LOCPlayAch_Achievements_HiddenTitle" : "LOCPlayAch_Common_Locked")
                    : name,
                descriptionMasked ? null : achievement.Description,
                icon,
                titleMasked,
                titleMasked || descriptionMasked || iconHidden || lockedIconHidden);
        }
    }
}
