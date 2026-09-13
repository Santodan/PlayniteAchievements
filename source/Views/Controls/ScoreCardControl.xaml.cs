using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Controls
{
    public partial class ScoreCardControl : UserControl
    {
        public static readonly DependencyProperty ScoreCardProperty =
            DependencyProperty.Register(
                nameof(ScoreCard),
                typeof(ScoreCardViewModel),
                typeof(ScoreCardControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsFeaturedProperty =
            DependencyProperty.Register(
                nameof(IsFeatured),
                typeof(bool),
                typeof(ScoreCardControl),
                new PropertyMetadata(false));

        public static readonly DependencyProperty FlatProperty =
            DependencyProperty.Register(
                nameof(Flat),
                typeof(bool),
                typeof(ScoreCardControl),
                new PropertyMetadata(false));

        public ScoreCardControl()
        {
            InitializeComponent();
        }

        public ScoreCardViewModel ScoreCard
        {
            get => (ScoreCardViewModel)GetValue(ScoreCardProperty);
            set => SetValue(ScoreCardProperty, value);
        }

        public bool IsFeatured
        {
            get => (bool)GetValue(IsFeaturedProperty);
            set => SetValue(IsFeaturedProperty, value);
        }

        /// <summary>
        /// Drops the card's own accent background and border for hosts that already provide
        /// chrome (showcase widget blocks).
        /// </summary>
        public bool Flat
        {
            get => (bool)GetValue(FlatProperty);
            set => SetValue(FlatProperty, value);
        }
    }
}
