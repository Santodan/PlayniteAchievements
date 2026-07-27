// --SUCCESSSTORY--
using System.Collections.Generic;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible compact locked control for theme integration.
    /// Displays locked achievements in a horizontal grid, with "+X more" in the last column.
    /// </summary>
    public partial class PluginCompactLockedControl : CompactAchievementControlBase
    {
        protected override Grid CompactViewGrid => PART_ScCompactView;

        public PluginCompactLockedControl()
        {
            InitializeComponent();
            DataContext = this;
        }

        protected override List<AchievementDetail> GetSourceAchievements()
        {
            var all = EffectiveLegacyTheme?.ListAchievements;
            if (all == null || all.Count == 0)
            {
                return new List<AchievementDetail>();
            }

            var result = new List<AchievementDetail>();
            for (int i = 0; i < all.Count; i++)
            {
                var a = all[i];
                if (a != null && !a.Unlocked)
                {
                    result.Add(a);
                }
            }

            var configuredSort = AchievementSortHelper.GetConfiguredDefaultSort(
                EffectiveSettings?.Persisted,
                AchievementSortSurface.CompactLockedList);
            if (configuredSort.PreservesSourceOrder)
            {
                return result;
            }

            return AchievementSortHelper.CreateSortedDetailList(
                result,
                configuredSort.SortMemberPath,
                configuredSort.Direction);
        }

        protected override int GetTotalCount()
        {
            return EffectiveLegacyTheme?.Locked ?? 0;
        }

        protected override string GetHasAchievementPropertyName()
        {
            return nameof(HasLocked);
        }

        protected override string GetTotalCountPropertyName()
        {
            return nameof(TotalLocked);
        }

        protected override string[] GetSettingsPropertyNames()
        {
            return new[]
            {
                nameof(LegacyThemeBindings.ListAchievements),
                nameof(LegacyThemeBindings.Locked)
            };
        }

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return AchievementSortHelper.IsConfiguredDefaultSortPropertyName(
                propertyName,
                AchievementSortSurface.CompactLockedList);
        }

        protected override bool ImagesAreLocked => true;

        // Legacy lists follow the modern compact list glow setting (locked icons never glow).
        protected override string RarityGlowSettingPath => "Settings.Persisted.ModernCompactListShowRarityGlow";

        protected override bool SupportsHiddenReveal => true;

        public bool HasLocked => GetTotalCount() > 0;

        public int TotalLocked => GetTotalCount();

        // RemainingCount is provided by the base class
    }
}
// --END SUCCESSSTORY--
