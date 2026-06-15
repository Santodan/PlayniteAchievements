using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    /// <summary>
    /// Service for managing fullscreen overlay achievement windows.
    /// Handles window creation, display, and closing for fullscreen mode.
    /// Follows SuccessStoryFullscreenHelper pattern for opening windows,
    /// but restores original selection when window closes.
    /// </summary>
    public sealed class FullscreenWindowService : IDisposable
    {
        private static readonly TimeSpan ControllerBackReactivationIgnoreWindow = TimeSpan.FromMilliseconds(500);

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action<Guid?> _requestSingleGameThemeUpdate;
        private Dispatcher UiDispatcher => _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        private Window _achievementsWindow;
        private Guid? _originalSelectedGameId;
        private bool _isTransitioning;
        private bool _overlayWasDeactivated;
        private DateTimeOffset? _ignoreControllerBackUntilUtc;
        public bool IsOverlayWindowOpen => _achievementsWindow?.IsVisible == true;

        public FullscreenWindowService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            Action<Guid?> requestSingleGameThemeUpdate)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _requestSingleGameThemeUpdate = requestSingleGameThemeUpdate ?? throw new ArgumentNullException(nameof(requestSingleGameThemeUpdate));
        }

        public void Dispose()
        {
            CloseOverlayWindowIfOpen();
        }

        /// <summary>
        /// Opens the all-games achievement overview window.
        /// </summary>
        public void OpenOverviewWindow()
        {
            _originalSelectedGameId = GetSingleSelectedGameId();
            ShowAchievementsWindow(styleKey: "AchievementsWindow");
        }

        /// <summary>
        /// Opens the View Achievements fullscreen window for a specific game.
        /// Changes Playnite's selection so theme bindings resolve to this game.
        /// </summary>
        public void OpenViewAchievementsWindow(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            if (_achievementsWindow == null || !_achievementsWindow.IsVisible)
            {
                _originalSelectedGameId = GetSingleSelectedGameId();
            }

            // Change Playnite's selection so theme bindings ({PluginSettings}, {Binding SelectedGame...})
            // resolve to this game's data. This is the SuccessStoryFullscreenHelper pattern.
            SelectGame(gameId);
            ShowAchievementsWindow(styleKey: "GameAchievementsWindow");
        }

        /// <summary>
        /// Closes the overlay window if it's currently open.
        /// </summary>
        public void CloseOverlayWindowIfOpen()
        {
            try
            {
                var dispatcher = UiDispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    if (_achievementsWindow != null && _achievementsWindow.IsVisible)
                    {
                        _achievementsWindow.Close();
                    }
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_achievementsWindow != null && _achievementsWindow.IsVisible)
                        {
                            _achievementsWindow.Close();
                        }
                    }), DispatcherPriority.Background);
                }
            }
            catch
            {
                // Ignore errors when closing window
            }
        }

        public void HandleControllerBackPressed()
        {
            try
            {
                var dispatcher = UiDispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    TryCloseOverlayForControllerBackOnUiThread();
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(TryCloseOverlayForControllerBackOnUiThread), DispatcherPriority.Background);
                }
            }
            catch
            {
                // Ignore errors when handling global controller input.
            }
        }

        private void TryCloseOverlayForControllerBackOnUiThread()
        {
            if (_achievementsWindow?.IsVisible != true)
            {
                return;
            }

            var activeWindow = Application.Current?.Windows?
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive);

            if (!ReferenceEquals(activeWindow, _achievementsWindow))
            {
                return;
            }

            if (_ignoreControllerBackUntilUtc.HasValue &&
                DateTimeOffset.UtcNow < _ignoreControllerBackUntilUtc.Value)
            {
                return;
            }

            _ignoreControllerBackUntilUtc = null;
            try { _achievementsWindow.Close(); } catch { }
        }

        private void SelectGame(Guid gameId)
        {
            try
            {
                var selectedGame = _api?.Database?.Games?.Get(gameId);
                if (selectedGame != null)
                {
                    _settings.SetSelectedGame(selectedGame);
                }
            }
            catch
            {
                // Ignore failures and let the normal selection flow populate SelectedGame.
            }

            // Change Playnite's main selection - this triggers OnGameSelected which
            // updates SelectedGame and theme data for the new selection
            try { _api?.MainView?.SelectGame(gameId); } catch { }
            // Request theme data update for the selected game
            try { _requestSingleGameThemeUpdate(gameId); } catch { }
        }

        private Guid? GetSingleSelectedGameId()
        {
            try
            {
                var selected = _api?.MainView?.SelectedGames?
                    .Where(g => g != null)
                    .Take(2)
                    .ToList();

                if (selected == null || selected.Count != 1)
                {
                    return null;
                }

                return selected[0].Id;
            }
            catch
            {
                return null;
            }
        }

        private void ShowAchievementsWindow(string styleKey)
        {
            if (string.IsNullOrWhiteSpace(styleKey))
            {
                return;
            }

            try
            {
                var dispatcher = UiDispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                if (dispatcher.CheckAccess())
                {
                    OpenOverlayWindowOnUiThread(styleKey);
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(() => OpenOverlayWindowOnUiThread(styleKey)), DispatcherPriority.Background);
                }
            }
            catch
            {
                // Ignore errors when opening window
            }
        }

        private void OpenOverlayWindowOnUiThread(string styleKey)
        {
            try
            {
                if (_achievementsWindow != null && _achievementsWindow.IsVisible)
                {
                    // Set transitioning flag so Closed handler skips restore
                    // (we're switching windows, not closing the final one)
                    _isTransitioning = true;
                    _achievementsWindow.Close();
                    _isTransitioning = false;
                }
            }
            catch
            {
                _isTransitioning = false;
            }

            // Match SuccessStoryFullscreenHelper behavior as closely as possible (Aniki ReMake expects this).
            var window = _api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false
            });

            var parent = _api.Dialogs.GetCurrentAppWindow();
            if (parent != null)
            {
                window.Owner = parent;
            }
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Title = "Achievements";

            window.Height = parent != null && parent.Height > 0 ? parent.Height : SystemParameters.PrimaryScreenHeight;
            window.Width = parent != null && parent.Width > 0 ? parent.Width : SystemParameters.PrimaryScreenWidth;

            var xamlString = $@"
                <Viewbox Stretch=""Uniform""
                        xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                        xmlns:pbeh=""clr-namespace:Playnite.Behaviors;assembly=Playnite"">
                    <Grid Width=""1920"" Height=""1080"">
                        <ContentControl x:Name=""AchievementsWindow""
                                        Focusable=""False""
                                        Style=""{{DynamicResource {styleKey}}}"" />
                    </Grid>
                </Viewbox>";

            var content = (FrameworkElement)XamlReader.Parse(xamlString);
            content.DataContext = _settings;

            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    try { window.Close(); } catch { }
                    e.Handled = true;
                }
            };

            window.Activated += (_, __) =>
            {
                if (ReferenceEquals(_achievementsWindow, window) && _overlayWasDeactivated)
                {
                    _ignoreControllerBackUntilUtc = DateTimeOffset.UtcNow.Add(
                        ControllerBackReactivationIgnoreWindow);
                    _overlayWasDeactivated = false;
                }
            };

            window.Deactivated += (_, __) =>
            {
                if (ReferenceEquals(_achievementsWindow, window) && window.IsVisible)
                {
                    _overlayWasDeactivated = true;
                }
            };

            window.Closed += (_, __) =>
            {
                if (ReferenceEquals(_achievementsWindow, window))
                {
                    _achievementsWindow = null;
                    _overlayWasDeactivated = false;
                    _ignoreControllerBackUntilUtc = null;
                }

                // Restore original selection when window closes (but not during transitions)
                if (!_isTransitioning && _originalSelectedGameId.HasValue)
                {
                    try { _api?.MainView?.SelectGame(_originalSelectedGameId.Value); } catch { }
                    try { _requestSingleGameThemeUpdate(_originalSelectedGameId); } catch { }
                    _originalSelectedGameId = null;
                }
            };

            window.Content = content;
            _achievementsWindow = window;

            window.ShowDialog();
        }
    }
}
