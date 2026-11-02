using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Picks a game, or several for a link that names several, from one search source. Hosted in a
    /// Playnite extension window by <see cref="Helpers.GameSearchWindow"/>; nothing is stored here.
    /// </summary>
    public partial class GameSearchDialog : UserControl
    {
        private readonly GameSearchViewModel _viewModel;

        public GameSearchDialog()
        {
            InitializeComponent();
        }

        internal GameSearchDialog(GameSearchViewModel viewModel)
            : this()
        {
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        public bool? DialogResult { get; private set; }

        /// <summary>The games chosen on Apply; empty otherwise.</summary>
        public IReadOnlyList<GameSearchResult> Selection { get; private set; } = Array.Empty<GameSearchResult>();

        public event EventHandler RequestClose;

        private void Accept()
        {
            var selection = _viewModel?.GetSelection();
            if (selection == null || selection.Count == 0)
            {
                return;
            }

            Selection = selection;
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void Cancel()
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void SearchPanel_Accepted(object sender, EventArgs e) => Accept();

        private void SearchPanel_Cancelled(object sender, EventArgs e) => Cancel();

        private void ApplyButton_Click(object sender, RoutedEventArgs e) => Accept();

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancel();
    }
}
