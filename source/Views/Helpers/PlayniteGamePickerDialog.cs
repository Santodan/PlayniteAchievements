using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Helpers
{
    internal sealed class PlayniteGamePickerDialog : Window
    {
        private readonly TextBox _searchBox;
        private readonly ListBox _gamesList;
        private readonly ICollectionView _gamesView;

        private PlayniteGamePickerDialog(
            IEnumerable<Game> games,
            string title,
            string initialSearch)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Select Playnite Game" : title;
            Width = 680;
            Height = 520;
            MinWidth = 460;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var items = new ObservableCollection<PlayniteGamePickerItem>(
                (games ?? Enumerable.Empty<Game>())
                    .Where(game => game != null && game.Id != Guid.Empty)
                    .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(game => new PlayniteGamePickerItem(game)));

            _gamesView = CollectionViewSource.GetDefaultView(items);
            _gamesView.Filter = FilterGame;

            _searchBox = new TextBox
            {
                Margin = new Thickness(12, 12, 12, 8),
                Text = initialSearch ?? string.Empty
            };
            _searchBox.TextChanged += (_, __) => _gamesView.Refresh();

            _gamesList = new ListBox
            {
                Margin = new Thickness(12, 0, 12, 12),
                ItemsSource = _gamesView,
                DisplayMemberPath = nameof(PlayniteGamePickerItem.DisplayText)
            };
            _gamesList.MouseDoubleClick += (_, __) => AcceptSelection();

            var okButton = new Button
            {
                Content = "OK",
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            okButton.Click += (_, __) => AcceptSelection();

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                IsCancel = true
            };
            cancelButton.Click += (_, __) => DialogResult = false;

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12)
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(_searchBox);
            Grid.SetRow(_gamesList, 1);
            root.Children.Add(_gamesList);
            Grid.SetRow(buttonPanel, 2);
            root.Children.Add(buttonPanel);

            Content = root;

            Loaded += (_, __) =>
            {
                _searchBox.Focus();
                _searchBox.SelectAll();
                if (_gamesList.Items.Count > 0)
                {
                    _gamesList.SelectedIndex = 0;
                }
            };
        }

        public Game SelectedGame { get; private set; }

        public static Game Pick(
            Window owner,
            IEnumerable<Game> games,
            string title,
            string initialSearch)
        {
            var dialog = new PlayniteGamePickerDialog(games, title, initialSearch);
            if (owner != null)
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog() == true ? dialog.SelectedGame : null;
        }

        private bool FilterGame(object value)
        {
            if (!(value is PlayniteGamePickerItem item))
            {
                return false;
            }

            var query = _searchBox?.Text;
            return string.IsNullOrWhiteSpace(query) ||
                   item.SearchText.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AcceptSelection()
        {
            if (!(_gamesList.SelectedItem is PlayniteGamePickerItem item))
            {
                return;
            }

            SelectedGame = item.Game;
            DialogResult = true;
        }

        private sealed class PlayniteGamePickerItem
        {
            public PlayniteGamePickerItem(Game game)
            {
                Game = game;
                var source = game.Source?.Name;
                var platforms = string.Join(
                    ", ",
                    (game.Platforms ?? Enumerable.Empty<Platform>())
                        .Where(platform => !string.IsNullOrWhiteSpace(platform?.Name))
                        .Select(platform => platform.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                var metadata = string.Join(
                    " | ",
                    new[] { source, platforms }.Where(value => !string.IsNullOrWhiteSpace(value)));
                DisplayText = string.IsNullOrWhiteSpace(metadata)
                    ? game.Name
                    : $"{game.Name} ({metadata})";
                SearchText = string.Join(" ", game.Name, source, platforms);
            }

            public Game Game { get; }

            public string DisplayText { get; }

            public string SearchText { get; }
        }
    }
}
