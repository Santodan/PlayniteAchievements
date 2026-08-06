using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Resolves the display platform provider key for a manual link, preferring the user's
    /// choice over the platform derived from the source game id. Shared by
    /// <see cref="ManualAchievementsProvider"/> (which writes the cache during refresh) and the
    /// manual-tracking view model (which writes it right after a save) so both agree.
    ///
    /// The override is restricted to registered provider keys, so the resulting key always
    /// resolves to a localized name, icon, and color through ProviderRegistry.
    /// </summary>
    public static class ManualDisplayPlatformResolver
    {
        /// <summary>
        /// Returns the provider key a manually tracked game displays as. Null when neither the
        /// override nor the source resolves a platform; the game then displays as Manual.
        /// </summary>
        public static string Resolve(IManualSource source, ManualAchievementLink link)
        {
            if (link == null)
            {
                return null;
            }

            var chosen = NormalizeOverride(link.DisplayPlatformKeyOverride);
            if (chosen != null)
            {
                return chosen;
            }

            return source?.ResolveProviderPlatformKey(link.SourceGameId);
        }

        /// <summary>
        /// Returns the stored override when it still names a selectable platform, otherwise null
        /// so the caller falls back to source-derived detection. This keeps a link written against
        /// a provider that has since been removed from displaying an unresolvable icon.
        ///
        /// Fails open: with no registry to check against, the stored value is trusted rather than
        /// discarded, since it was constrained to a registered key when the user chose it.
        /// </summary>
        public static string NormalizeOverride(string providerKey)
        {
            var trimmed = providerKey?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            var selectable = GetSelectablePlatformKeys();
            if (selectable.Count == 0)
            {
                return trimmed;
            }

            return selectable.Any(key => string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase))
                ? trimmed
                : null;
        }

        /// <summary>
        /// Gets whether a provider key may be chosen as a manual display platform.
        /// </summary>
        public static bool IsSelectablePlatformKey(string providerKey)
            => !string.IsNullOrWhiteSpace(providerKey) &&
               GetSelectablePlatformKeys().Any(key => string.Equals(key, providerKey, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Gets the provider keys offered as manual display platforms, in registry display order.
        /// </summary>
        public static IReadOnlyList<string> GetSelectablePlatformKeys()
            => ProviderRegistry.Instance?
                   .GetAllProviders()
                   .Select(provider => provider?.ProviderKey)
                   .Where(key => !string.IsNullOrWhiteSpace(key))
                   .ToList()
               ?? new List<string>();
    }
}
