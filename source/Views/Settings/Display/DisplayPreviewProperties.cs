using System;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Persisted setting property names that affect the mock achievement previews shown in the
    /// Display settings sections.
    /// </summary>
    internal static class DisplayPreviewProperties
    {
        private static readonly string[] MockPreviewProperties =
        {
            nameof(PersistedSettings.ShowCompactListRarityBar),
            nameof(PersistedSettings.ShowHiddenIcon),
            nameof(PersistedSettings.ShowHiddenTitle),
            nameof(PersistedSettings.ShowHiddenDescription),
            nameof(PersistedSettings.ShowHiddenSuffix),
            nameof(PersistedSettings.ShowLockedIcon),
            nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable),
            nameof(PersistedSettings.UseUniformRarityBadges),
            nameof(PersistedSettings.RoundRarityPercentages),
            nameof(PersistedSettings.RarityColors)
        };

        public static bool AffectsMockPreviews(string propertyName)
            => MockPreviewProperties.Contains(propertyName, StringComparer.Ordinal);
    }
}
