// --SUCCESSSTORY--
using System.Collections.Generic;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible compact unlocked control for theme integration.
    /// Displays unlocked achievements in a horizontal grid, with "+X more" in the last column.
    /// </summary>
    public partial class PluginCompactUnlockedControl : CompactAchievementControlBase
    {
        protected override Grid CompactViewGrid => PART_ScCompactView;

        public PluginCompactUnlockedControl()
        {
            InitializeComponent();
            DataContext = this;
        }

        protected override List<AchievementDetail> GetSourceAchievements()
        {
            var source = EffectiveLegacyTheme?.ListAchUnlockDateDesc;
            if (source == null || source.Count == 0)
            {
                return new List<AchievementDetail>();
            }
            // ListAchUnlockDateDesc contains ALL achievements (locked and unlocked),
            // sorted with unlocked first. Filter to only unlocked achievements.
            int count = 0;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i]?.Unlocked == true)
                {
                    count++;
                }
            }
            var result = new List<AchievementDetail>(count);
            for (int i = 0; i < source.Count; i++)
            {
                var a = source[i];
                if (a != null && a.Unlocked)
                {
                    result.Add(a);
                }
            }
            return result;
        }

        protected override int GetTotalCount()
        {
            // Use the Unlocked property which has the correct count of unlocked achievements.
            // ListAchUnlockDateDesc contains all achievements (locked + unlocked), so its count is incorrect.
            return EffectiveLegacyTheme?.Unlocked ?? 0;
        }

        protected override string GetHasAchievementPropertyName()
        {
            return nameof(HasUnlocked);
        }

        protected override string GetTotalCountPropertyName()
        {
            return nameof(TotalUnlocked);
        }

        protected override string[] GetSettingsPropertyNames()
        {
            return new[]
            {
                nameof(LegacyThemeBindings.ListAchUnlockDateDesc),
                nameof(LegacyThemeBindings.ListAchievements),
                nameof(LegacyThemeBindings.Unlocked)
            };
        }

        public bool HasUnlocked => GetTotalCount() > 0;

        public int TotalUnlocked => GetTotalCount();

        // RemainingCount is provided by the base class
    }
}
// --END SUCCESSSTORY--
