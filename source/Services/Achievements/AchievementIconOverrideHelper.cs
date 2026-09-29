using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
namespace PlayniteAchievements.Services.Achievements
{
    internal static class AchievementIconOverrideHelper
    {
        public static bool HasOverrides(IReadOnlyDictionary<string, string> unlockedOverrides, IReadOnlyDictionary<string, string> lockedOverrides)
        {
            return (unlockedOverrides != null && unlockedOverrides.Count > 0) ||
                   (lockedOverrides != null && lockedOverrides.Count > 0);
        }

        public static string GetOverrideValue(
            IReadOnlyDictionary<string, string> overrides,
            string apiName)
        {
            if (overrides == null)
            {
                return null;
            }

            var normalizedKey = NormalizeKey(apiName);
            if (string.IsNullOrWhiteSpace(normalizedKey) ||
                !overrides.TryGetValue(normalizedKey, out var value))
            {
                return null;
            }

            return NormalizeKey(value);
        }

        /// <summary>
        /// The locked path to persist for one achievement: an explicit locked override when there is
        /// one, otherwise the real locked icon while separate locked icons are enabled, otherwise the
        /// unlocked path.
        ///
        /// A custom *unlocked* override does not suppress the locked icon. It used to, which
        /// discarded the provider's locked path in the cache and left nothing to reveal behind a
        /// locked cover.
        /// </summary>
        public static string ResolveEffectiveLockedPath(
            string unlockedIconPath,
            string lockedIconPath,
            bool useSeparateLockedIcons,
            bool hasExplicitLockedIcon)
        {
            if (hasExplicitLockedIcon && !string.IsNullOrWhiteSpace(lockedIconPath))
            {
                return lockedIconPath;
            }

            if (!useSeparateLockedIcons)
            {
                return unlockedIconPath;
            }

            return !string.IsNullOrWhiteSpace(lockedIconPath)
                ? lockedIconPath
                : unlockedIconPath;
        }


        /// <summary>
        /// Stamps a game's stored icon overrides onto a set of items, resolving each stored value
        /// through the managed icon cache. Returns whether anything was written.
        /// </summary>
        /// <remarks>
        /// Only the entries the store holds are written: an item with no override keeps whatever
        /// icon it arrived with, which is the provider's own for freshly read rows. Re-stamping
        /// rows that are already in memory therefore shows a new or replaced override but cannot
        /// undo a removed one, so a caller that has to survive a removal rebuilds its rows instead.
        ///
        /// The hydrator and the Overview's in-place re-stamp share this so the two cannot drift on
        /// which stored value wins or how a managed path resolves.
        /// </remarks>
        public static bool ApplyOverrides<T>(
            Guid gameId,
            IEnumerable<T> items,
            ManagedCustomIconService managedCustomIconService,
            Func<T, string> readApiName,
            Action<T, string> writeUnlockedIconPath,
            Action<T, string> writeLockedIconPath)
        {
            if (items == null || readApiName == null)
            {
                return false;
            }

            var unlockedOverrides = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(gameId);
            var lockedOverrides = GameCustomDataLookup.GetAchievementLockedIconOverrides(gameId);
            if (!HasOverrides(unlockedOverrides, lockedOverrides))
            {
                return false;
            }

            var gameIdText = gameId.ToString("D");
            var applied = false;

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var apiName = NormalizeKey(readApiName(item));
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var unlockedOverride = GetOverrideValue(unlockedOverrides, apiName);
                if (writeUnlockedIconPath != null && !string.IsNullOrWhiteSpace(unlockedOverride))
                {
                    writeUnlockedIconPath(
                        item,
                        ResolveOverridePath(unlockedOverride, gameIdText, managedCustomIconService));
                    applied = true;
                }

                var lockedOverride = GetOverrideValue(lockedOverrides, apiName);
                if (writeLockedIconPath != null && !string.IsNullOrWhiteSpace(lockedOverride))
                {
                    writeLockedIconPath(
                        item,
                        ResolveOverridePath(lockedOverride, gameIdText, managedCustomIconService));
                    applied = true;
                }
            }

            return applied;
        }

        /// <summary>
        /// Turns one stored override value into the path a view can render, mapping a managed icon
        /// to its location under the plugin's icon cache.
        /// </summary>
        public static string ResolveOverridePath(
            string path,
            string gameIdText,
            ManagedCustomIconService managedCustomIconService)
        {
            var normalized = NormalizeKey(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return managedCustomIconService?.ResolveManagedDisplayPath(normalized, gameIdText) ?? normalized;
        }
        private static string NormalizeKey(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
