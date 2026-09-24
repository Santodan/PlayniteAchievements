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

        public string RarityCommonText { get; set; } = "0 / 0";

        public string RarityUncommonText { get; set; } = "0 / 0";

        public string RarityRareText { get; set; } = "0 / 0";

        public string RarityUltraRareText { get; set; } = "0 / 0";

        public bool HasTrophies { get; set; }

        public string TrophyPlatinumText { get; set; } = "0 / 0";

        public string TrophyGoldText { get; set; } = "0 / 0";

        public string TrophySilverText { get; set; } = "0 / 0";

        public string TrophyBronzeText { get; set; } = "0 / 0";

        public bool HasPoints { get; set; }

        public string PointsText { get; set; } = "0 / 0";

        public string HiddenText { get; set; } = "0";

        public string LastUnlockText { get; set; }

        public IReadOnlyList<ManageOverviewCategoryRow> Categories { get; set; } =
            Array.Empty<ManageOverviewCategoryRow>();

        public bool HasMultipleCategories => Categories.Count > 1;

        public IReadOnlyList<ManageOverviewCustomizationChip> Customizations { get; set; } =
            Array.Empty<ManageOverviewCustomizationChip>();

        public bool HasCustomizations => Customizations.Count > 0;
    }

    /// <summary>One category's name and <c>unlocked / total</c> on the Overview.</summary>
    public sealed class ManageOverviewCategoryRow
    {
        public ManageOverviewCategoryRow(string label, string progressText)
        {
            Label = label;
            ProgressText = progressText;
        }

        public string Label { get; }

        public string ProgressText { get; }
    }

    /// <summary>
    /// One kind of stored customization on the Overview. A game-level setting has no count and
    /// shows its label alone.
    /// </summary>
    public sealed class ManageOverviewCustomizationChip
    {
        public ManageOverviewCustomizationChip(string label, string countText)
        {
            Label = label;
            CountText = countText;
        }

        public string Label { get; }

        public string CountText { get; }

        public bool HasCount => !string.IsNullOrEmpty(CountText);
    }
}
