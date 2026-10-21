using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>The metadata source choices grid; DataContext is its rows.</summary>
    public partial class MetadataChoicesGrid : UserControl
    {
        public MetadataChoicesGrid()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Whether the Rarity column shows. Exophase rarity is read by each platform's scanner, so
        /// it is chosen per platform and has no per-game choice.
        /// </summary>
        public bool ShowsRarity
        {
            get => RarityColumn.Visibility == Visibility.Visible;
            set => RarityColumn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Whether the grid takes all the height it is given and scrolls inside it, for a page where
        /// it is the only content; otherwise it stops at a fixed height among other sections.
        /// </summary>
        public bool FillsHeight
        {
            get => double.IsPositiveInfinity(ChoicesGrid.MaxHeight);
            set
            {
                ChoicesGrid.MaxHeight = value ? double.PositiveInfinity : 520;
                ChoicesGrid.VerticalAlignment = value ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            }
        }
    }
}
