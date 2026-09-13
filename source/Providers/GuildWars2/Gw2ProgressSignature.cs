using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// The part of one account achievement entry that can change between polls. Comparing these
    /// rather than the whole payload is what lets a refresh decide it has nothing to do.
    /// </summary>
    internal readonly struct Gw2ProgressSignature : IEquatable<Gw2ProgressSignature>
    {
        public Gw2ProgressSignature(int current, int repeated, bool done)
        {
            Current = current;
            Repeated = repeated;
            Done = done;
        }

        public int Current { get; }

        public int Repeated { get; }

        public bool Done { get; }

        public bool Equals(Gw2ProgressSignature other)
            => Current == other.Current && Repeated == other.Repeated && Done == other.Done;

        public override bool Equals(object obj)
            => obj is Gw2ProgressSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Current;
                hash = (hash * 397) ^ Repeated;
                hash = (hash * 397) ^ (Done ? 1 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Builds and diffs snapshots of an account's achievement progress.
    ///
    /// The Guild Wars 2 account endpoint returns progress only - no names, icons or tiers - so a
    /// poll can be reduced to "did any of these three numbers move?". Nothing here touches
    /// definitions, which is what keeps the in-game path cheap.
    /// </summary>
    internal static class Gw2ProgressSnapshot
    {
        /// <summary>
        /// Indexes an account response by achievement id. A repeated id keeps the last entry, which
        /// matches how the progress index is built for the full refresh.
        /// </summary>
        public static Dictionary<int, Gw2ProgressSignature> Build(
            IReadOnlyList<Gw2AccountAchievement> entries)
        {
            var snapshot = new Dictionary<int, Gw2ProgressSignature>();
            if (entries == null)
            {
                return snapshot;
            }

            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                snapshot[entry.Id] = new Gw2ProgressSignature(
                    entry.Current ?? 0,
                    entry.Repeated ?? 0,
                    entry.Done);
            }

            return snapshot;
        }

        /// <summary>
        /// True when two snapshots describe the same progress. A null snapshot is never equivalent,
        /// so the first poll of a session always does the full work.
        /// </summary>
        public static bool AreEquivalent(
            Dictionary<int, Gw2ProgressSignature> previous,
            Dictionary<int, Gw2ProgressSignature> current)
        {
            if (previous == null || current == null || previous.Count != current.Count)
            {
                return false;
            }

            foreach (var pair in current)
            {
                if (!previous.TryGetValue(pair.Key, out var before) || !before.Equals(pair.Value))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Achievement ids whose progress moved. An id that appears for the first time counts as
        /// changed: the account endpoint omits achievements with no progress at all, so its arrival
        /// is itself the first progress on it.
        ///
        /// An id that disappears is deliberately not reported. Progress in this game does not go
        /// backwards, so a vanished entry means the response was partial, and treating it as a
        /// change would relock an achievement the player earned.
        /// </summary>
        public static List<int> GetChangedIds(
            Dictionary<int, Gw2ProgressSignature> previous,
            Dictionary<int, Gw2ProgressSignature> current)
        {
            var changed = new List<int>();
            if (current == null)
            {
                return changed;
            }

            foreach (var pair in current)
            {
                if (previous == null ||
                    !previous.TryGetValue(pair.Key, out var before) ||
                    !before.Equals(pair.Value))
                {
                    changed.Add(pair.Key);
                }
            }

            return changed;
        }
    }
}
