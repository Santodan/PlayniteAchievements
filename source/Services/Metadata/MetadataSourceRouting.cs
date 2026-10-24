using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Which source serves each chosen field. Points are chosen as Xbox and trophy ranks as
    /// PlayStation; both read the same data from an Exophase page or a TrueGaming one
    /// (TrueAchievements for gamerscore, TrueTrophies for ranks). The field takes whichever page
    /// the game loads anyway: the Exophase one when another field already uses that source,
    /// otherwise TrueGaming, whose pages load faster than Exophase's.
    /// </summary>
    internal static class MetadataSourceRouting
    {
        /// <summary>
        /// <paramref name="choices"/> with points and ranks set to the source that serves them for
        /// a game <paramref name="servicingProviderKey"/> services; other fields are unchanged.
        /// </summary>
        public static MetadataChoices Route(
            MetadataChoices choices,
            IReadOnlyCollection<IMetadataProvider> providers,
            string servicingProviderKey)
        {
            if (choices == null)
            {
                return null;
            }

            var routed = choices.Clone();
            routed.Points = Serve(choices.Points, MetadataField.Points, MetadataChoices.Xbox, choices, providers, servicingProviderKey);
            routed.TrophyTypes = Serve(choices.TrophyTypes, MetadataField.TrophyTypes, MetadataChoices.PlayStation, choices, providers, servicingProviderKey);
            return routed;
        }

        /// <summary>
        /// False for a source that is never picked by itself for <paramref name="field"/>: the
        /// Exophase own-platform source (it applies on its own) and TrueGaming for points and
        /// ranks (it serves the Xbox and PlayStation choices).
        /// </summary>
        public static bool IsChoice(IMetadataProvider provider, MetadataField field)
        {
            if (provider == null || string.Equals(provider.Key, MetadataChoices.Exophase, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !(string.Equals(provider.Key, MetadataChoices.TrueGaming, StringComparison.OrdinalIgnoreCase) &&
                     (field == MetadataField.Points || field == MetadataField.TrophyTypes));
        }

        private static string Serve(
            string chosen,
            MetadataField field,
            string frontend,
            MetadataChoices choices,
            IReadOnlyCollection<IMetadataProvider> providers,
            string servicingProviderKey)
        {
            if (!string.Equals(chosen?.Trim(), frontend, StringComparison.OrdinalIgnoreCase) ||
                !Usable(providers, MetadataChoices.TrueGaming, field, servicingProviderKey))
            {
                return chosen;
            }

            // The fields other than points and ranks decide which pages the game loads anyway.
            var loaded = new[] { choices.Icons, choices.Categories, choices.Tags, choices.CapstoneArt, choices.Text };
            return loaded.Any(key => string.Equals(key?.Trim(), frontend, StringComparison.OrdinalIgnoreCase))
                ? chosen
                : MetadataChoices.TrueGaming;
        }

        private static bool Usable(IReadOnlyCollection<IMetadataProvider> providers, string key, MetadataField field, string servicingProviderKey)
        {
            var provider = providers?.FirstOrDefault(candidate => string.Equals(candidate?.Key, key, StringComparison.OrdinalIgnoreCase));
            return provider != null && (provider.SupportedFields & field) == field && !provider.IsNativeFor(servicingProviderKey);
        }
    }
}
