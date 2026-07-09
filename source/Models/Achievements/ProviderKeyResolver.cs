using System;

namespace PlayniteAchievements.Models.Achievements
{
    internal static class ProviderKeyResolver
    {
        public static string ResolveEffectiveProviderKey(string providerKey, string providerPlatformKey, string fallback = null)
        {
            var normalizedProviderKey = Normalize(providerKey);
            var normalizedPlatformKey = Normalize(providerPlatformKey);

            if (IsMeaningfulProviderPlatformKey(normalizedPlatformKey))
            {
                return normalizedPlatformKey;
            }

            if (!string.IsNullOrWhiteSpace(normalizedProviderKey))
            {
                return normalizedProviderKey;
            }

            return fallback;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool IsMeaningfulProviderPlatformKey(string providerPlatformKey)
        {
            return !string.IsNullOrWhiteSpace(providerPlatformKey) &&
                !providerPlatformKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        }
    }
}
