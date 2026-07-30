using System;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class ResolvedNotificationAppearance
    {
        public NotificationStyleSettings Style { get; set; }

        public bool ToastUseThemeStyling { get; set; }

        public bool FrameUseThemeStyling { get; set; }
    }

    /// <summary>
    /// Resolves the effective notification appearance style for an unlock: the provider's
    /// whole-style copy when the platform is customized, otherwise the global default.
    /// Behavior gating (whether notifications fire at all) is ProviderNotificationPolicy's
    /// concern, not this class's.
    /// </summary>
    internal static class NotificationStyleResolver
    {
        public static NotificationStyleSettings Resolve(PersistedSettings settings, string providerKey)
        {
            return settings?.GetProviderNotificationStyle(providerKey)
                ?? settings?.NotificationStyle
                ?? NotificationStyleSettings.CreateDefault();
        }

        /// <summary>
        /// Resolves the complete appearance for one notification. A valid per-game snapshot wins;
        /// otherwise the style follows the provider/global chain and the template choices follow
        /// the current global settings.
        /// </summary>
        public static ResolvedNotificationAppearance ResolveAppearance(
            PersistedSettings settings,
            string providerKey,
            Guid playniteGameId,
            GameCustomDataStore gameCustomDataStore = null)
        {
            if (playniteGameId != Guid.Empty &&
                gameCustomDataStore?.TryLoad(playniteGameId, out var customData) == true &&
                customData?.NotificationAppearanceOverride?.Style != null)
            {
                var gameAppearance = customData.NotificationAppearanceOverride;
                return new ResolvedNotificationAppearance
                {
                    Style = gameAppearance.Style,
                    ToastUseThemeStyling = gameAppearance.ToastUseThemeStyling,
                    FrameUseThemeStyling = gameAppearance.FrameUseThemeStyling
                };
            }

            return new ResolvedNotificationAppearance
            {
                Style = Resolve(settings, providerKey),
                ToastUseThemeStyling = settings?.ToastUseThemeStyling ?? true,
                FrameUseThemeStyling = settings?.FrameUseThemeStyling ?? true
            };
        }
    }
}
