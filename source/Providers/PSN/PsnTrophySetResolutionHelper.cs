using PlayniteAchievements.Providers.PSN.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// A trophy set resolved for a Playnite game. Compilations (e.g. Spyro Reignited Trilogy,
    /// Crash Bandicoot N.Sane Trilogy) ship one trophy set per included game under a single SKU,
    /// so one game can resolve to several sets.
    /// </summary>
    internal sealed class PsnResolvedTrophySet
    {
        public string NpCommunicationId { get; set; }

        public string Title { get; set; }

        public string IconUrl { get; set; }
    }

    // Pure trophy-set resolution logic kept separate from PsnScanner so it is unit-testable
    // without the scanner's HTTP/session dependencies.
    internal static class PsnTrophySetResolutionHelper
    {
        private static readonly char[] OverrideSeparators = { '+', ',' };

        /// <summary>
        /// Extracts every trophy set of the first title container from an npTitleId lookup
        /// response, preserving API order and dropping blank or duplicate npCommunicationIds.
        /// </summary>
        internal static IReadOnlyList<PsnResolvedTrophySet> ExtractSets(PsnTrophyTitleLookup lookup)
        {
            var sets = new List<PsnResolvedTrophySet>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = lookup?.Titles?.FirstOrDefault()?.TrophyTitles
                ?? Enumerable.Empty<PsnTrophyTitleEntry>();
            foreach (var entry in entries)
            {
                var id = entry?.NpCommunicationId?.Trim();
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                sets.Add(new PsnResolvedTrophySet
                {
                    NpCommunicationId = id,
                    Title = string.IsNullOrWhiteSpace(entry.TrophyTitleName) ? null : entry.TrophyTitleName.Trim(),
                    IconUrl = string.IsNullOrWhiteSpace(entry.TrophyTitleIconUrl) ? null : entry.TrophyTitleIconUrl.Trim()
                });
            }

            return sets;
        }

        /// <summary>
        /// Parses a per-game override into trophy sets. Accepts a single NPWR id or several
        /// joined with '+' or ','. Any invalid token invalidates the whole value (empty result),
        /// matching the override dialog's validation.
        /// </summary>
        internal static IReadOnlyList<PsnResolvedTrophySet> ParseOverrideSets(string value)
        {
            var sets = new List<PsnResolvedTrophySet>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return sets;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in value.Split(OverrideSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (!PsnNpCommIdHelper.TryNormalize(token, out var normalized))
                {
                    return new List<PsnResolvedTrophySet>();
                }

                if (seen.Add(normalized))
                {
                    sets.Add(new PsnResolvedTrophySet { NpCommunicationId = normalized });
                }
            }

            return sets;
        }

        /// <summary>
        /// The canonical stored form of an override: normalized ids in the user's order,
        /// joined with '+'.
        /// </summary>
        internal static string BuildCanonicalOverrideValue(IEnumerable<PsnResolvedTrophySet> sets)
        {
            return string.Join(
                "+",
                (sets ?? Enumerable.Empty<PsnResolvedTrophySet>())
                    .Where(set => !string.IsNullOrWhiteSpace(set?.NpCommunicationId))
                    .Select(set => set.NpCommunicationId));
        }

        /// <summary>
        /// Builds the stored achievement key. Collections prefix each set's npCommunicationId so
        /// keys stay unique across sets; single-set games keep the bare trophy key so existing
        /// cached rows and ApiName-keyed user data are unaffected.
        /// </summary>
        internal static string BuildApiName(bool isCollection, string npCommId, string trophyKey)
        {
            return isCollection ? $"{npCommId}:{trophyKey}" : trophyKey;
        }

        /// <summary>
        /// Builds the provider source identity for the game so the refresh pipeline can detect
        /// match changes and overwrite stale cached icons.
        /// </summary>
        internal static string BuildProviderGameKey(IEnumerable<PsnResolvedTrophySet> sets)
        {
            return string.Join(
                "+",
                (sets ?? Enumerable.Empty<PsnResolvedTrophySet>())
                    .Where(set => !string.IsNullOrWhiteSpace(set?.NpCommunicationId))
                    .Select(set => set.NpCommunicationId)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        }
    }
}
