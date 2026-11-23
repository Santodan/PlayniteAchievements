using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Services.Database;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels
{
    public partial class OverviewViewModel
    {
        // Santodan-Fork: the sidebar All Achievements tab filters and sorts every summary row in
        // memory (control bar, additive sorts, saved custom sort). Upstream's snapshot no longer
        // carries those rows, so they are read through the resolved-row reads, only while the tab
        // is shown, and dropped again when the data changes.
        private bool _sidebarAllLoaded;
        private bool _sidebarAllLoading;
        private int _sidebarAllGeneration;

        private void InvalidateSidebarAllAchievements()
        {
            _sidebarAllGeneration++;
            _sidebarAllLoaded = false;
            if (IsAllAchievementsTabSelected)
            {
                ApplyRightFilters();
            }
            else
            {
                // Free the rows while another tab is shown; they are read again on return.
                _allAchievements = new List<AchievementDisplayItem>();
                _filteredSidebarAllAchievements = new List<AchievementDisplayItem>();
            }
        }

        /// <summary>
        /// True when the sidebar rows are current; otherwise starts their read, which re-runs
        /// the right-panel filters when it lands.
        /// </summary>
        private bool EnsureSidebarAllAchievementsLoaded()
        {
            if (_sidebarAllLoaded)
            {
                return true;
            }

            if (_sidebarAllLoading || _cacheReads == null || _disposed)
            {
                return false;
            }

            _sidebarAllLoading = true;
            var generation = _sidebarAllGeneration;
            var query = _achievementDataService.SummaryQuery().OrderBy(AchievementSort.Display);
            _scheduler.Run(() =>
            {
                IReadOnlyList<AchievementDisplayItem> rows;
                try
                {
                    var keys = _cacheReads.Reads.SortedKeys(query);
                    rows = _dataBuilder.LoadRows(_settings, query, keys);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to load the sidebar All Achievements rows.");
                    rows = Array.Empty<AchievementDisplayItem>();
                }

                _scheduler.Post(() =>
                {
                    _sidebarAllLoading = false;
                    if (_disposed)
                    {
                        return;
                    }

                    if (generation != _sidebarAllGeneration)
                    {
                        // The data changed during the read: read again if still shown.
                        if (IsAllAchievementsTabSelected)
                        {
                            ApplyRightFilters();
                        }

                        return;
                    }

                    _allAchievements = new List<AchievementDisplayItem>(rows ?? Array.Empty<AchievementDisplayItem>());
                    _sidebarAllLoaded = true;
                    if (IsAllAchievementsTabSelected)
                    {
                        ApplyRightFilters();
                    }
                });
            });
            return false;
        }
    }
}
