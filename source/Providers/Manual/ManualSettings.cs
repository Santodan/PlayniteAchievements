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
        private bool _requireExophaseAuthentication = true;
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
        /// Gets or sets whether Exophase authentication is required for manual source operations.
        /// When disabled, Exophase manual schema fetch/search can proceed unauthenticated.
        /// </summary>
        public bool RequireExophaseAuthentication
        {
            get => _requireExophaseAuthentication;
            set => SetValue(ref _requireExophaseAuthentication, value);
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
