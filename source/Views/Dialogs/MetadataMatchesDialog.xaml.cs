using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Pairs a game's achievements with one metadata source's rows. Hosted in a Playnite extension
    /// window by <see cref="Helpers.MetadataMatchesWindow"/>; nothing is written until Save.
    /// </summary>
    public partial class MetadataMatchesDialog : UserControl
    {
        public MetadataMatchesDialog()
        {
            InitializeComponent();
        }

        internal MetadataMatchesDialog(MetadataMatchesViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
        }

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        private void TrophyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // A double-click on a row pairs it; one on the scrollbar or empty space does nothing.
            var item = ItemsControl.ContainerFromElement(TrophyList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is MatchTrophyItem trophy)
            {
                (DataContext as MetadataMatchesViewModel)?.PairSelected(trophy);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
