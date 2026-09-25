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
        private readonly List<ProfileLinkRow> _linkRows = new List<ProfileLinkRow>();

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
        /// One row per enabled platform (plus any platform already holding a saved link): a URL
        /// box that replaces the derived link when filled, and a Hide toggle. The derived link,
        /// when there is one, shows as the box's tooltip, and a blank box keeps using it.
        /// </summary>
        private void BuildProfileLinks(Panel panel)
        {
            var header = CreateLabel(Localize("LOCPlayAch_Showcase_ProfileLinks"));
            panel.Children.Add(header);

            IReadOnlyList<Models.Friends.FriendIdentity> identities = null;
            try
            {
                identities = PlayniteAchievementsPlugin.Instance?.FriendCacheManager?.LoadCurrentUserIdentities();
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Debug($"[Showcase] Profile link identities unavailable: {ex.Message}");
            }

            var derivedByProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in identities ?? Array.Empty<Models.Friends.FriendIdentity>())
            {
                var url = ShowcaseProfileResolver.DeriveProfileUrl(identity?.ProviderKey, identity?.ExternalUserId);
                if (url != null && !derivedByProvider.ContainsKey(identity.ProviderKey))
                {
                    derivedByProvider[identity.ProviderKey] = url;
                }
            }

            var saved = (_workingProfile.Links ?? new List<ShowcaseProfileLink>())
                .Where(link => !string.IsNullOrWhiteSpace(link?.ProviderKey))
                .GroupBy(link => link.ProviderKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var registry = PlayniteAchievementsPlugin.Instance?.ProviderRegistry;
            var providerKeys = (registry?.GetAllProviders() ?? Array.Empty<Providers.IDataProvider>())
                .Select(provider => provider?.ProviderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key) &&
                              !string.Equals(key, "Manual", StringComparison.OrdinalIgnoreCase) &&
                              (registry.IsProviderEnabled(key) || saved.ContainsKey(key)))
                .ToList();

            foreach (var key in providerKeys)
            {
                saved.TryGetValue(key, out var link);
                derivedByProvider.TryGetValue(key, out var derived);
                _linkRows.Add(AddLinkRow(panel, key, link, derived));
            }
        }

        private ProfileLinkRow AddLinkRow(Panel panel, string providerKey, ShowcaseProfileLink link, string derived)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Sm");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = Providers.ProviderRegistry.GetLocalizedName(providerKey),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
            label.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(label);
            var box = new TextBox
            {
                Text = link?.Url ?? string.Empty,
                MinHeight = 30,
                Padding = new Thickness(7, 4, 7, 4),
                ToolTip = derived
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            var hide = new CheckBox
            {
                Content = Localize("LOCPlayAch_Common_Hide"),
                IsChecked = link?.Hidden == true,
                VerticalAlignment = VerticalAlignment.Center
            };
            hide.SetResourceReference(MarginProperty, "PlayAch.Thickness.Left.Sm");
            Grid.SetColumn(hide, 2);
            row.Children.Add(hide);
            panel.Children.Add(row);
            return new ProfileLinkRow(providerKey, box, hide);
        }

        // Rows the dialog showed replace their saved entries; entries for platforms it did not
        // show (a provider since disabled) are kept as they were. An entry is only stored when it
        // carries something: a manual URL or a hide.
        private List<ShowcaseProfileLink> CollectProfileLinks()
        {
            var shown = new HashSet<string>(
                _linkRows.Select(row => row.ProviderKey),
                StringComparer.OrdinalIgnoreCase);
            var links = (_workingProfile.Links ?? new List<ShowcaseProfileLink>())
                .Where(link => link != null && !shown.Contains(link.ProviderKey ?? string.Empty))
                .Select(link => link.Clone())
                .ToList();
            foreach (var row in _linkRows)
            {
                var url = row.UrlBox.Text?.Trim();
                var hidden = row.HideBox.IsChecked == true;
                if (!string.IsNullOrWhiteSpace(url) || hidden)
                {
                    links.Add(new ShowcaseProfileLink
                    {
                        ProviderKey = row.ProviderKey,
                        Url = string.IsNullOrWhiteSpace(url) ? null : url,
                        Hidden = hidden
                    });
                }
            }

            return links;
        }

        private sealed class ProfileLinkRow
        {
            public ProfileLinkRow(string providerKey, TextBox urlBox, CheckBox hideBox)
            {
                ProviderKey = providerKey;
                UrlBox = urlBox;
                HideBox = hideBox;
            }

            public string ProviderKey { get; }

            public TextBox UrlBox { get; }

            public CheckBox HideBox { get; }
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
