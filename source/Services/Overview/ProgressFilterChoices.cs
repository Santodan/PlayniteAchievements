using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
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

        // Listeners are held weakly: each subscriber keeps its own delegate alive for as long as
        // it lives, so a discarded widget or control bar drops out on its own.
        private static readonly List<WeakReference<Action>> Listeners = new List<WeakReference<Action>>();
        private static PlayniteAchievementsSettings _watchedSettings;
        private static PersistedSettings _watchedPersisted;

        /// <summary>
        /// Calls <paramref name="onChanged"/> whenever the Achievement styles change, including
        /// when a cancelled settings edit swaps the persisted settings back. The caller must keep
        /// a reference to the delegate.
        /// </summary>
        public static void Subscribe(Action onChanged)
        {
            if (onChanged == null)
            {
                return;
            }

            Listeners.Add(new WeakReference<Action>(onChanged));
            Watch(PlayniteAchievementsPlugin.Instance?.Settings);
        }

        private static void Watch(PlayniteAchievementsSettings settings)
        {
            if (!ReferenceEquals(settings, _watchedSettings))
            {
                if (_watchedSettings != null)
                {
                    _watchedSettings.PropertyChanged -= OnSettingsPropertyChanged;
                }

                _watchedSettings = settings;
                if (settings != null)
                {
                    settings.PropertyChanged += OnSettingsPropertyChanged;
                }
            }

            var persisted = settings?.Persisted;
            if (!ReferenceEquals(persisted, _watchedPersisted))
            {
                if (_watchedPersisted != null)
                {
                    _watchedPersisted.PropertyChanged -= OnPersistedPropertyChanged;
                }

                _watchedPersisted = persisted;
                if (persisted != null)
                {
                    persisted.PropertyChanged += OnPersistedPropertyChanged;
                }
            }
        }

        private static void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                Watch(_watchedSettings);
                Raise();
            }
        }

        private static void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PersistedSettings.ProgressDetails))
            {
                Raise();
            }
        }

        private static void Raise()
        {
            var live = new List<Action>();
            Listeners.RemoveAll(reference =>
            {
                if (reference.TryGetTarget(out var action))
                {
                    live.Add(action);
                    return false;
                }

                return true;
            });

            foreach (var action in live)
            {
                action();
            }
        }

        /// <summary>
        /// The choices offered under <paramref name="order"/>, in display order. Given
        /// <paramref name="games"/>, only the finishes one of them shows are offered: with Rarity
        /// ahead of Points, a game with rarity data shows the completed badge, never the mythic.
        /// </summary>
        public static IReadOnlyList<string> Keys(
            IReadOnlyList<ProgressDetailStyle> order,
            bool includeNoProgress,
            IEnumerable<GameSummaryItem> games = null)
        {
            var finishes = CompletionBreakdown.FinishOrderFor(order).Select(KeyOf);
            if (games != null)
            {
                var shown = new HashSet<string>(
                    games.Where(game => game?.IsCompleted == true).Select(game => KeyFor(game, order)),
                    StringComparer.Ordinal);
                finishes = finishes.Where(shown.Contains);
            }

            var keys = finishes.ToList();
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
