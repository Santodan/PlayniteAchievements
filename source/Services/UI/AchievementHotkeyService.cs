using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class AchievementHotkeyService : IDisposable
    {
        private const int ViewAchievementsHotkeyId = 0x504101;
        private const int ManageAchievementsHotkeyId = 0x504102;
        private const int OverviewHotkeyId = 0x504103;
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const uint ModNoRepeat = 0x4000;
        private const int WsExToolWindow = 0x00000080;
        private static readonly IntPtr HwndMessage = new IntPtr(-3);

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly AchievementHotkeyTargetResolver _targetResolver;
        private readonly ILogger _logger;
        private readonly Action<Guid> _toggleViewAchievementsWindow;
        private readonly Action<Guid> _toggleManageAchievementsWindow;
        private readonly Action _toggleOverviewWindow;
        private readonly Action _openSettings;
        private readonly Func<bool> _tryFlipCategoryMode;
        private readonly Func<bool> _tryRefreshFocusedView;
        private readonly Dictionary<int, AchievementHotkeyAction> _registeredGlobalHotkeys =
            new Dictionary<int, AchievementHotkeyAction>();

        private bool _started;
        private bool _disposed;
        private HwndSource _globalHotkeySource;
        private IntPtr _globalHotkeyWindowHandle = IntPtr.Zero;
        private AchievementHotkeyGesture _viewGesture = AchievementHotkeyGesture.Empty;
        private AchievementHotkeyGesture _manageGesture = AchievementHotkeyGesture.Empty;
        private AchievementHotkeyGesture _overviewGesture = AchievementHotkeyGesture.Empty;
        private AchievementHotkeyGesture _openSettingsGesture = AchievementHotkeyGesture.Empty;
        private AchievementHotkeyGesture _categoryModeGesture = AchievementHotkeyGesture.Empty;
        private AchievementHotkeyAction? _lastHandledAction;
        private DateTime _lastHandledAtUtc;
        private string _lastGlobalRegistrationFailureSignature;

        public AchievementHotkeyService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            AchievementHotkeyTargetResolver targetResolver,
            ILogger logger,
            Action<Guid> toggleViewAchievementsWindow,
            Action<Guid> toggleManageAchievementsWindow,
            Action toggleOverviewWindow,
            Action openSettings = null,
            Func<bool> tryFlipCategoryMode = null,
            Func<bool> tryRefreshFocusedView = null)
        {
            _api = api;
            _settings = settings;
            _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
            _logger = logger;
            _toggleViewAchievementsWindow = toggleViewAchievementsWindow ?? throw new ArgumentNullException(nameof(toggleViewAchievementsWindow));
            _toggleManageAchievementsWindow = toggleManageAchievementsWindow ?? throw new ArgumentNullException(nameof(toggleManageAchievementsWindow));
            _toggleOverviewWindow = toggleOverviewWindow ?? throw new ArgumentNullException(nameof(toggleOverviewWindow));
            _openSettings = openSettings;
            _tryFlipCategoryMode = tryFlipCategoryMode;
            _tryRefreshFocusedView = tryRefreshFocusedView;
        }

        private Dispatcher UiDispatcher =>
            _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        public void Start()
        {
            if (_disposed || _started)
            {
                return;
            }

            UiDispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _started)
                {
                    return;
                }

                _started = true;
                InputManager.Current.PreProcessInput += OnPreProcessInput;
                RefreshConfigurationCore();
            }), DispatcherPriority.Normal);
        }

        public void RefreshConfiguration()
        {
            if (_disposed)
            {
                return;
            }

            UiDispatcher.BeginInvoke(new Action(RefreshConfigurationCore), DispatcherPriority.Normal);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                UiDispatcher.Invoke(new Action(() =>
                {
                    if (_started)
                    {
                        InputManager.Current.PreProcessInput -= OnPreProcessInput;
                    }

                    UnregisterGlobalHotkeys(disposeSink: true);
                    _started = false;
                }), DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose achievement hotkey service cleanly.");
            }
        }

        private void RefreshConfigurationCore()
        {
            if (_disposed)
            {
                return;
            }

            _viewGesture = ParseGesture(_settings?.Persisted?.ViewAchievementsHotkey);
            _manageGesture = ParseGesture(_settings?.Persisted?.ManageAchievementsHotkey);
            _overviewGesture = ParseGesture(_settings?.Persisted?.OverviewHotkey);
            _openSettingsGesture = ParseGesture(_settings?.Persisted?.OpenSettingsHotkey);
            _categoryModeGesture = ParseGesture(_settings?.Persisted?.CategoryModeHotkey);

            var persisted = _settings?.Persisted;
            var enableGlobalHotkeys = persisted?.EnableAchievementHotkeys == true &&
                                      persisted.EnableGlobalAchievementHotkeys;

            _logger?.Debug(
                $"Refreshing achievement hotkeys. enabled={persisted?.EnableAchievementHotkeys == true}, global={enableGlobalHotkeys}, view='{_viewGesture}', manage='{_manageGesture}', overview='{_overviewGesture}', openSettings='{_openSettingsGesture}', categoryMode='{_categoryModeGesture}', sinkHandle={_globalHotkeyWindowHandle}");

            UnregisterGlobalHotkeys(disposeSink: !enableGlobalHotkeys);

            if (enableGlobalHotkeys)
            {
                RegisterGlobalHotkeys();
            }
            else
            {
                _lastGlobalRegistrationFailureSignature = null;
            }
        }

        private static AchievementHotkeyGesture ParseGesture(string text)
        {
            return AchievementHotkeyGesture.TryParse(text, out var gesture) && gesture != null
                ? gesture
                : AchievementHotkeyGesture.Empty;
        }

        private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (_disposed ||
                e?.StagingItem?.Input is not KeyEventArgs keyArgs ||
                keyArgs.RoutedEvent != Keyboard.KeyDownEvent ||
                keyArgs.IsRepeat ||
                keyArgs.Handled)
            {
                return;
            }

            // Fixed F5 -> refresh, scoped to the plugin's own views via focus. Handled here
            // (before the routed KeyDown) so it preempts Playnite's F5 InputBinding. Independent
            // of the configurable achievement-hotkey feature and active even in a focused search box.
            if (GetEffectiveKey(keyArgs) == Key.F5 &&
                Keyboard.Modifiers == ModifierKeys.None &&
                _tryRefreshFocusedView?.Invoke() == true)
            {
                keyArgs.Handled = true;
                return;
            }

            if (_settings?.Persisted?.EnableAchievementHotkeys != true ||
                KeyboardFocusScope.IsTextInputFocused())
            {
                return;
            }

            var key = GetEffectiveKey(keyArgs);
            if (!AchievementHotkeyGesture.TryCreate(key, Keyboard.Modifiers, out var gesture))
            {
                return;
            }

            if (!TryResolveAction(gesture, out var action))
            {
                // The category-mode gesture is scoped, not app-wide: it is handled synchronously
                // so the key passes through untouched whenever the active window hosts no
                // achievement grid whose category toggle is currently available.
                if (!_categoryModeGesture.IsEmpty &&
                    gesture.Equals(_categoryModeGesture) &&
                    _tryFlipCategoryMode?.Invoke() == true)
                {
                    keyArgs.Handled = true;
                }

                return;
            }

            keyArgs.Handled = true;
            DispatchAction(action);
        }

        private static Key GetEffectiveKey(KeyEventArgs keyArgs)
        {
            if (keyArgs == null)
            {
                return Key.None;
            }

            if (keyArgs.Key == Key.System)
            {
                return keyArgs.SystemKey;
            }

            if (keyArgs.Key == Key.ImeProcessed)
            {
                return keyArgs.ImeProcessedKey;
            }

            return keyArgs.Key;
        }

        private bool TryResolveAction(AchievementHotkeyGesture gesture, out AchievementHotkeyAction action)
        {
            action = AchievementHotkeyAction.ViewAchievements;
            if (gesture == null || gesture.IsEmpty)
            {
                return false;
            }

            if (!_viewGesture.IsEmpty && gesture.Equals(_viewGesture))
            {
                action = AchievementHotkeyAction.ViewAchievements;
                return true;
            }

            if (!_manageGesture.IsEmpty && gesture.Equals(_manageGesture))
            {
                action = AchievementHotkeyAction.ManageAchievements;
                return true;
            }

            if (!_overviewGesture.IsEmpty && gesture.Equals(_overviewGesture))
            {
                action = AchievementHotkeyAction.Overview;
                return true;
            }

            if (_openSettings != null && !_openSettingsGesture.IsEmpty && gesture.Equals(_openSettingsGesture))
            {
                action = AchievementHotkeyAction.OpenSettings;
                return true;
            }

            return false;
        }

        private void DispatchAction(AchievementHotkeyAction action)
        {
            UiDispatcher.BeginInvoke(new Action(() => HandleAction(action)), DispatcherPriority.Normal);
        }

        private void HandleAction(AchievementHotkeyAction action)
        {
            if (_disposed || _settings?.Persisted?.EnableAchievementHotkeys != true)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (_lastHandledAction == action &&
                (now - _lastHandledAtUtc).TotalMilliseconds < 250)
            {
                return;
            }

            _lastHandledAction = action;
            _lastHandledAtUtc = now;

            if (action == AchievementHotkeyAction.Overview)
            {
                _toggleOverviewWindow();
                return;
            }

            if (action == AchievementHotkeyAction.OpenSettings)
            {
                _openSettings?.Invoke();
                return;
            }

            var target = _targetResolver.Resolve();
            if (target?.HasTarget != true)
            {
                ShowNotification(
                    "PlayniteAchievements-Hotkey-NoTarget",
                    ResourceProvider.GetString("LOCPlayAch_Hotkeys_NoTarget"),
                    NotificationType.Info);
                return;
            }

            if (action == AchievementHotkeyAction.ViewAchievements)
            {
                _toggleViewAchievementsWindow(target.GameId);
            }
            else
            {
                _toggleManageAchievementsWindow(target.GameId);
            }
        }

        private void RegisterGlobalHotkeys()
        {
            if (!EnsureGlobalHotkeySink())
            {
                return;
            }

            var failedGestures = new List<string>();
            RegisterGlobalHotkey(ViewAchievementsHotkeyId, AchievementHotkeyAction.ViewAchievements, _viewGesture, failedGestures);
            RegisterGlobalHotkey(ManageAchievementsHotkeyId, AchievementHotkeyAction.ManageAchievements, _manageGesture, failedGestures);
            RegisterGlobalHotkey(OverviewHotkeyId, AchievementHotkeyAction.Overview, _overviewGesture, failedGestures);
            ShowGlobalRegistrationFailureNotification(failedGestures);
        }

        private bool EnsureGlobalHotkeySink()
        {
            if (_globalHotkeySource != null &&
                _globalHotkeyWindowHandle != IntPtr.Zero)
            {
                return true;
            }

            try
            {
                var parameters = new HwndSourceParameters("PlayniteAchievementsHotkeySink")
                {
                    Width = 0,
                    Height = 0,
                    ParentWindow = HwndMessage,
                    WindowStyle = 0,
                    ExtendedWindowStyle = WsExToolWindow
                };

                _globalHotkeySource = new HwndSource(parameters);
                _globalHotkeyWindowHandle = _globalHotkeySource.Handle;
                if (_globalHotkeyWindowHandle == IntPtr.Zero)
                {
                    _logger?.Warn("Could not register achievement global hotkeys because the hotkey sink handle is unavailable.");
                    _globalHotkeySource.Dispose();
                    _globalHotkeySource = null;
                    return false;
                }

                _globalHotkeySource.AddHook(WndProc);
                _logger?.Debug($"Created achievement global hotkey message sink. handle={_globalHotkeyWindowHandle}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to create achievement global hotkey sink.");
                _globalHotkeySource = null;
                _globalHotkeyWindowHandle = IntPtr.Zero;
                return false;
            }
        }

        private void RegisterGlobalHotkey(
            int id,
            AchievementHotkeyAction action,
            AchievementHotkeyGesture gesture,
            ICollection<string> failedGestures)
        {
            if (gesture == null || gesture.IsEmpty || !gesture.CanRegisterGlobally)
            {
                return;
            }

            var modifiers = ToNativeModifiers(gesture.Modifiers) | ModNoRepeat;
            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
            if (virtualKey == 0)
            {
                return;
            }

            if (RegisterHotKey(_globalHotkeyWindowHandle, id, modifiers, virtualKey))
            {
                _registeredGlobalHotkeys[id] = action;
                _logger?.Debug($"Registered global achievement hotkey '{gesture}' for action={action}, id={id}, sinkHandle={_globalHotkeyWindowHandle}.");
                return;
            }

            var errorCode = Marshal.GetLastWin32Error();
            _logger?.Warn($"Failed to register global achievement hotkey '{gesture}' (Win32 error {errorCode}).");
            failedGestures?.Add(gesture.ToString());
        }

        private void ShowGlobalRegistrationFailureNotification(IReadOnlyCollection<string> failedGestures)
        {
            if (failedGestures == null || failedGestures.Count == 0)
            {
                _lastGlobalRegistrationFailureSignature = null;
                return;
            }

            var gestures = failedGestures
                .Where(gesture => !string.IsNullOrWhiteSpace(gesture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(gesture => gesture, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (gestures.Count == 0)
            {
                _lastGlobalRegistrationFailureSignature = null;
                return;
            }

            var signature = string.Join("|", gestures);
            if (string.Equals(signature, _lastGlobalRegistrationFailureSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastGlobalRegistrationFailureSignature = signature;
            var displayText = string.Join(", ", gestures);
            var messageFormat = ResourceProvider.GetString("LOCPlayAch_Hotkeys_GlobalRegistrationFailed");
            if (string.IsNullOrWhiteSpace(messageFormat))
            {
                messageFormat = "Global shortcut unavailable: {0}. Another app may already be using the same key.";
            }

            ShowNotification(
                "PlayniteAchievements-Hotkey-GlobalFailed",
                string.Format(messageFormat, displayText),
                NotificationType.Error);
        }

        private void UnregisterGlobalHotkeys(bool disposeSink)
        {
            foreach (var id in _registeredGlobalHotkeys.Keys.ToList())
            {
                try
                {
                    if (_globalHotkeyWindowHandle != IntPtr.Zero)
                    {
                        UnregisterHotKey(_globalHotkeyWindowHandle, id);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed to unregister achievement global hotkey id={id}.");
                }
            }

            _registeredGlobalHotkeys.Clear();

            if (!disposeSink)
            {
                _logger?.Debug($"Unregistered achievement global hotkeys; kept sinkHandle={_globalHotkeyWindowHandle}.");
                return;
            }

            try
            {
                _globalHotkeySource?.RemoveHook(WndProc);
                _globalHotkeySource?.Dispose();
            }
            catch
            {
            }

            _globalHotkeySource = null;
            _globalHotkeyWindowHandle = IntPtr.Zero;
            _logger?.Debug("Disposed achievement global hotkey message sink.");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmHotkey)
            {
                return IntPtr.Zero;
            }

            var id = wParam.ToInt32();
            if (_registeredGlobalHotkeys.TryGetValue(id, out var action))
            {
                handled = true;
                _logger?.Debug($"Received global achievement hotkey id={id}, action={action}.");
                DispatchAction(action);
            }

            return IntPtr.Zero;
        }

        private void ShowNotification(string id, string message, NotificationType type)
        {
            try
            {
                _logger?.Debug($"Showing achievement hotkey notification id='{id}', type={type}, message='{message}'");
                var title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
                _api?.Notifications?.Add(new NotificationMessage(
                    id,
                    $"{title}\n{message}",
                    type));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to show achievement hotkey notification.");
            }
        }

        private static uint ToNativeModifiers(ModifierKeys modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                result |= ModControl;
            }
            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                result |= ModAlt;
            }
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                result |= ModShift;
            }
            if (modifiers.HasFlag(ModifierKeys.Windows))
            {
                result |= ModWin;
            }

            return result;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private enum AchievementHotkeyAction
        {
            ViewAchievements,
            ManageAchievements,
            Overview,

            // Handled only through the in-process input hook; deliberately excluded from
            // RegisterGlobalHotkeys so opening plugin settings stays scoped to Playnite focus.
            OpenSettings
        }
    }
}
