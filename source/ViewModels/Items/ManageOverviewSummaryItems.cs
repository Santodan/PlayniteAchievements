using System;
using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// The Manage Achievements Overview's breakdowns and customization counts, already formatted
    /// for display. Rebuilt whole on every shell reload and swapped in as one value.
    /// </summary>
    public sealed class ManageOverviewSummary
    {
        public static readonly ManageOverviewSummary Empty = new ManageOverviewSummary();

        // Each stat is an "x / y" text and whether it has anything to show: a stat whose total
        // is zero is left off the Overview rather than shown as "0 / 0".

        public ManageOverviewStat RarityCommon { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat RarityUncommon { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat RarityRare { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat RarityUltraRare { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat TrophyPlatinum { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat TrophyGold { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat TrophySilver { get; set; } = ManageOverviewStat.None;

        public ManageOverviewStat TrophyBronze { get; set; } = ManageOverviewStat.None;

        /// <summary>Unlocked over total, summed from each achievement's points.</summary>
        public ManageOverviewStat Points { get; set; } = ManageOverviewStat.None;

        /// <summary>Achievements in a category other than the default one, over all achievements.</summary>
        public ManageOverviewStat Categorized { get; set; } = ManageOverviewStat.None;

        /// <summary>Unlocked goals over all goals.</summary>
        public ManageOverviewStat Goals { get; set; } = ManageOverviewStat.None;

        public IReadOnlyList<ManageOverviewCustomizationChip> Customizations { get; set; } =
            Array.Empty<ManageOverviewCustomizationChip>();

        public bool HasCustomizations => Customizations.Count > 0;
    }

    /// <summary>One "x / y" stat on the Overview, shown only when its total is above zero.</summary>
    public sealed class ManageOverviewStat
    {
        public static readonly ManageOverviewStat None = new ManageOverviewStat(null, false);

        public ManageOverviewStat(string text, bool isVisible)
        {
            Text = text;
            IsVisible = isVisible;
        }

        public string Text { get; }

        public bool IsVisible { get; }
    }

    /// <summary>
    /// One kind of stored customization on the Overview. A game-level setting has no count and
    /// shows its label alone.
    /// </summary>
    public sealed class ManageOverviewCustomizationChip
    {
        public ManageOverviewCustomizationChip(string label, string countText, string toolTip = null)
        {
            Label = label;
            CountText = countText;
            ToolTip = toolTip;
        }

        public string Label { get; }

        public string CountText { get; }

        public bool HasCount => !string.IsNullOrEmpty(CountText);

        /// <summary>Detail behind the count, such as which achievements are the capstones.</summary>
        public string ToolTip { get; }
    }
}
