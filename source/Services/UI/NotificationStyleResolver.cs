using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
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
    }
}
