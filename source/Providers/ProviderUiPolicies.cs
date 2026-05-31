using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers
{
    internal static class ProviderUiPolicies
    {
        public const string ExophaseProviderKey = "Exophase";

        private const string StorefrontGroupResourceKey = "LOCPlayAch_Settings_ProviderGroup_StoresLaunchers";
        private const string ConsoleMobileGroupResourceKey = "LOCPlayAch_Settings_ProviderGroup_ConsoleMobile";
        private const string EmulatorRetroGroupResourceKey = "LOCPlayAch_Settings_ProviderGroup_EmulatorsRetro";
        private const string ManualAggregatorGroupResourceKey = "LOCPlayAch_Settings_ProviderGroup_ManualAggregators";
        private const string OtherGroupResourceKey = "LOCPlayAch_Settings_ProviderGroup_Other";

        private static readonly HashSet<string> StorefrontProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Steam",
                "Epic",
                "GOG",
                "BattleNet",
                "EA",
                "Ubisoft"
            };

        private static readonly HashSet<string> ConsoleMobileProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PSN",
                "Xbox",
                "Apple",
                "GooglePlay"
            };

        private static readonly HashSet<string> EmulatorRetroProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RetroAchievements",
                "ShadPS4",
                "Xenia",
                "RPCS3"
            };

        private static readonly HashSet<string> ManualAggregatorProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Manual",
                "Exophase",
                "Hoyoverse"
            };

        private static readonly HashSet<string> ExophaseServicedProviders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GooglePlay",
                "Apple",
                "EA",
                "Ubisoft"
            };

        public static bool ShouldHideFromSetupSurfaces(string providerKey)
        {
            return !string.IsNullOrWhiteSpace(providerKey) &&
                ExophaseServicedProviders.Contains(providerKey);
        }

        public static string GetSettingsGroupResourceKey(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return OtherGroupResourceKey;
            }

            if (StorefrontProviders.Contains(providerKey))
            {
                return StorefrontGroupResourceKey;
            }

            if (ConsoleMobileProviders.Contains(providerKey))
            {
                return ConsoleMobileGroupResourceKey;
            }

            if (EmulatorRetroProviders.Contains(providerKey))
            {
                return EmulatorRetroGroupResourceKey;
            }

            if (ManualAggregatorProviders.Contains(providerKey))
            {
                return ManualAggregatorGroupResourceKey;
            }

            return OtherGroupResourceKey;
        }

        public static bool TryGetSettingsRedirectProviderKey(string providerKey, out string redirectProviderKey)
        {
            redirectProviderKey = null;

            if (string.IsNullOrWhiteSpace(providerKey) ||
                !ExophaseServicedProviders.Contains(providerKey))
            {
                return false;
            }

            redirectProviderKey = ExophaseProviderKey;
            return true;
        }
    }
}
