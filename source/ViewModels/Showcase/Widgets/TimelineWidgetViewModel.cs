using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Timeline widget: the shared unlocks-over-time chart via TimelineViewModel. The
    /// window, granularity and split are widget settings; the widget also sets them itself from
    /// a picker and a split menu below the chart when its settings show them, and saves what is
    /// picked back into its settings.
    /// </summary>
    public sealed class TimelineWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly TimelineViewModel _timeline = new TimelineViewModel();
        private bool _showControls;
        private bool _showTimeWindowControl;
        private bool _showSplitControl;
        private ShowcaseTimelineSplit _split;

        // Set while Refresh pushes the settings into the chart, so those writes are not saved
        // back as if the user had picked them.
        private bool _applying;

        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<TimelineControlBarAdapter> _controlBarSlot;
        private GridControlBarViewModel _controlBar;

        public TimelineWidgetViewModel()
        {
            _timeline.PropertyChanged += Timeline_PropertyChanged;
            _controlBarSlot = new ShowcaseControlBarSlot<TimelineControlBarAdapter>(FeedCounts);
            SplitChoices = new[]
            {
                new TimelineSplitChoice(this, ShowcaseTimelineSplit.Platform, "LOCPlayAch_Common_Platform"),
                new TimelineSplitChoice(this, ShowcaseTimelineSplit.Rarity, "LOCPlayAch_Column_Rarity"),
                new TimelineSplitChoice(this, ShowcaseTimelineSplit.Trophy, "LOCPlayAch_Column_Trophy")
            };
        }

        public TimelineViewModel Timeline => _timeline;

        /// <summary>The platform, game, rarity and trophy filters the widget's settings show; null when they show none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        /// <summary>Whether the widget's settings show either control above the chart.</summary>
        public bool ShowControls
        {
            get => _showControls;
            private set => SetValue(ref _showControls, value);
        }

        public bool ShowTimeWindowControl
        {
            get => _showTimeWindowControl;
            private set => SetValue(ref _showTimeWindowControl, value);
        }

        public bool ShowSplitControl
        {
            get => _showSplitControl;
            private set => SetValue(ref _showSplitControl, value);
        }

        /// <summary>The split menu's entries, each checked while it splits the bars.</summary>
        public IReadOnlyList<TimelineSplitChoice> SplitChoices { get; }

        /// <summary>What each bar is split into, as stacked segments.</summary>
        public ShowcaseTimelineSplit Split
        {
            get => _split;
            set
            {
                if (_split == value)
                {
                    return;
                }

                SetValue(ref _split, value);
                OnPropertyChanged(nameof(IsSplit));
                foreach (var choice in SplitChoices)
                {
                    choice.Refresh();
                }

                if (!_applying)
                {
                    Save(ShowcaseTimelineOptions.SplitOption, value.ToString());
                    FeedCounts();
                }
            }
        }

        /// <summary>Whether the bars are split, which the split menu's button shows as checked.</summary>
        public bool IsSplit => _split != ShowcaseTimelineSplit.None;

        /// <summary>Whether the bars count completions, which only split by platform.</summary>
        public bool CountsCompletions =>
            ShowcaseTimelineOptions.GetCount(Projection?.Instance) == ShowcaseTimelineCount.Completions;

        protected override void Refresh()
        {
            var instance = Projection?.Instance;
            _applying = true;
            try
            {
                ShowTimeWindowControl = ShowcaseWidgetOptions.GetControlBarItemVisible(
                    instance,
                    ShowcaseControlBarItems.TimeWindow);
                ShowSplitControl = ShowcaseWidgetOptions.GetControlBarItemVisible(
                    instance,
                    ShowcaseControlBarItems.SplitByPlatform);
                ShowControls = ShowTimeWindowControl || ShowSplitControl;
                Split = ShowcaseTimelineOptions.GetSplit(instance);
                OnPropertyChanged(nameof(CountsCompletions));
                foreach (var choice in SplitChoices)
                {
                    choice.Refresh();
                }

                _timeline.Window = ShowcaseTimelineOptions.GetWindow(instance);
                _timeline.Granularity = ShowcaseTimelineOptions.GetGranularity(instance);
                _timeline.HighlightedSpan = Projection?.HighlightedSpan;
            }
            finally
            {
                _applying = false;
            }

            FeedCounts();
        }

        // The projection carries the unfiltered unlock counts, and the per-platform series only
        // when the saved option asked for them; filtered games and unlocks, completions, and a
        // split the menu picked before a reprojection are counted here from the snapshot. Tags,
        // rarity and trophy describe achievements, so completions count without them and split
        // by platform only.
        private void FeedCounts()
        {
            var source = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var completions = CountsCompletions;
            var snapshot = ApplyControlBarFilter(source);
            var adapter = _controlBarSlot.Adapter;
            if (adapter.HasAchievementSelection)
            {
                snapshot = OverviewLinkedSnapshots.ClipToAchievements(snapshot, adapter.MatchesAchievement);
            }

            var unfiltered = ReferenceEquals(snapshot, source);
            var split = completions && _split != ShowcaseTimelineSplit.Platform ? ShowcaseTimelineSplit.None : _split;
            switch (split)
            {
                case ShowcaseTimelineSplit.Platform:
                    _timeline.SetSeriesCounts(completions
                        ? TimelinePlatformSeries.FromCounts(snapshot, TimelinePlatformSeries.CompletionCountsByGame(snapshot))
                        : (unfiltered ? Projection?.TimelineByPlatform : null) ?? TimelinePlatformSeries.FromSnapshot(snapshot));
                    return;
                case ShowcaseTimelineSplit.Rarity:
                    _timeline.SetSeriesCounts(TimelineTierSeries.ByRarity(snapshot));
                    return;
                case ShowcaseTimelineSplit.Trophy:
                    _timeline.SetSeriesCounts(TimelineTierSeries.ByTrophy(snapshot));
                    return;
            }

            // The chart itself shows the empty caption when the window holds nothing.
            var counts = completions
                ? TimelinePlatformSeries.Sum(TimelinePlatformSeries.CompletionCountsByGame(snapshot))
                : (unfiltered ? Projection?.Timeline : null) ?? snapshot.GlobalUnlockCountsByDate ?? new Dictionary<DateTime, int>();
            _timeline.SetCounts(counts.ToDictionary(pair => pair.Key, pair => Math.Max(0, pair.Value)));
        }

        /// <summary>The snapshot narrowed to the games the control bar keeps; the snapshot itself when it keeps every one.</summary>
        private OverviewDataSnapshot ApplyControlBarFilter(OverviewDataSnapshot snapshot)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(Projection?.Instance);
            ControlBar = _controlBarSlot.ShownBar;

            var adapter = _controlBarSlot.Adapter;
            // Tags, rarity and trophy describe achievements, so they hide while the bars count
            // completions.
            adapter.SetAchievementFiltersApply(!CountsCompletions);
            var games = (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null)
                .ToList();
            adapter.UpdateOptions(games);
            var kept = adapter.Apply(games);
            return kept.Count == games.Count ? snapshot : OverviewLinkedSnapshots.NarrowToGames(snapshot, kept);
        }

        private void Timeline_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_applying)
            {
                return;
            }

            if (e.PropertyName == nameof(TimelineViewModel.Window))
            {
                Save(ShowcaseTimelineOptions.RangeOption, _timeline.Window?.ToKey());
            }
            else if (e.PropertyName == nameof(TimelineViewModel.Granularity))
            {
                Save(ShowcaseTimelineOptions.GranularityOption, _timeline.Granularity.ToString());
            }
        }

        private void Save(string key, string value)
        {
            var instanceId = Projection?.Instance?.InstanceId;
            if (!string.IsNullOrWhiteSpace(instanceId) && value != null)
            {
                ShowcaseControlBarStates.Store?.Save(instanceId, key, value);
            }
        }
    }

    /// <summary>One entry of the Timeline's split menu. Picking the checked entry again stops splitting.</summary>
    public sealed class TimelineSplitChoice : PlayniteAchievements.Common.ObservableObject
    {
        private readonly TimelineWidgetViewModel _owner;
        private readonly string _labelKey;

        public TimelineSplitChoice(TimelineWidgetViewModel owner, ShowcaseTimelineSplit split, string labelKey)
        {
            _owner = owner;
            _labelKey = labelKey;
            Split = split;
            PickCommand = new RelayCommand(_ => _owner.Split = IsSelected ? ShowcaseTimelineSplit.None : Split);
        }

        public ShowcaseTimelineSplit Split { get; }

        public string Label => Playnite.SDK.ResourceProvider.GetString(_labelKey);

        public bool IsSelected => _owner.Split == Split;

        /// <summary>False for rarity and trophy while the bars count completions.</summary>
        public bool IsEnabled => Split == ShowcaseTimelineSplit.Platform || !_owner.CountsCompletions;

        public RelayCommand PickCommand { get; }

        internal void Refresh()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsEnabled));
        }
    }}
