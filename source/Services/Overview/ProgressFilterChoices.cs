using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// The Progress filter's choices, shared by the Overview, the game grids and the showcase
    /// control bars: each finish badge the Achievement styles allow, in their order (Mythic,
    /// Platinum, Completed), then In Progress and No Progress. A finished game falls under the
    /// badge its rows show (<see cref="GameSummaryItem.CompletionBadgeFor"/>), so the choices
    /// line up with the Completions pie's slices. Choices are stable keys; labels are for display.
    /// </summary>
    public static class ProgressFilterChoices
    {
        public const string Mythic = "Mythic";
        public const string Platinum = "Platinum";
        public const string Completed = "Complete";
        public const string InProgress = "InProgress";
        public const string NoProgress = "NoProgress";

        /// <summary>
        /// Every finish key, whether offered or not, so a selection that means "finished" keeps
        /// meaning it after the Achievement styles offer another finish.
        /// </summary>
        public static IReadOnlyList<string> AllFinishes { get; } = new[] { Mythic, Platinum, Completed };

        /// <summary>The Achievement styles order from the current settings.</summary>
        public static IReadOnlyList<ProgressDetailStyle> CurrentOrder() =>
            ProgressDetailOrder.EnabledStyles(PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.ProgressDetails);

        /// <summary>The choices offered under <paramref name="order"/>, in display order.</summary>
        public static IReadOnlyList<string> Keys(IReadOnlyList<ProgressDetailStyle> order, bool includeNoProgress)
        {
            var keys = CompletionBreakdown.FinishOrderFor(order).Select(KeyOf).ToList();
            keys.Add(InProgress);
            if (includeNoProgress)
            {
                keys.Add(NoProgress);
            }

            return keys;
        }

        public static string KeyOf(CompletionBadgeKind kind)
        {
            switch (kind)
            {
                case CompletionBadgeKind.Mythic:
                    return Mythic;
                case CompletionBadgeKind.Platinum:
                    return Platinum;
                default:
                    return Completed;
            }
        }

        /// <summary>The choice a game falls under.</summary>
        public static string KeyFor(GameSummaryItem game, IReadOnlyList<ProgressDetailStyle> order)
        {
            if (game == null)
            {
                return null;
            }

            if (game.IsCompleted)
            {
                return KeyOf(game.CompletionBadgeFor(order));
            }

            return game.UnlockedAchievements <= 0 ? NoProgress : InProgress;
        }

        public static string Label(string key)
        {
            switch (key)
            {
                case Mythic:
                    return ResourceProvider.GetString("LOCPlayAch_Badge_Mythic");
                case Platinum:
                    return ResourceProvider.GetString("LOCPlayAch_Trophy_Platinum");
                case Completed:
                    return ResourceProvider.GetString("LOCPlayAch_Completed");
                case InProgress:
                    return ResourceProvider.GetString("LOCPlayAch_Filter_InProgress");
                case NoProgress:
                    return ResourceProvider.GetString("LOCPlayAch_Filter_NoProgress");
                default:
                    return key;
            }
        }

        /// <summary>The key whose label is <paramref name="label"/>, or null.</summary>
        public static string KeyForLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            return AllFinishes.Concat(new[] { InProgress, NoProgress })
                .FirstOrDefault(key => string.Equals(Label(key), label.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Keys for a filter state saved by position, when the options were Complete, In Progress
        /// and No Progress: Complete covers every finish.
        /// </summary>
        public static IReadOnlyList<string> FromLegacyPositions(IEnumerable<int> positions)
        {
            var keys = new List<string>();
            foreach (var position in positions ?? Enumerable.Empty<int>())
            {
                switch (position)
                {
                    case 0:
                        keys.AddRange(AllFinishes);
                        break;
                    case 1:
                        keys.Add(InProgress);
                        break;
                    case 2:
                        keys.Add(NoProgress);
                        break;
                }
            }

            return keys.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// The games a selection keeps, counting only the offered choices; null when none of them
        /// is selected, which keeps every game.
        /// </summary>
        public static Func<GameSummaryItem, bool> Matcher(
            IEnumerable<string> selected,
            IEnumerable<string> offered,
            IReadOnlyList<ProgressDetailStyle> order)
        {
            var effective = new HashSet<string>(
                (selected ?? Enumerable.Empty<string>()).Intersect(offered ?? Enumerable.Empty<string>(), StringComparer.Ordinal),
                StringComparer.Ordinal);
            if (effective.Count == 0)
            {
                return null;
            }

            return game => game != null && effective.Contains(KeyFor(game, order));
        }
    }
}
