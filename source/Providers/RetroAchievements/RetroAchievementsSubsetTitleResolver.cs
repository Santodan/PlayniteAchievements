using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsSubsetTitleResolver
    {
        internal static string ExtractBaseTitle(string subsetTitle)
        {
            if (string.IsNullOrWhiteSpace(subsetTitle))
            {
                return null;
            }

            // Strip the first bracket-delimited suffix: "[Subset - ...]", "[Bonus]", "[Hub]", etc.
            var bracketStart = subsetTitle.IndexOf('[');
            if (bracketStart > 0)
            {
                return subsetTitle.Substring(0, bracketStart).Trim();
            }

            var parenStart = subsetTitle.IndexOf('(');
            if (parenStart > 0)
            {
                return subsetTitle.Substring(0, parenStart).Trim();
            }

            return null;
        }

        internal static IReadOnlyList<string> ExtractAlternateBaseTitleCandidates(string baseTitle)
        {
            if (string.IsNullOrWhiteSpace(baseTitle) || baseTitle.IndexOf('|') < 0)
            {
                return Array.Empty<string>();
            }

            var segments = baseTitle
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment?.Trim())
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToList();

            var candidates = new List<string>();
            foreach (var segment in segments)
            {
                AddCandidate(candidates, segment);
            }

            foreach (var segment in segments)
            {
                if (segment.IndexOf(':') >= 0)
                {
                    continue;
                }

                foreach (var prefix in ExtractColonPrefixes(segments))
                {
                    AddCandidate(candidates, $"{prefix} {segment}");
                }
            }

            return candidates;
        }

        private static IReadOnlyList<string> ExtractColonPrefixes(IReadOnlyList<string> segments)
        {
            var prefixes = new List<string>();
            foreach (var segment in segments)
            {
                var colonIndex = segment.IndexOf(':');
                if (colonIndex <= 0 || colonIndex >= segment.Length - 1)
                {
                    continue;
                }

                AddCandidate(prefixes, segment.Substring(0, colonIndex + 1).Trim());
            }

            return prefixes;
        }

        private static void AddCandidate(List<string> candidates, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var normalized = candidate.Trim();
            foreach (var existing in candidates)
            {
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            candidates.Add(normalized);
        }
    }
}
