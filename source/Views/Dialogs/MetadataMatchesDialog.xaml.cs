using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Pairs a game's achievements with one metadata source's rows, one searchable dropdown per
    /// achievement. Hosted in a Playnite extension window by <see cref="Helpers.MetadataMatchesWindow"/>;
    /// nothing is written until Save.
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

        private void PickerPopup_Opened(object sender, EventArgs e) => MatchPickerHandlers.PopupOpened(sender);

        private void PickerSearch_PreviewKeyDown(object sender, KeyEventArgs e) => MatchPickerHandlers.SearchPreviewKeyDown(sender, e);

        private void PickerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
            MatchPickerHandlers.ListPreviewMouseLeftButtonUp(sender, e);

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
