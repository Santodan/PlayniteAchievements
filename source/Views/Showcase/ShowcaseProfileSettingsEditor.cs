using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Shared editor for a Profile widget's manual identity: name, subtitle, avatar,
    /// background, and profile links. Every edit writes straight into the given profile;
    /// <c>onChanged</c> fires when an edit is committed (focus loss, Enter, or a button), so an
    /// instant-persist host can save there while the Showcase dialog edits a clone and saves
    /// on its own button.
    /// </summary>
    internal sealed class ShowcaseProfileSettingsEditor : UserControl
    {
        private const string ImagePatterns = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp";

        private readonly ShowcaseProfileSettings _profile;
        private readonly Action _onChanged;
        private readonly List<ShowcaseProfileLink> _links = new List<ShowcaseProfileLink>();
        private IReadOnlyList<KeyValuePair<string, string>> _currentProfileNames = Array.Empty<KeyValuePair<string, string>>();
        private StackPanel _linksPanel;

        public ShowcaseProfileSettingsEditor(ShowcaseProfileSettings profile, Action onChanged = null)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _onChanged = onChanged;
            Content = BuildContent();
        }

        /// <summary>The links as edited, for a host that stores them on save.</summary>
        public List<ShowcaseProfileLink> CollectProfileLinks()
        {
            return _links
                .Where(link => !string.IsNullOrWhiteSpace(link?.ProviderKey))
                .Select(link => new ShowcaseProfileLink
                {
                    ProviderKey = link.ProviderKey.Trim(),
                    Value = string.IsNullOrWhiteSpace(link.Value) ? null : link.Value.Trim()
                })
                .ToList();
        }

        private UIElement BuildContent()
        {
            var panel = new StackPanel();
            var providerHint = new TextBlock
            {
                Text = Localize("LOCPlayAch_Showcase_ProfileProviderHint"),
                FontStyle = FontStyles.Italic,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };
            providerHint.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Sm");
            providerHint.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            panel.Children.Add(providerHint);

            AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileName"),
                _profile.DisplayName,
                value => _profile.DisplayName = value,
                _onChanged);
            AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileSubtitle"),
                _profile.Subtitle,
                value => _profile.Subtitle = value,
                _onChanged);
            AddImagePicker(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileAvatar"),
                () => _profile.AvatarPath,
                value => _profile.AvatarPath = value);
            AddImagePicker(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileBackground"),
                () => _profile.BackgroundPath,
                value => _profile.BackgroundPath = value);
            BuildProfileLinks(panel);
            return panel;
        }

        /// <summary>
        /// The profile's links as an ordered, editable list: each row is a platform and either
        /// the user's name there or a full link, with move and remove buttons, and the add row
        /// appends one platform at a time. The caption under each box shows the page it will
        /// open, or while blank the platform's address shape. A new row starts from the
        /// signed-in user's stored name when the provider has one. Until links are first edited
        /// the list starts from what the card shows meanwhile: every platform that knows the name.
        /// </summary>
        private void BuildProfileLinks(Panel panel)
        {
            panel.Children.Add(CreateLabel(Localize("LOCPlayAch_Showcase_ProfileLinks")));

            _currentProfileNames = ShowcaseProfileResolver.CurrentUserProfileNames?.Invoke()
                ?? Array.Empty<KeyValuePair<string, string>>();
            _links.Clear();
            _links.AddRange(_profile.Links != null
                ? _profile.Links.Where(link => link != null).Select(link => link.Clone())
                : _currentProfileNames.Select(pair => new ShowcaseProfileLink { ProviderKey = pair.Key, Value = pair.Value }));

            _linksPanel = new StackPanel();
            panel.Children.Add(_linksPanel);
            panel.Children.Add(BuildAddLinkRow());
            RenderLinks();
        }

        // Writes the list into the profile; from then on the card shows exactly these links.
        private void StoreLinks()
        {
            _profile.Links = CollectProfileLinks();
        }

        private void CommitLinks()
        {
            StoreLinks();
            _onChanged?.Invoke();
        }

        private UIElement BuildAddLinkRow()
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var providers = new ComboBox { MinHeight = 30 };
            var registry = PlayniteAchievementsPlugin.Instance?.ProviderRegistry;
            foreach (var provider in registry?.GetAllProviders() ?? Array.Empty<Providers.IDataProvider>())
            {
                var key = provider?.ProviderKey;
                if (string.IsNullOrWhiteSpace(key) ||
                    string.Equals(key, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                providers.Items.Add(new ComboBoxItem
                {
                    Content = Providers.ProviderRegistry.GetLocalizedName(key),
                    Tag = key
                });
            }

            providers.SelectedIndex = providers.Items.Count > 0 ? 0 : -1;
            row.Children.Add(providers);

            var add = new Button
            {
                Content = Localize("LOCPlayAch_Button_Add"),
                MinWidth = 82
            };
            add.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            add.Click += (_, __) =>
            {
                if (!((providers.SelectedItem as ComboBoxItem)?.Tag is string key))
                {
                    return;
                }

                _links.Add(new ShowcaseProfileLink
                {
                    ProviderKey = key,
                    Value = _currentProfileNames.FirstOrDefault(pair =>
                        string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value
                });
                CommitLinks();
                RenderLinks(focusIndex: _links.Count - 1);
            };
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            return row;
        }

        private void RenderLinks(int focusIndex = -1)
        {
            _linksPanel.Children.Clear();
            for (var i = 0; i < _links.Count; i++)
            {
                var index = i;
                var link = _links[i];
                var row = new Grid();
                row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Sm");
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = Providers.ProviderRegistry.GetLocalizedName(link.ProviderKey),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                label.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
                label.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
                row.Children.Add(label);

                var box = new TextBox
                {
                    Text = link.Value ?? string.Empty,
                    MinHeight = 30,
                    Padding = new Thickness(7, 4, 7, 4)
                };
                Grid.SetColumn(box, 1);
                row.Children.Add(box);

                var caption = new TextBlock
                {
                    Opacity = 0.7,
                    FontStyle = FontStyles.Italic,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(2, 2, 0, 0)
                };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
                Grid.SetRow(caption, 1);
                Grid.SetColumn(caption, 1);
                Grid.SetColumnSpan(caption, 4);
                row.Children.Add(caption);
                UpdateLinkCaption(caption, link);

                var pending = false;
                box.TextChanged += (_, __) =>
                {
                    link.Value = box.Text;
                    UpdateLinkCaption(caption, link);
                    StoreLinks();
                    pending = true;
                };
                AttachCommit(box, () =>
                {
                    if (pending)
                    {
                        pending = false;
                        _onChanged?.Invoke();
                    }
                });

                // IcoFont arrow-up, arrow-down, and trash, as mapped in Playnite's shipped icofont.ttf.
                AddLinkButton(row, 2, "\uEA5E", "LOCPlayAch_Showcase_MoveEarlier", index > 0,
                    () => MoveLink(index, -1));
                AddLinkButton(row, 3, "\uEA5B", "LOCPlayAch_Showcase_MoveLater", index < _links.Count - 1,
                    () => MoveLink(index, 1));

                AddLinkButton(row, 4, "\uEE09", "LOCPlayAch_Button_Remove", true, () =>
                {
                    _links.RemoveAt(index);
                    CommitLinks();
                    RenderLinks();
                });
                _linksPanel.Children.Add(row);

                if (index == focusIndex)
                {
                    box.Loaded += (_, __) =>
                    {
                        box.Focus();
                        box.CaretIndex = box.Text.Length;
                    };
                }
            }
        }

        // The page the row will open; while blank, the platform's address shape with the name
        // slot shown literally, or nothing for a platform that only takes full links.
        private static void UpdateLinkCaption(TextBlock caption, ShowcaseProfileLink link)
        {
            var url = ShowcaseProfileResolver.BuildLinkUrl(link.ProviderKey, link.Value);
            if (url == null &&
                PlayniteAchievementsPlugin.Instance?.ProviderRegistry?.TryGetProvider(link.ProviderKey, out var provider) == true &&
                provider is Providers.IProfileLinkProvider links &&
                !string.IsNullOrWhiteSpace(links.ProfileUrlPattern))
            {
                url = links.ProfileUrlPattern.Replace("{0}", "\u2026");
            }

            caption.Text = url ?? string.Empty;
            caption.Visibility = string.IsNullOrEmpty(caption.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddLinkButton(Grid row, int column, string glyph, string toolTipKey, bool enabled, Action onClick)
        {
            // No fixed width: the implicit button padding clipped the glyph out of a 30px box.
            var button = new Button
            {
                Content = glyph,
                MinWidth = 30,
                MinHeight = 30,
                Padding = new Thickness(6, 0, 6, 0),
                IsEnabled = enabled,
                ToolTip = Localize(toolTipKey)
            };
            button.SetResourceReference(FontFamilyProperty, "PlayAch.FontFamily.Icon");
            button.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            button.Click += (_, __) => onClick();
            Grid.SetColumn(button, column);
            row.Children.Add(button);
        }

        private void MoveLink(int index, int direction)
        {
            var target = index + direction;
            if (index < 0 || index >= _links.Count || target < 0 || target >= _links.Count)
            {
                return;
            }

            var link = _links[index];
            _links.RemoveAt(index);
            _links.Insert(target, link);
            CommitLinks();
            RenderLinks();
        }

        // A picked file is copied into the showcase image store right away (content-addressed,
        // so the stored name never changes meaning and needs no cache eviction). A copy that
        // fails keeps the previous image rather than dropping it.
        private void AddImagePicker(Panel panel, string label, Func<string> read, Action<string> apply)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labelBlock = CreateLabel(label);
            labelBlock.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
            labelBlock.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(labelBlock);
            var box = new TextBox
            {
                Text = read() ?? string.Empty,
                IsReadOnly = true,
                MinHeight = 30,
                Padding = new Thickness(7, 4, 7, 4)
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            var browse = new Button
            {
                Content = Localize("LOCPlayAch_Button_Browse"),
                MinWidth = 82
            };
            browse.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            browse.Click += (_, __) =>
            {
                var dialog = new OpenFileDialog
                {
                    Filter = $"{Localize("LOCPlayAch_Showcase_ImageFiles")} ({ImagePatterns})|{ImagePatterns}|{Localize("LOCPlayAch_Showcase_AllFiles")} (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var store = PlayniteAchievementsPlugin.Instance?.ShowcaseImageStore;
                var imported = store?.Import(dialog.FileName);
                if (string.IsNullOrWhiteSpace(imported) ||
                    string.Equals(imported, read(), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                apply(imported);
                box.Text = imported;
                _onChanged?.Invoke();
            };
            Grid.SetColumn(browse, 2);
            row.Children.Add(browse);
            var clear = new Button
            {
                Content = Localize("LOCPlayAch_Button_Clear"),
                MinWidth = 72
            };
            clear.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            clear.Click += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(read()))
                {
                    return;
                }

                apply(null);
                box.Text = string.Empty;
                _onChanged?.Invoke();
            };
            Grid.SetColumn(clear, 3);
            row.Children.Add(clear);
            panel.Children.Add(row);
        }

        /// <summary>
        /// Labeled text row. The value is written through on every keystroke, so a host that
        /// saves on its own button always sees the latest text; <paramref name="onCommit"/>
        /// fires once per edit on focus loss or Enter.
        /// </summary>
        internal static TextBox AddTextBox(
            Panel panel,
            string label,
            string value,
            Action<string> apply = null,
            Action onCommit = null)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var labelBlock = CreateLabel(label);
            labelBlock.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
            labelBlock.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(labelBlock);
            var box = new TextBox
            {
                Text = value ?? string.Empty,
                MinHeight = 30,
                Padding = new Thickness(7, 4, 7, 4)
            };
            if (apply != null)
            {
                var pending = false;
                box.TextChanged += (_, __) =>
                {
                    apply(box.Text?.Trim());
                    pending = true;
                };
                AttachCommit(box, () =>
                {
                    if (pending)
                    {
                        pending = false;
                        onCommit?.Invoke();
                    }
                });
            }

            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            panel.Children.Add(row);

            return box;
        }

        // Enter is left unhandled so a dialog's default button still fires.
        private static void AttachCommit(TextBox box, Action commit)
        {
            box.LostFocus += (_, __) => commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    commit();
                }
            };
        }

        internal static TextBlock CreateLabel(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            return block;
        }
    }
}
