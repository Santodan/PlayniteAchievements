using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Views.Helpers;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    public sealed class ShowcaseWidgetSettingsDialog : UserControl
    {
        private const string ImagePatterns = "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp";
        private const double DialogWidth = 460;
        private const double MinimumDialogHeight = 200;
        private const double FallbackDialogHeight = 520;
        private const string WindowPlacementKey = "ShowcaseWidgetSettings";

        private readonly ShowcaseWidgetInstanceSettings _sourceWidget;
        private readonly ShowcaseWidgetInstanceSettings _workingWidget;
        private readonly ShowcaseSettings _layout;
        private readonly ShowcaseProfileSettings _workingProfile;
        private TextBox _titleBox;
        private TextBox _profileNameBox;
        private TextBox _profileSubtitleBox;
        private TextBox _avatarBox;
        private TextBox _backgroundBox;
        private readonly List<ShowcaseProfileLink> _links = new List<ShowcaseProfileLink>();
        private IReadOnlyList<KeyValuePair<string, string>> _currentProfileNames = Array.Empty<KeyValuePair<string, string>>();
        private StackPanel _linksPanel;

        private ShowcaseWidgetSettingsDialog(
            ShowcaseWidgetInstanceSettings widget,
            ShowcaseSettings layout)
        {
            _sourceWidget = widget ?? throw new ArgumentNullException(nameof(widget));
            _workingWidget = widget.Clone();
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _workingProfile = (layout.Profile ?? new ShowcaseProfileSettings()).Clone();
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/PlayniteAchievements;component/Resources/PlayAchImplicitControlStyles.xaml",
                    UriKind.Absolute)
            });
            // No fixed size on the control itself: the window is resizable and the content
            // should stretch with it (the option list scrolls when it overflows).
            Content = BuildContent();
            FormattingCulture.Apply(this);
        }

        public bool Saved { get; private set; }

        public static bool Show(
            ShowcaseWidgetInstanceSettings widget,
            ShowcaseSettings layout)
        {
            if (widget == null || layout == null)
            {
                return false;
            }

            var editor = new ShowcaseWidgetSettingsDialog(widget, layout);
            var title = string.Format(
                FormattingCulture.Current,
                Localize("LOCPlayAch_Showcase_WidgetSettingsTitle"),
                GetWidgetName(widget.Kind));
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                editor,
                new WindowOptions
                {
                    Width = DialogWidth,
                    Height = SettingsDialogSizing.MeasureHeight(
                        editor,
                        DialogWidth - 28,
                        MinimumDialogHeight,
                        FallbackDialogHeight),
                    CanBeResizable = true,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            window.MinWidth = 360;
            window.MinHeight = MinimumDialogHeight;
            // The measured height is the first-open default; a saved placement wins.
            WindowPlacementPersistenceService.Attach(window, WindowPlacementKey);

            window.ShowDialog();
            return editor.Saved;
        }

        private UIElement BuildContent()
        {
            var root = new Grid
            {
                Background = Brushes.Transparent
            };
            root.SetResourceReference(MarginProperty, "PlayAch.Thickness.CardPadding");
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var panel = new StackPanel();
            scroll.Content = panel;
            root.Children.Add(scroll);

            _titleBox = AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_CustomTitle"),
                _workingWidget.CustomTitle);

            if (_workingWidget.Kind == ShowcaseWidgetKind.Profile)
            {
                BuildProfileSettings(panel);
            }

            if (ShowcaseWidgetOptionsControl.HasOptions(_workingWidget.Kind))
            {
                panel.Children.Add(new ShowcaseWidgetOptionsControl(
                    _workingWidget,
                    publishChanges: false,
                    margin: new Thickness(0),
                    loadStyles: false));
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.SetResourceReference(MarginProperty, "PlayAch.Thickness.Top.Md");
            var cancel = new Button
            {
                Content = Localize("LOCPlayAch_Button_Cancel"),
                MinWidth = 82
            };
            cancel.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
            cancel.Click += (_, __) => Window.GetWindow(this)?.Close();
            var save = new Button
            {
                Content = Localize("LOCPlayAch_Button_Save"),
                MinWidth = 82,
                IsDefault = true
            };
            save.Click += Save_Click;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);
            return root;
        }

        private void BuildProfileSettings(Panel panel)
        {
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

            _profileNameBox = AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileName"),
                _workingProfile.DisplayName);
            _profileSubtitleBox = AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileSubtitle"),
                _workingProfile.Subtitle);
            _avatarBox = AddImagePicker(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileAvatar"),
                _workingProfile.AvatarPath);
            _backgroundBox = AddImagePicker(
                panel,
                Localize("LOCPlayAch_Showcase_ProfileBackground"),
                _workingProfile.BackgroundPath);
            BuildProfileLinks(panel);
        }

        /// <summary>
        /// The profile's links as an ordered, editable list: each row is a platform and either
        /// the user's name there or a full link, with move and remove buttons, and the add row
        /// appends one platform at a time. The caption under each box shows the page it will
        /// open, or while blank the platform's address shape. A new row starts from the
        /// signed-in user's stored name when the provider has one. Until links are first saved
        /// the list starts from what the card shows meanwhile: every platform that knows the name.
        /// </summary>
        private void BuildProfileLinks(Panel panel)
        {
            panel.Children.Add(CreateLabel(Localize("LOCPlayAch_Showcase_ProfileLinks")));

            _currentProfileNames = ShowcaseProfileResolver.CurrentUserProfileNames?.Invoke()
                ?? Array.Empty<KeyValuePair<string, string>>();
            _links.Clear();
            _links.AddRange(_workingProfile.Links != null
                ? _workingProfile.Links.Where(link => link != null).Select(link => link.Clone())
                : _currentProfileNames.Select(pair => new ShowcaseProfileLink { ProviderKey = pair.Key, Value = pair.Value }));

            _linksPanel = new StackPanel();
            panel.Children.Add(_linksPanel);
            panel.Children.Add(BuildAddLinkRow());
            RenderLinks();
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
                box.TextChanged += (_, __) =>
                {
                    link.Value = box.Text;
                    UpdateLinkCaption(caption, link);
                };

                // IcoFont arrow-up, arrow-down, and trash, as mapped in Playnite's shipped icofont.ttf.
                AddLinkButton(row, 2, "\uEA5E", "LOCPlayAch_Showcase_MoveEarlier", index > 0,
                    () => MoveLink(index, -1));
                AddLinkButton(row, 3, "\uEA5B", "LOCPlayAch_Showcase_MoveLater", index < _links.Count - 1,
                    () => MoveLink(index, 1));

                AddLinkButton(row, 4, "\uEE09", "LOCPlayAch_Button_Remove", true, () =>
                {
                    _links.RemoveAt(index);
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
            RenderLinks();
        }

        // Saving always stores the list, even empty: from then on the card shows exactly these.
        private List<ShowcaseProfileLink> CollectProfileLinks()
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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _sourceWidget.CustomTitle = _titleBox.Text?.Trim();
            _sourceWidget.Options = new Dictionary<string, string>(
                _workingWidget.Options ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            if (_sourceWidget.Kind == ShowcaseWidgetKind.Profile)
            {
                var profile = _layout.Profile ?? (_layout.Profile = new ShowcaseProfileSettings());
                profile.DisplayName = _profileNameBox.Text?.Trim();
                profile.Subtitle = _profileSubtitleBox.Text?.Trim();
                profile.AvatarPath = ResolveManagedImage(
                    _avatarBox.Text,
                    _workingProfile.AvatarPath,
                    "avatar");
                profile.BackgroundPath = ResolveManagedImage(
                    _backgroundBox.Text,
                    _workingProfile.BackgroundPath,
                    "background");
                profile.Links = CollectProfileLinks();
            }

            Saved = true;
            Window.GetWindow(this)?.Close();
        }

        private string ResolveManagedImage(string selectedPath, string originalPath, string slot)
        {
            var path = selectedPath?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                return originalPath;
            }

            var plugin = PlayniteAchievementsPlugin.Instance;
            var imported = ManagedShowcaseImageService.Import(
                path,
                plugin?.GetPluginUserDataPath(),
                slot);
            if (!string.IsNullOrWhiteSpace(imported))
            {
                plugin?.ImageService?.EvictByUriSegment(imported);
            }

            return imported ?? originalPath;
        }

        private static TextBox AddTextBox(
            Panel panel,
            string label,
            string value)
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
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            panel.Children.Add(row);

            return box;
        }

        private static TextBox AddImagePicker(Panel panel, string label, string value)
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
                Text = value ?? string.Empty,
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
                if (dialog.ShowDialog() == true)
                {
                    box.Text = dialog.FileName;
                }
            };
            Grid.SetColumn(browse, 2);
            row.Children.Add(browse);
            var clear = new Button
            {
                Content = Localize("LOCPlayAch_Button_Clear"),
                MinWidth = 72
            };
            clear.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            clear.Click += (_, __) => box.Text = string.Empty;
            Grid.SetColumn(clear, 3);
            row.Children.Add(clear);
            panel.Children.Add(row);
            return box;
        }

        private static TextBlock CreateLabel(string text)
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
