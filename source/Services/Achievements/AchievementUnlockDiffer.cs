using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// One still-locked achievement whose provider-reported progress advanced between two cache
    /// snapshots, as produced by <see cref="AchievementUnlockDiffer.DiffProgressAdvances"/>.
    /// </summary>
    internal sealed class AchievementProgressAdvance
    {
        public AchievementProgressAdvance(
            AchievementDetail achievement,
            int? previous,
            int current,
            int denominator)
        {
            Achievement = achievement;
            Previous = previous;
            Current = current;
            Denominator = denominator;
        }

        public AchievementDetail Achievement { get; }

        public string ApiName => Achievement?.ApiName;

        /// <summary>Numerator in the earlier snapshot; null when it carried none.</summary>
        public int? Previous { get; }

        /// <summary>Numerator in the later snapshot (always below <see cref="Denominator"/>).</summary>
        public int Current { get; }

        public int Denominator { get; }
    }

    internal sealed class AchievementUnlockDiffer
    {
        public IReadOnlyList<AchievementDetail> DiffUserUnlocks(
            GameAchievementData before,
            GameAchievementData after)
        {
            if (after?.Achievements == null || after.Achievements.Count == 0)
            {
                return Array.Empty<AchievementDetail>();
            }

            var beforeByKey = BuildUserLookup(before?.Achievements);
            var result = new List<AchievementDetail>();
            foreach (var current in after.Achievements.Where(a => a?.Unlocked == true))
            {
                var key = GetAchievementKey(current.ApiName, current.DisplayName);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                // Only a lock-to-unlock transition counts. An achievement already unlocked in the
                // baseline stays out even when its timestamp moves, because sources disagree on the
                // unlock instant: a local stats file records the moment, a scraped page a coarser
                // rendered time. Treating that shift as new re-announces the whole set.
                beforeByKey.TryGetValue(key, out var previous);
                if (previous == null || previous.Unlocked != true)
                {
                    result.Add(current);
                }
            }

            // Time ascending with the provider-ordered input as the stable tie. The sole caller
            // projects this to a key set; InGameUnlockEmissionOrder owns the real user emission
            // order, which this matches for unhydrated details.
            return result
                .OrderBy(a => NormalizeUnlockTime(a.UnlockTimeUtc) ?? DateTime.MaxValue)
                .ToList();
        }

        /// <summary>
        /// Locked achievements whose provider-reported progress numerator rose between the two
        /// snapshots, in the after-list (provider) order. Excluded on purpose: anything unlocked in
        /// <paramref name="after"/>, anything at or past its denominator, and single-step
        /// denominators — reaching the target is the provider's unlock to announce, never an
        /// increment. A null previous numerator counts as an advance when the current one is
        /// positive; the caller decides whether the session baseline allows announcing it.
        /// </summary>
        public IReadOnlyList<AchievementProgressAdvance> DiffProgressAdvances(
            GameAchievementData before,
            GameAchievementData after)
        {
            if (after?.Achievements == null || after.Achievements.Count == 0)
            {
                return Array.Empty<AchievementProgressAdvance>();
            }

            var beforeByKey = BuildUserLookup(before?.Achievements);
            var result = new List<AchievementProgressAdvance>();
            foreach (var current in after.Achievements)
            {
                if (current == null ||
                    current.Unlocked == true ||
                    !current.ProgressNum.HasValue ||
                    !current.ProgressDenom.HasValue)
                {
                    continue;
                }

                var numerator = current.ProgressNum.Value;
                var denominator = current.ProgressDenom.Value;
                if (denominator <= 1 || numerator <= 0 || numerator >= denominator)
                {
                    continue;
                }

                var key = GetAchievementKey(current.ApiName, current.DisplayName);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                beforeByKey.TryGetValue(key, out var previous);
                var previousNumerator = previous?.ProgressNum;
                if (previousNumerator.HasValue && previousNumerator.Value >= numerator)
                {
                    continue;
                }

                result.Add(new AchievementProgressAdvance(current, previousNumerator, numerator, denominator));
            }

            return result;
        }

        public IReadOnlyList<FriendAchievementRow> DiffFriendSessionUnlocks(
            IEnumerable<FriendAchievementRow> rows,
            DateTime sessionStartUtc,
            ISet<string> alreadyToastedKeys)
        {
            var result = new List<FriendAchievementRow>();
            foreach (var row in rows ?? Enumerable.Empty<FriendAchievementRow>())
            {
                if (row?.Unlocked != true)
                {
                    continue;
                }

                var key = GetAchievementKey(row.ApiName, row.DisplayName);
                if (string.IsNullOrEmpty(key) || alreadyToastedKeys?.Contains(key) == true)
                {
                    continue;
                }

                var unlockTime = NormalizeUnlockTime(row.UnlockTimeUtc);
                if (!unlockTime.HasValue || unlockTime.Value < sessionStartUtc)
                {
                    continue;
                }

                result.Add(row);
                alreadyToastedKeys?.Add(key);
            }

            // Rows arrive provider-ordered (definition rowid), so the input index is provider
            // order: same-timestamp friend unlocks notify in provider order, matching the user
            // emission order. Friend rows cannot resolve the user's per-game custom order.
            return result
                .Select((row, index) => (row, index))
                .OrderBy(entry => NormalizeUnlockTime(entry.row.UnlockTimeUtc) ?? DateTime.MaxValue)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.row)
                .ToList();
        }

        public IReadOnlyList<FriendAchievementRow> DiffFriendBaselineUnlocks(
            IEnumerable<FriendAchievementRow> baselineRows,
            IEnumerable<FriendAchievementRow> currentRows,
            ISet<string> alreadyToastedKeys)
        {
            var baseline = new HashSet<string>(
                (baselineRows ?? Enumerable.Empty<FriendAchievementRow>())
                .Where(row => row?.Unlocked == true)
                .Select(row => GetAchievementKey(row.ApiName, row.DisplayName))
                .Where(key => !string.IsNullOrEmpty(key)),
                StringComparer.OrdinalIgnoreCase);

            var result = new List<FriendAchievementRow>();
            foreach (var row in currentRows ?? Enumerable.Empty<FriendAchievementRow>())
            {
                if (row?.Unlocked != true)
                {
                    continue;
                }

                var key = GetAchievementKey(row.ApiName, row.DisplayName);
                if (string.IsNullOrEmpty(key) ||
                    baseline.Contains(key) ||
                    alreadyToastedKeys?.Contains(key) == true)
                {
                    continue;
                }

                result.Add(row);
                alreadyToastedKeys?.Add(key);
            }

            // Baseline diffs carry no timestamps; rows arrive provider-ordered (definition rowid),
            // so the input order is already the emission order.
            return result;
        }

        public static string GetAchievementKey(string apiName, string displayName)
        {
            var key = !string.IsNullOrWhiteSpace(apiName) ? apiName : displayName;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        private static Dictionary<string, AchievementDetail> BuildUserLookup(IEnumerable<AchievementDetail> achievements)
        {
            var result = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                var key = GetAchievementKey(achievement?.ApiName, achievement?.DisplayName);
                if (!string.IsNullOrEmpty(key) && !result.ContainsKey(key))
                {
                    result[key] = achievement;
                }
            }

            return result;
        }

        private static DateTime? NormalizeUnlockTime(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var utc = value.Value;
            if (utc.Kind == DateTimeKind.Local)
            {
                return utc.ToUniversalTime();
            }

            return utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc;
        }
    }
}
