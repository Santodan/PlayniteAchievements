using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Narrows the sidebar's completion bar and stat chips to the categories the selected tab is
    /// looking at: the editor's category filter, or the Categories tab's selected rows.
    /// </summary>
    /// <remarks>
    /// Each tab that can scope registers a source; the sidebar reads the source of the selected
    /// tab and nothing else, so switching tabs follows the new tab's own selection and a tab
    /// without one shows the whole game. Membership is by exact label, the way the editor filter
    /// and the category rows count, so the sidebar describes the rows the grid beside it shows.
    /// </remarks>
    public sealed partial class ManageAchievementsViewModel
    {
        private readonly Dictionary<ManageAchievementsTab, Func<IReadOnlyCollection<string>>> _sidebarScopeSources =
            new Dictionary<ManageAchievementsTab, Func<IReadOnlyCollection<string>>>();

        private IReadOnlyList<AchievementDetail> _sidebarSourceAchievements = Array.Empty<AchievementDetail>();
        private List<string> _sidebarScopeLabels;
        private bool _sidebarScopeRefreshQueued;
        private ManageOverviewSummary _sidebarSummary = ManageOverviewSummary.Empty;
        private int _sidebarTotalAchievements;
        private int _sidebarUnlockedAchievements;
        private string _sidebarScopeLabel;
        private string _sidebarScopeToolTip;

        /// <summary>The stats the sidebar chips show: the whole game, or the scoped categories.</summary>
        public ManageOverviewSummary SidebarSummary
        {
            get => _sidebarSummary;
            private set => SetValue(ref _sidebarSummary, value ?? ManageOverviewSummary.Empty);
        }

        public bool IsSidebarCategoryScoped => _sidebarScopeLabels != null;

        /// <summary>The scoped categories' leaf names, shown in place of the completion label.</summary>
        public string SidebarScopeLabel
        {
            get => _sidebarScopeLabel;
            private set => SetValue(ref _sidebarScopeLabel, value);
        }

        /// <summary>The scoped categories' full paths, one per line.</summary>
        public string SidebarScopeToolTip
        {
            get => _sidebarScopeToolTip;
            private set => SetValue(ref _sidebarScopeToolTip, value);
        }

        public string SidebarCompletionSummary
        {
            get
            {
                var percent = AchievementCompletionPercentCalculator.ComputeRoundedPercent(
                    _sidebarUnlockedAchievements,
                    _sidebarTotalAchievements);
                return $"{_sidebarUnlockedAchievements} / {_sidebarTotalAchievements} ({PercentFormatter.FormatWhole(percent)})";
            }
        }

        /// <summary>
        /// The completion value at its widest for this game: every achievement unlocked out of
        /// all of them. A scoped value is a subset, so it never needs more room than this.
        /// </summary>
        public string SidebarCompletionWidthReservationText =>
            $"{TotalAchievements} / {TotalAchievements} ({PercentFormatter.FormatWhole(100)})";

        public int SidebarCompletionPercentValue => AchievementCompletionPercentCalculator.ComputeRoundedPercent(
            _sidebarUnlockedAchievements,
            _sidebarTotalAchievements);

        /// <summary>
        /// Registers the categories a tab scopes the sidebar to. The source is read whenever that
        /// tab is selected or reports a change; an empty result means the whole game.
        /// </summary>
        public void RegisterSidebarCategoryScopeSource(
            ManageAchievementsTab tab,
            Func<IReadOnlyCollection<string>> source)
        {
            if (source == null)
            {
                _sidebarScopeSources.Remove(tab);
            }
            else
            {
                _sidebarScopeSources[tab] = source;
            }

            NotifySidebarCategoryScopeChanged(tab);
        }

        /// <summary>Called by a tab when the categories it scopes to change.</summary>
        public void NotifySidebarCategoryScopeChanged(ManageAchievementsTab tab)
        {
            if (tab == SelectedTab)
            {
                QueueSidebarScopeRefresh();
            }
        }

        // Coalesced onto one Background pass: the category grid raises SelectionChanged several
        // times while it rebinds, and each would otherwise rebuild the stats.
        private void QueueSidebarScopeRefresh()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                RefreshSidebarScope();
                return;
            }

            if (_sidebarScopeRefreshQueued)
            {
                return;
            }

            _sidebarScopeRefreshQueued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _sidebarScopeRefreshQueued = false;
                RefreshSidebarScope();
            }));
        }

        private void RefreshSidebarScope()
        {
            IReadOnlyCollection<string> labels = null;
            if (_sidebarScopeSources.TryGetValue(SelectedTab, out var source))
            {
                try
                {
                    labels = source();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to read the sidebar category scope.");
                }
            }

            var normalized = (labels ?? Array.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                .Distinct(CategoryPathHelper.LabelComparer)
                .ToList();
            _sidebarScopeLabels = normalized.Count > 0 ? normalized : null;
            RebuildSidebarStats();
        }

        private void RebuildSidebarStats()
        {
            var labels = _sidebarScopeLabels;
            if (labels == null)
            {
                SidebarSummary = OverviewSummary;
                _sidebarTotalAchievements = TotalAchievements;
                _sidebarUnlockedAchievements = UnlockedAchievements;
                SidebarScopeLabel = null;
                SidebarScopeToolTip = null;
            }
            else
            {
                var labelSet = new HashSet<string>(labels, CategoryPathHelper.LabelComparer);
                var scoped = _sidebarSourceAchievements
                    .Where(achievement => achievement != null &&
                                          labelSet.Contains(AchievementCategoryHelper.NormalizeCategoryOrDefault(achievement.Category)))
                    .ToList();

                // No custom data: the per-kind customization counts are an Overview tab section,
                // never a sidebar chip.
                SidebarSummary = BuildOverviewSummary(scoped, null, _showsRarity);
                _sidebarTotalAchievements = scoped.Count;
                _sidebarUnlockedAchievements = scoped.Count(achievement => achievement.Unlocked);
                SidebarScopeLabel = string.Join(", ", labels.Select(AchievementCategoryHelper.ToCategoryLeafDisplayText));
                SidebarScopeToolTip = string.Join(Environment.NewLine, labels.Select(AchievementCategoryHelper.ToCategoryLabelDisplayText));
            }

            OnPropertyChanged(nameof(IsSidebarCategoryScoped));
            OnPropertyChanged(nameof(SidebarCompletionSummary));
            OnPropertyChanged(nameof(SidebarCompletionWidthReservationText));
            OnPropertyChanged(nameof(SidebarCompletionPercentValue));
            RaiseSidebarStatVisibility();
        }
    }
}
