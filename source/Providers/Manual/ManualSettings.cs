using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Manual achievement tracking provider settings.
    /// </summary>
    public class ManualSettings : ProviderSettingsBase
    {
        private bool _manualTrackingOverrideEnabled;
        private Dictionary<Guid, ManualAchievementLink> _achievementLinks = new Dictionary<Guid, ManualAchievementLink>();

        /// <inheritdoc />
        public override string ProviderKey => "Manual";

        /// <summary>
        /// Gets or sets whether manual tracking override is enabled.
        /// </summary>
        public bool ManualTrackingOverrideEnabled
        {
            get => _manualTrackingOverrideEnabled;
            set => SetValue(ref _manualTrackingOverrideEnabled, value);
        }

        /// <summary>
        /// Manual achievement links. Key = Playnite Game ID, Value = ManualAchievementLink.
        /// Links any Playnite game to achievements from a source (e.g., Steam).
        /// </summary>
        public Dictionary<Guid, ManualAchievementLink> AchievementLinks
        {
            get => _achievementLinks;
            set => SetValue(ref _achievementLinks, value ?? new Dictionary<Guid, ManualAchievementLink>());
        }
    }
}
