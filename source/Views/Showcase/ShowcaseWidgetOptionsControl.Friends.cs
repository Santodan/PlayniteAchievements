using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Settings.Controls;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    internal sealed partial class ShowcaseWidgetOptionsControl
    {
        /// <summary>
        /// The friends widgets' rows: which friends the widget shows, then its control bar items.
        /// </summary>
        private void AddFriendWidgetRows<TAdapter>(Panel contentRows, Panel barRows)
            where TAdapter : SharedControlBarAdapter, new()
        {
            AddFriendPicker(contentRows);
            AddControlBarRows<TAdapter>(barRows);
        }

        /// <summary>
        /// The friends a friends widget shows, picked from a dropdown of every friend; none picked
        /// shows them all. Each friend is saved as one of its accounts, and a saved account picks
        /// the friend it belongs to now, so merging and unmerging keep the choice.
        /// </summary>
        private void AddFriendPicker(Panel panel)
        {
            var picker = new FriendPicker(_settings, FriendChoices(), () =>
            {
                _persist?.Invoke();
                if (_publishChanges)
                {
                    ShowcaseConfigurationEvents.RaiseChanged();
                }
            });

            var row = new Grid();
            OptionRowLayout.Apply(row);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = Localize("LOCPlayAch_Settings_Friends"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var bar = new GridControlBarViewModel();
            bar.Items.Add(picker.Item);
            var editor = new GridControlBarControl
            {
                ControlBar = bar,
                ShowControlBar = true,
                FillsWidth = true,
                CollapsesFilters = false
            };
            var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = ControlRowSizeGroup });
            host.Children.Add(editor);
            Grid.SetColumn(host, 1);
            row.Children.Add(host);
            panel.Children.Add(row);
        }

        /// <summary>
        /// Every friend as (its accounts, its name), by name: the merged friends of the shared
        /// friend lists once read, otherwise the friends in the settings.
        /// </summary>
        private static IReadOnlyList<FriendChoice> FriendChoices()
        {
            var source = PlayniteAchievementsPlugin.Instance?.FriendLists;
            var lists = source != null && source.Ensure() ? source.Current : null;
            IEnumerable<FriendChoice> choices;
            if (lists != null)
            {
                choices = lists.Friends
                    .Where(friend => friend != null)
                    .Select(friend => new FriendChoice(
                        FriendsOverviewLists.AccountsOf(friend).Select(account => account.Key).ToList(),
                        friend.DisplayName));
            }
            else
            {
                choices = (PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?
                        .GetFriendSettings(includeIgnored: false) ?? new List<FriendSettingsEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => new FriendChoice(
                        new[] { FriendAccountRef.BuildKey(entry.ProviderKey, entry.ExternalUserId) },
                        string.IsNullOrWhiteSpace(entry.Nickname) ? entry.DisplayName : entry.Nickname));
            }

            return choices
                .Where(choice => choice.Accounts.Count > 0)
                .OrderBy(choice => choice.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private sealed class FriendChoice
        {
            public FriendChoice(IReadOnlyList<string> accounts, string name)
            {
                Accounts = accounts.Where(account => !string.IsNullOrWhiteSpace(account)).ToList();
                Name = name;
            }

            /// <summary>The friend's account keys; the first names the friend in the saved option.</summary>
            public IReadOnlyList<string> Accounts { get; }

            public string Name { get; }

            public string Key => Accounts[0];
        }

        /// <summary>The friend dropdown's state: the picked friends, saved as account keys.</summary>
        private sealed class FriendPicker : PlayniteAchievements.Common.ObservableObject
        {
            private readonly ShowcaseWidgetInstanceSettings _settings;
            private readonly IReadOnlyList<FriendChoice> _choices;
            private readonly Action _changed;
            private readonly HashSet<string> _picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public FriendPicker(ShowcaseWidgetInstanceSettings settings, IReadOnlyList<FriendChoice> choices, Action changed)
            {
                _settings = settings;
                _choices = choices;
                _changed = changed;

                // A saved account picks the friend it belongs to; accounts of friends no longer
                // listed are kept, so a friend reappearing is still picked.
                var saved = ShowcaseWidgetOptions.GetFriendAccountKeys(settings);
                var savedSet = new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase);
                foreach (var choice in choices.Where(choice => choice.Accounts.Any(savedSet.Contains)))
                {
                    _picked.Add(choice.Key);
                    savedSet.ExceptWith(choice.Accounts);
                }

                _unlisted = savedSet.ToList();
                Item = new GridMultiSelectFilter(
                    this,
                    nameof(DisplayText),
                    () => DisplayText,
                    () => _choices.Select(choice => choice.Key),
                    key => _picked.Contains(key),
                    SetPicked,
                    key => _choices.FirstOrDefault(choice => choice.Key == key)?.Name ?? key)
                {
                    LabelKey = "LOCPlayAch_Settings_Friends",
                    Width = 220,
                    ClearAll = Clear
                };
            }

            private readonly List<string> _unlisted;

            public GridMultiSelectFilter Item { get; }

            public string DisplayText
            {
                get
                {
                    var names = _choices.Where(choice => _picked.Contains(choice.Key)).Select(choice => choice.Name).ToList();
                    return names.Count == 0 ? Localize("LOCPlayAch_Common_All") : string.Join(", ", names);
                }
            }

            private void SetPicked(string key, bool picked)
            {
                if (!(picked ? _picked.Add(key) : _picked.Remove(key)))
                {
                    return;
                }

                Save();
            }

            private void Clear()
            {
                if (_picked.Count == 0)
                {
                    return;
                }

                _picked.Clear();
                _unlisted.Clear();
                Save();
            }

            private void Save()
            {
                ShowcaseWidgetOptions.SetFriendAccountKeys(
                    _settings,
                    _choices.Where(choice => _picked.Contains(choice.Key)).Select(choice => choice.Key).Concat(_unlisted));
                OnPropertyChanged(nameof(DisplayText));
                _changed?.Invoke();
            }
        }
    }
}
