using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    public sealed class ShowcaseWidgetSettingsDialog : UserControl
    {
        private const double DialogWidth = 460;
        private const double MinimumDialogHeight = 200;
        private const double FallbackDialogHeight = 520;
        private const string WindowPlacementKey = "ShowcaseWidgetSettings";

        private readonly ShowcaseWidgetInstanceSettings _sourceWidget;
        private readonly ShowcaseWidgetInstanceSettings _workingWidget;
        private readonly ShowcaseProfileSettings _workingProfile;
        private TextBox _titleBox;
        private ShowcaseProfileSettingsEditor _profileEditor;

        private ShowcaseWidgetSettingsDialog(ShowcaseWidgetInstanceSettings widget)
        {
            _sourceWidget = widget ?? throw new ArgumentNullException(nameof(widget));
            _workingWidget = widget.Clone();
            _workingProfile = (widget.Profile ?? new ShowcaseProfileSettings()).Clone();
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

        public static bool Show(ShowcaseWidgetInstanceSettings widget)
        {
            if (widget == null)
            {
                return false;
            }

            var editor = new ShowcaseWidgetSettingsDialog(widget);
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

            _titleBox = ShowcaseProfileSettingsEditor.AddTextBox(
                panel,
                Localize("LOCPlayAch_Showcase_CustomTitle"),
                _workingWidget.CustomTitle);

            if (_workingWidget.Kind == ShowcaseWidgetKind.Profile)
            {
                // Edits the clone; Save copies it back and Cancel discards it.
                _profileEditor = new ShowcaseProfileSettingsEditor(_workingProfile);
                panel.Children.Add(_profileEditor);
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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _sourceWidget.CustomTitle = _titleBox.Text?.Trim();
            _sourceWidget.Options = new Dictionary<string, string>(
                _workingWidget.Options ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            if (_sourceWidget.Kind == ShowcaseWidgetKind.Profile && _profileEditor != null)
            {
                // Per widget, so the card on a duplicated page is edited on its own.
                var profile = _sourceWidget.Profile ?? (_sourceWidget.Profile = new ShowcaseProfileSettings());
                profile.DisplayName = _workingProfile.DisplayName;
                profile.Subtitle = _workingProfile.Subtitle;
                profile.AvatarPath = _workingProfile.AvatarPath;
                profile.BackgroundPath = _workingProfile.BackgroundPath;
                // Saving always stores the list, even empty: from then on the card shows exactly these.
                profile.Links = _profileEditor.CollectProfileLinks();
            }

            Saved = true;
            Window.GetWindow(this)?.Close();
        }
    }
}
