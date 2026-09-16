using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    public sealed class ScreenshotSlideshowControl : UserControl, IDisposable
    {
        private static readonly string PreviousGlyph = char.ConvertFromUtf32(0xE76B);
        private static readonly string NextGlyph = char.ConvertFromUtf32(0xE76C);
        private static readonly string PlayGlyph = char.ConvertFromUtf32(0xE768);
        private static readonly string PauseGlyph = char.ConvertFromUtf32(0xE769);
        private static readonly string FullscreenGlyph = char.ConvertFromUtf32(0xE740);

        // The info panel takes a share of the widget's width, clamped so it neither shrinks below
        // readability nor eats a wide tile, and gives up entirely on a tile too narrow to leave
        // the image usable.
        private const double InfoPanelWidthRatio = 0.32;
        private const double InfoPanelMinWidth = 160;
        private const double InfoPanelMaxWidth = 280;
        private const double InfoPanelMinWidgetWidth = 420;
        private const double InfoPanelHeightRatio = 0.4;
        private const double InfoPanelMinHeight = 140;
        private const double InfoPanelMaxHeight = 300;
        private const double InfoPanelMinWidgetHeight = 360;

        private readonly ShowcaseWidgetInstanceSettings _settings;
        private readonly Image _image;
        private readonly TextBlock _status;
        private readonly TextBlock _caption;
        private readonly TextBlock _position;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly Button _pause;
        private readonly Button _fullscreen;
        private readonly Border _captionBar;
        private readonly Border _transport;
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new Random();
        private CaptureLibraryService _captureLibrary;
        private IReadOnlyList<CaptureItem> _items = Array.Empty<CaptureItem>();
        private int _index;
        private int _reloadVersion;
        private bool _paused;
        private bool _editHold;
        private bool _mediaHovered;
        private ShowcaseScreenshotVariant? _loadedVariant;
        private bool? _loadedShuffle;
        private ShowcaseImageFitMode? _loadedFit;
        private ShowcaseSlideshowSource? _loadedSource;
        private string _loadedCollectionId;
        private ShowcaseInfoPanelPosition? _loadedInfoPanel;
        private DockPanel _mediaPair;
        private Border _mediaFrame;
        private ScreenshotInfoPanel _infoPanel;
        private IReadOnlyList<AchievementDisplayItem> _rowsSource;
        private Dictionary<string, AchievementDisplayItem> _rowsByKey;
        private int _indexVersion;

        public ScreenshotSlideshowControl(ShowcaseWidgetInstanceSettings settings)
        {
            _settings = settings ?? ShowcaseWidgetSettingsFactory.CreateDefault(
                ShowcaseWidgetKind.ScreenshotSlideshow);
            _image = new Image
            {
                Margin = new Thickness(8),
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _status = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18),
                Opacity = 0.68
            };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            _caption = CreateTextBlock(FontWeights.SemiBold);
            _caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _position = CreateTextBlock(FontWeights.Normal);
            _position.Opacity = 0.65;

            _previous = CreateGlyphButton(
                PreviousGlyph,
                "PlayAch.Capture.NavButtonStyle",
                "LOCPlayAch_Showcase_PreviousScreenshot",
                () => Move(-1));
            _previous.Margin = new Thickness(0, 0, 8, 0);
            _next = CreateGlyphButton(
                NextGlyph,
                "PlayAch.Capture.NavButtonStyle",
                "LOCPlayAch_Showcase_NextScreenshot",
                () => Move(1));
            _next.Margin = new Thickness(8, 0, 0, 0);
            _pause = CreateGlyphButton(
                PauseGlyph,
                "PlayAch.Capture.GlyphButtonStyle",
                "LOCPlayAch_Showcase_PauseSlideshow",
                TogglePause);
            _fullscreen = CreateGlyphButton(
                FullscreenGlyph,
                "PlayAch.Capture.GlyphButtonStyle",
                "LOCPlayAch_Captures_Fullscreen",
                OpenFullscreen);

            _transport = BuildTransport();
            _captionBar = BuildCaptionBar();
            _timer = new DispatcherTimer();
            _timer.Tick += Timer_Tick;
            Content = Build();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        /// <summary>True when this control was built for the given widget instance, so a
        /// projection re-apply can keep the running slideshow instead of recreating it.</summary>
        public bool IsFor(ShowcaseWidgetInstanceSettings instance) =>
            ReferenceEquals(_settings, instance);

        /// <summary>
        /// Re-applies this widget's own options after a projection re-apply. Deltas only: an
        /// unchanged option set leaves the playback order, position, and timer phase alone, so
        /// edits to other widgets never disturb a running slideshow.
        /// </summary>
        public void RefreshOptions()
        {
            var interval = TimeSpan.FromSeconds(Math.Max(
                2,
                ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(_settings)));
            if (_timer.Interval != interval)
            {
                _timer.Interval = interval;
            }

            // The info panel is pure presentation, so a change relays out and refills it rather
            // than reloading the playlist. Handled before the reload paths below because those
            // return early, and a scoped slideshow takes one on every single re-apply.
            if (ShowcaseWidgetOptions.GetInfoPanelPosition(_settings) != _loadedInfoPanel)
            {
                ApplyInfoPanelLayout();
                EnsureInfoPanelIndex();
                UpdateInfoPanelContent();
            }

            // A scoped slideshow reloads on every re-apply so pin-collection membership changes
            // are picked up; the reload's same-set fast path keeps an unchanged scope invisible.
            var source = ShowcaseWidgetOptions.GetSlideshowSource(_settings);
            if (ShowcaseWidgetOptions.GetScreenshotVariant(_settings) != _loadedVariant ||
                ShowcaseWidgetOptions.GetShuffle(_settings) != _loadedShuffle ||
                source != _loadedSource ||
                source != ShowcaseSlideshowSource.All ||
                !string.Equals(
                    ShowcaseWidgetOptions.GetPinCollectionId(_settings),
                    _loadedCollectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _ = ReloadAsync();
                return;
            }

            if (ShowcaseWidgetOptions.GetImageFitMode(_settings) != _loadedFit)
            {
                ShowCurrent();
            }
        }

        /// <summary>
        /// Achievement rows the info panel resolves captures against. Rebuilt collections arrive as
        /// fresh row objects, so reference inequality is the signal that the index is stale.
        /// </summary>
        public void SetAchievementRows(IReadOnlyList<AchievementDisplayItem> rows)
        {
            if (ReferenceEquals(_rowsSource, rows))
            {
                return;
            }

            _rowsSource = rows;
            _rowsByKey = null;
            _indexVersion++;
            EnsureInfoPanelIndex();
            UpdateInfoPanelContent();
        }

        /// <summary>
        /// Holds the slideshow still while the dashboard is in edit mode: the timer stops so the
        /// image does not change mid-edit, and resumes on exit unless the user paused it.
        /// </summary>
        public void SetEditHold(bool hold)
        {
            if (_editHold == hold)
            {
                return;
            }

            _editHold = hold;
            if (hold)
            {
                _timer.Stop();
            }
            else if (!_paused && IsLoaded)
            {
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _reloadVersion++;
            _timer.Stop();
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged -= CaptureLibrary_Changed;
                _captureLibrary = null;
            }
        }

        private UIElement Build()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var mediaRow = new Grid();
            mediaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mediaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mediaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mediaRow.Children.Add(_previous);

            var mediaLayers = new Grid();
            mediaLayers.Children.Add(_image);
            mediaLayers.Children.Add(_status);
            mediaLayers.Children.Add(_transport);
            var mediaFrame = new Border
            {
                Child = mediaLayers,
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };
            _mediaFrame = mediaFrame;
            mediaFrame.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Border");
            mediaFrame.MouseLeftButtonUp += (_, args) =>
            {
                if (args.ClickCount > 1)
                {
                    OpenFullscreen();
                    args.Handled = true;
                }
            };
            // The pause/fullscreen transport only shows while the pointer is over the image, so
            // the idle slideshow stays chrome-free.
            mediaFrame.MouseEnter += (_, __) =>
            {
                _mediaHovered = true;
                UpdateChromeVisibility();
            };
            mediaFrame.MouseLeave += (_, __) =>
            {
                _mediaHovered = false;
                UpdateChromeVisibility();
            };
            // The image and the info panel share the centre column so the prev/next arrows keep
            // flanking the pair rather than the image alone. A DockPanel gives all three panel
            // positions for free; the image is the last child so it fills whatever is left.
            // Which side the panel takes, and whether it takes one at all, is settled by
            // ApplyInfoPanelLayout.
            _infoPanel = new ScreenshotInfoPanel { Visibility = Visibility.Collapsed };
            _mediaPair = new DockPanel { LastChildFill = true };
            _mediaPair.Children.Add(_infoPanel);
            _mediaPair.Children.Add(mediaFrame);
            Grid.SetColumn(_mediaPair, 1);
            mediaRow.Children.Add(_mediaPair);
            Grid.SetColumn(_next, 2);
            mediaRow.Children.Add(_next);
            root.Children.Add(mediaRow);

            Grid.SetRow(_captionBar, 1);
            root.Children.Add(_captionBar);
            ApplyInfoPanelLayout();
            return root;
        }

        private Border BuildTransport()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(_pause);
            panel.Children.Add(_fullscreen);
            return new Border
            {
                Child = panel,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(12, 0, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };
        }

        private Border BuildCaptionBar()
        {
            var captionGrid = new Grid();
            captionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            captionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            captionGrid.Children.Add(_caption);
            Grid.SetColumn(_position, 1);
            _position.Margin = new Thickness(10, 0, 0, 0);
            captionGrid.Children.Add(_position);
            return new Border
            {
                Child = captionGrid,
                Padding = new Thickness(2, 7, 2, 0)
            };
        }

        private static TextBlock CreateTextBlock(FontWeight weight)
        {
            var text = new TextBlock
            {
                FontWeight = weight,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            return text;
        }

        private static Button CreateGlyphButton(
            string glyph,
            string styleKey,
            string localizationKey,
            Action action)
        {
            var label = Localize(localizationKey);
            var button = new Button
            {
                Content = glyph,
                ToolTip = label
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            AutomationProperties.SetName(button, label);
            button.Click += (_, __) => action();
            return button;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged -= CaptureLibrary_Changed;
            }

            _captureLibrary = PlayniteAchievementsPlugin.Instance?.CaptureLibraryService;
            if (_captureLibrary != null)
            {
                _captureLibrary.CapturesChanged += CaptureLibrary_Changed;
            }

            _timer.Interval = TimeSpan.FromSeconds(Math.Max(
                2,
                ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(_settings)));
            if (!_paused && !_editHold)
            {
                _timer.Start();
            }

            UpdateChromeVisibility();
            ApplyInfoPanelLayout();
            EnsureInfoPanelIndex();
            _ = ReloadAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Height matters too: a bottom-docked panel is sized against it.
            if (e.WidthChanged || e.HeightChanged)
            {
                ApplyInfoPanelLayout();
            }
        }

        private void UpdateChromeVisibility()
        {
            var canNavigate = _items.Count > 1;
            _previous.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
            _next.Visibility = canNavigate ? Visibility.Visible : Visibility.Collapsed;
            _captionBar.Visibility = Visibility.Visible;
            _transport.Visibility = _items.Count > 0 && _mediaHovered
                ? Visibility.Visible
                : Visibility.Collapsed;
            _pause.IsEnabled = _items.Count > 1;
            _fullscreen.IsEnabled = _items.Count > 0;
        }

        private void CaptureLibrary_Changed(object sender, CapturesChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _ = ReloadAsync()));
        }

        /// <summary>
        /// Sanitized capture folder names (and, for achievement collections, the allowed
        /// achievement stems per folder) for the configured pin-collection scope. Folders is null
        /// when the slideshow draws from the whole library; Stems is null when a folder's every
        /// capture qualifies.
        /// </summary>
        private (IReadOnlyCollection<string> Folders, Dictionary<string, HashSet<string>> Stems)
            ResolveScope(ShowcaseSlideshowSource source)
        {
            if (source == ShowcaseSlideshowSource.All)
            {
                return (null, null);
            }

            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            var collectionId = ShowcaseWidgetOptions.GetPinCollectionId(_settings);
            var games = PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Database?.Games;
            if (source == ShowcaseSlideshowSource.GameCollection)
            {
                var collection = ShowcasePinService.ResolveGameCollection(showcase, collectionId);
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var gameId in collection?.GameIds ?? new List<Guid>())
                {
                    var folder = UnlockScreenshotService.SanitizeCaptureGameName(
                        games?.Get(gameId)?.Name);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        folders.Add(folder);
                    }
                }

                return (folders, null);
            }

            var achievementCollection = ShowcasePinService.ResolveAchievementCollection(
                showcase,
                collectionId);
            var stems = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in achievementCollection?.Pins ?? new List<PinnedAchievementReference>())
            {
                if (pin == null)
                {
                    continue;
                }

                var gameName = games?.Get(pin.GameId)?.Name ?? pin.LastKnownGameName;
                var folder = UnlockScreenshotService.SanitizeCaptureGameName(gameName);
                var stem = AchievementIconCachePathBuilder.SanitizeSegment(
                    pin.LastKnownAchievementName);
                if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem))
                {
                    continue;
                }

                if (!stems.TryGetValue(folder, out var folderStems))
                {
                    folderStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    stems[folder] = folderStems;
                }

                folderStems.Add(stem);
            }

            return (stems.Keys.ToList(), stems);
        }

        private async Task ReloadAsync()
        {
            var captureLibrary = _captureLibrary;
            var reloadVersion = ++_reloadVersion;
            var selectedVariant = ShowcaseWidgetOptions.GetScreenshotVariant(_settings);
            var selectedSource = ShowcaseWidgetOptions.GetSlideshowSource(_settings);
            var selectedCollectionId = ShowcaseWidgetOptions.GetPinCollectionId(_settings);
            var scope = ResolveScope(selectedSource);
            CaptureVariant? captureVariant = null;
            switch (selectedVariant)
            {
                case ShowcaseScreenshotVariant.Clean:
                    captureVariant = CaptureVariant.Clean;
                    break;
                case ShowcaseScreenshotVariant.Notification:
                    captureVariant = CaptureVariant.Notification;
                    break;
                case ShowcaseScreenshotVariant.Framed:
                    captureVariant = CaptureVariant.Framed;
                    break;
            }

            // Only announce loading when nothing is on screen yet; a reload behind a visible
            // image (a re-parent during layout edits, a capture event) keeps the image up.
            if (_items.Count == 0)
            {
                _status.Text = Localize("LOCPlayAch_Showcase_LoadingScreenshots");
                _status.Visibility = Visibility.Visible;
            }

            IReadOnlyList<CaptureItem> items;
            try
            {
                items = captureLibrary == null
                    ? (IReadOnlyList<CaptureItem>)Array.Empty<CaptureItem>()
                    : await Task.Run(() => captureLibrary.GetScreenshots(
                        captureVariant,
                        gameFolders: scope.Folders));
            }
            catch
            {
                items = Array.Empty<CaptureItem>();
            }

            if (scope.Stems != null)
            {
                items = items
                    .Where(item =>
                        scope.Stems.TryGetValue(
                            CaptureAchievementIndex.GetCaptureFolderName(item.FilePath),
                            out var stems) &&
                        stems.Contains(item.AchievementStem ?? string.Empty))
                    .ToList();
            }

            if (reloadVersion != _reloadVersion ||
                !ReferenceEquals(_captureLibrary, captureLibrary) ||
                !IsLoaded)
            {
                return;
            }

            var shuffle = ShowcaseWidgetOptions.GetShuffle(_settings);
            if (_items.Count > 0 &&
                selectedVariant == _loadedVariant &&
                shuffle == _loadedShuffle &&
                selectedSource == _loadedSource &&
                string.Equals(selectedCollectionId, _loadedCollectionId, StringComparison.OrdinalIgnoreCase) &&
                SameItemSet(items))
            {
                // Same files under the same options: keep the playback order, position, and
                // timer phase so reloads triggered by layout churn are invisible.
                return;
            }

            var currentPath = Current?.FilePath;
            _loadedVariant = selectedVariant;
            _loadedShuffle = shuffle;
            _loadedSource = selectedSource;
            _loadedCollectionId = selectedCollectionId;
            _items = CreatePlaybackOrder(items);
            _index = ResolveIndex(currentPath);
            ShowCurrent();
        }

        private bool SameItemSet(IReadOnlyList<CaptureItem> items)
        {
            if (items == null || items.Count != _items.Count)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                seen.Add(item.FilePath);
            }

            foreach (var item in items)
            {
                if (!seen.Remove(item.FilePath))
                {
                    return false;
                }
            }

            return seen.Count == 0;
        }

        private IReadOnlyList<CaptureItem> CreatePlaybackOrder(IReadOnlyList<CaptureItem> items)
        {
            if (items == null || items.Count < 2 || !ShowcaseWidgetOptions.GetShuffle(_settings))
            {
                return items ?? Array.Empty<CaptureItem>();
            }

            var shuffled = new List<CaptureItem>(items);
            for (var index = shuffled.Count - 1; index > 0; index--)
            {
                var swapIndex = _random.Next(index + 1);
                var temporary = shuffled[index];
                shuffled[index] = shuffled[swapIndex];
                shuffled[swapIndex] = temporary;
            }

            return shuffled;
        }

        private int ResolveIndex(string previousPath)
        {
            if (_items.Count == 0)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(previousPath))
            {
                for (var index = 0; index < _items.Count; index++)
                {
                    if (string.Equals(
                            _items[index].FilePath,
                            previousPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }
            }

            return Math.Max(0, Math.Min(_index, _items.Count - 1));
        }

        private CaptureItem Current =>
            _index >= 0 && _index < _items.Count ? _items[_index] : null;

        private void Timer_Tick(object sender, EventArgs e) => Move(1);

        private void TogglePause()
        {
            _paused = !_paused;
            var label = _paused
                ? Localize("LOCPlayAch_Showcase_ResumeSlideshow")
                : Localize("LOCPlayAch_Showcase_PauseSlideshow");
            _pause.Content = _paused ? PlayGlyph : PauseGlyph;
            _pause.ToolTip = label;
            AutomationProperties.SetName(_pause, label);
            if (_paused)
            {
                _timer.Stop();
            }
            else if (!_editHold)
            {
                _timer.Start();
            }
        }

        private void Move(int direction)
        {
            if (_items.Count < 2)
            {
                return;
            }

            _index = (_index + Math.Sign(direction) + _items.Count) % _items.Count;

            ShowCurrent();
        }

        private void OpenFullscreen()
        {
            var current = Current;
            if (current != null)
            {
                FullscreenMediaViewerPresenter.Show(this, current.FilePath, false);
            }
        }

        private void ShowCurrent()
        {
            var fit = ShowcaseWidgetOptions.GetImageFitMode(_settings);
            _loadedFit = fit;
            _image.Stretch = fit == ShowcaseImageFitMode.Fill
                ? Stretch.UniformToFill
                : Stretch.Uniform;
            var current = Current;
            if (current == null)
            {
                _image.Source = null;
                _caption.Text = string.Empty;
                _position.Text = string.Empty;
                _status.Text = Localize("LOCPlayAch_Showcase_NoScreenshots");
                _status.Visibility = Visibility.Visible;
                UpdateChromeVisibility();
                return;
            }

            var renderedEdge = Math.Max(ActualWidth, ActualHeight);
            var decodePixel = (int)Math.Max(
                720,
                Math.Min(3840, Math.Ceiling(renderedEdge * 1.5)));
            AsyncImage.SetDecodePixel(_image, decodePixel);
            AsyncImage.SetUri(_image, current.FilePath);
            _caption.Text = (current.AchievementStem ?? string.Empty).Replace('_', ' ');
            _position.Text = $"{_index + 1} / {_items.Count}";
            _status.Visibility = Visibility.Collapsed;
            UpdateInfoPanelContent();
            UpdateChromeVisibility();
        }

        /// <summary>
        /// Places the info panel, sizes it against the current width, and collapses it on a tile
        /// too narrow to spare the room. The caption's achievement name is suppressed whenever the
        /// panel is showing one, so the two never duplicate each other.
        /// </summary>
        private void ApplyInfoPanelLayout()
        {
            var position = ShowcaseWidgetOptions.GetInfoPanelPosition(_settings);
            _loadedInfoPanel = position;
            if (_mediaPair == null || _infoPanel == null)
            {
                return;
            }

            var bottom = position == ShowcaseInfoPanelPosition.Bottom;
            var extent = bottom ? ResolveInfoPanelHeight() : ResolveInfoPanelWidth();
            var showPanel = position != ShowcaseInfoPanelPosition.Off && extent > 0;
            _infoPanel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
            _caption.Visibility = showPanel ? Visibility.Collapsed : Visibility.Visible;

            switch (position)
            {
                case ShowcaseInfoPanelPosition.Left:
                    DockPanel.SetDock(_infoPanel, Dock.Left);
                    break;
                case ShowcaseInfoPanelPosition.Bottom:
                    DockPanel.SetDock(_infoPanel, Dock.Bottom);
                    break;
                default:
                    DockPanel.SetDock(_infoPanel, Dock.Right);
                    break;
            }

            // The docked panel is sized on its docking axis only; the other axis stretches, so it
            // is left unset rather than pinned.
            _infoPanel.Width = bottom || !showPanel ? double.NaN : extent;
            _infoPanel.Height = bottom && showPanel ? extent : double.NaN;
        }

        /// <summary>Info panel width for the current widget width; 0 means there is no room.</summary>
        private double ResolveInfoPanelWidth()
        {
            // Before the first layout pass the size is unknown; staying collapsed avoids showing
            // a panel that the following pass would immediately take away.
            if (ActualWidth < InfoPanelMinWidgetWidth)
            {
                return 0;
            }

            return Math.Max(
                InfoPanelMinWidth,
                Math.Min(InfoPanelMaxWidth, ActualWidth * InfoPanelWidthRatio));
        }

        /// <summary>
        /// Info panel height for a bottom-docked panel; 0 means there is no room. The share is
        /// larger than the side panels' because the details stack vertically either way, so a
        /// bottom strip needs the height to show more than a line or two.
        /// </summary>
        private double ResolveInfoPanelHeight()
        {
            if (ActualHeight < InfoPanelMinWidgetHeight)
            {
                return 0;
            }

            return Math.Max(
                InfoPanelMinHeight,
                Math.Min(InfoPanelMaxHeight, ActualHeight * InfoPanelHeightRatio));
        }

        /// <summary>
        /// Builds the capture-to-achievement index off the UI thread, and only while the info panel
        /// is actually showing, so an Off panel costs nothing.
        /// </summary>
        private void EnsureInfoPanelIndex()
        {
            if (ShowcaseWidgetOptions.GetInfoPanelPosition(_settings) == ShowcaseInfoPanelPosition.Off ||
                _rowsByKey != null)
            {
                return;
            }

            var rows = _rowsSource;
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            var version = _indexVersion;
            Task.Run(() => CaptureAchievementIndex.Build(
                    rows,
                    row => row.GameName,
                    row => row.DisplayName))
                .ContinueWith(
                    task =>
                    {
                        if (version != _indexVersion)
                        {
                            return;
                        }

                        _rowsByKey = task.Result;
                        UpdateInfoPanelContent();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Points the info panel at the achievement behind the current capture. An unmatched
        /// capture (a renamed achievement, a game that left the library) falls back to a row
        /// carrying only the name and game recovered from the file path, and the panel's own
        /// per-field visibility drops the rows it cannot fill.
        /// </summary>
        private void UpdateInfoPanelContent()
        {
            if (_infoPanel == null ||
                ShowcaseWidgetOptions.GetInfoPanelPosition(_settings) == ShowcaseInfoPanelPosition.Off)
            {
                return;
            }

            var current = Current;
            if (current == null)
            {
                _infoPanel.DataContext = null;
                return;
            }

            AchievementDisplayItem row = null;
            _rowsByKey?.TryGetValue(
                CaptureAchievementIndex.KeyForCapture(current.FilePath, current.AchievementStem),
                out row);
            _infoPanel.DataContext = row ?? new AchievementDisplayItem
            {
                DisplayName = (current.AchievementStem ?? string.Empty).Replace('_', ' '),
                GameName = CaptureAchievementIndex.GetCaptureFolderName(current.FilePath)
            };
        }
    }
}
