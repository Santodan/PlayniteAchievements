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

        /// <summary>Unlocked capstones over all capstones; the tooltip names them.</summary>
        public ManageOverviewStat Capstones { get; set; } = ManageOverviewStat.None;

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

        // Whether any stat in a multi-chip sidebar group has something to show.

        public bool HasRarity =>
            RarityCommon.IsVisible || RarityUncommon.IsVisible || RarityRare.IsVisible || RarityUltraRare.IsVisible;

        public bool HasTrophies =>
            TrophyBronze.IsVisible || TrophySilver.IsVisible || TrophyGold.IsVisible || TrophyPlatinum.IsVisible;

        /// <summary>Filtered achievements over all achievements.</summary>
        public ManageOverviewStat Filtered { get; set; } = ManageOverviewStat.None;

        /// <summary>Achievements with a note over all achievements.</summary>
        public ManageOverviewStat Notes { get; set; } = ManageOverviewStat.None;

        public IReadOnlyList<ManageOverviewCustomizationChip> Customizations { get; set; } =
            Array.Empty<ManageOverviewCustomizationChip>();

        public bool HasCustomizations => Customizations.Count > 0;
    }

    /// <summary>One "x / y" stat on the Overview, shown only when its total is above zero.</summary>
    public sealed class ManageOverviewStat
    {
        public static readonly ManageOverviewStat None = new ManageOverviewStat(null, false);

        public ManageOverviewStat(string text, bool isVisible, string toolTip = null)
        {
            Text = text;
            IsVisible = isVisible;
            ToolTip = toolTip;
        }

        public string Text { get; }

        public bool IsVisible { get; }

        /// <summary>Detail behind the stat, or null to fall back to the chip's own tooltip.</summary>
        public string ToolTip { get; }
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
