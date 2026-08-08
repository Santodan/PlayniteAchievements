using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class ToastNotificationService : IDisposable
    {
        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly Action _ensureResourcesLoaded;
        // Resolves the started process id for a game (null game id: most recently started game).
        private readonly Func<Guid?, int?> _getGameProcessId;
        // Optional foreground tracker: supplies the learned game window handle, which beats the
        // pid-based resolve for launcher-wrapped titles.
        private readonly ActiveGameWindowTracker _windowTracker;
        // Whether an unlock is being cut into a clip, so its card must be rendered into an overlay
        // track even when nothing about it shows on screen. Supplied by the recording service.
        private readonly Func<AchievementUnlockedEventArgs, bool> _needsOverlayTrack;
        private readonly Func<AchievementUnlockedEventArgs, bool> _usesCustomNotification;
        private readonly Func<AchievementUnlockedEventArgs, FrameworkElement> _createCustomNotificationContent;
        private readonly UnlockScreenshotService _screenshotService;
        private readonly ScreenshotFrameCompositor _frameCompositor;
        private readonly AchievementToastTemplateResolver _templateResolver;
        private readonly GameCustomDataStore _gameCustomDataStore;
        private readonly Queue<AchievementToastViewModel> _queue = new Queue<AchievementToastViewModel>();
        private bool _processing;
        // Diagnostic hold-logging (no behavior change): when the queue is holding waves because no
        // queued game is foreground, the UTC time the current hold began and the last time it was
        // logged, so a hold is reported once at start, at most every HoldLogIntervalSeconds while it
        // persists, and on release — each line naming the foreground window responsible.
        private DateTime? _holdStartedUtc;
        private DateTime _lastHoldLogUtc;
        private const int HoldLogIntervalSeconds = 15;
        // Target gap (DIP) from the screen/game-window corner to the visible card body, held
        // constant regardless of the card's ToastGlowMargin: the window margin is derived as
        // CornerGapDip - glow so the body sits here whether or not the border glow is on (with the
        // glow on, the glow itself may reach the screen edge). Tunable.
        private const double CornerGapDip = 24d;
        // Gap between launching the sound URI and the toast slide-in / controller pulse. The sound
        // is played out-of-process (UniPlaySong resolves and starts the audio), so its onset lags
        // the launch; this offset is what the slide-in and the in-process vibration wait for so all
        // three land together. Tunable.
        private const int SoundAlignmentDelayMs = 450;

        private bool _disposed;
        private Window _activeWindow;
        // The corner the current wave uses, resolved once per wave (theme override or plugin
        // setting). Read by the per-frame positioning path so it isn't re-resolved every frame.
        private ToastScreenCorner _activePosition = ToastScreenCorner.BottomRight;
        // The wave cards' uniform ToastGlowMargin, resolved once per wave. Positioning subtracts it
        // from CornerGapDip so the visible card body sits a constant distance from the corner.
        private double _activeCardGlow;
        private bool _activeToastThemeStylingEnabled = true;
        // The game the current wave belongs to, resolved once per wave. Screenshot capture and
        // toast placement key window resolution off this game so a wave from one running game
        // never anchors to another running game's window.
        private Guid? _activeWaveGameId;
        // The anchor the toast is placed against, resolved once per wave: the running game's window
        // when a game is running, otherwise the current Playnite window. The toast is always realized
        // Per-Monitor-V2 aware and positioned in physical pixels (ToastWindowPlacer) relative to this
        // anchor, so it renders crisply on whatever monitor the anchor is on. _activeIsGame selects the
        // anchor rect (game -> client rect; else -> the anchor monitor's work area); _activeMonitorScale
        // is that monitor's true effective scale (1.0 = 100%).
        private IntPtr _activeReferenceHwnd;
        private bool _activeIsGame;
        // Set for an unrevealed wave. The window is never revealed, so it must not insert itself into
        // the game's z-order; placement itself still runs, because the overlay track reads the
        // window's physical rect every frame.
        private bool _activeSuppressZOrder;
        private double _activeMonitorScale = 1.0;
        // The anchor monitor's refresh rate (Hz), resolved with the scale above, or 0 when it can't be
        // read. Every on-screen cadence derives from it: the composition tick that drives the slide
        // cannot outpace it, the card's WPF timelines are asked to tick at it, and the DPI settle poll
        // waits one of its frames at a time.
        private int _activeMonitorRefreshHz;
        // The running physical slide's per-frame tick handler (CompositionTarget.Rendering), or null.
        private EventHandler _activeSlideTick;
        // Per-wave placement state: the offset between where SetWindowPos is asked to put the toast
        // and where its HWND lands (measured once, on the wave's first settled placement), and whether
        // this wave has already logged that its placement needed rescuing.
        private ToastWindowPlacer.PlacementCorrection _placementCorrection;
        private bool _placementAnomalyLogged;
        private bool _activeUsesCustomMediaSurface;
        private bool _activeCenterHorizontally;
        private Dictionary<AchievementToastViewModel, FrameworkElement> _activeCardItems;
        private readonly Dictionary<AchievementToastViewModel, WebViewCaptureState> _activeWebViewFrames =
            new Dictionary<AchievementToastViewModel, WebViewCaptureState>();

        private sealed class WebViewCapturedFrame
        {
            public byte[] Pixels { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private sealed class WebViewCaptureState
        {
            public bool Capturing { get; set; }
            public WebViewCapturedFrame Latest { get; set; }
        }

        public ToastNotificationService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            Action ensureResourcesLoaded,
            Func<Guid?, int?> getGameProcessId = null,
            ActiveGameWindowTracker windowTracker = null,
            GameCustomDataStore gameCustomDataStore = null,
            Func<AchievementUnlockedEventArgs, bool> needsOverlayTrack = null,
            Func<AchievementUnlockedEventArgs, bool> usesCustomNotification = null,
            Func<AchievementUnlockedEventArgs, FrameworkElement> createCustomNotificationContent = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _ensureResourcesLoaded = ensureResourcesLoaded;
            _getGameProcessId = getGameProcessId;
            _windowTracker = windowTracker;
            _gameCustomDataStore = gameCustomDataStore;
            _needsOverlayTrack = needsOverlayTrack;
            _usesCustomNotification = usesCustomNotification;
            _createCustomNotificationContent = createCustomNotificationContent;
            _screenshotService = new UnlockScreenshotService(logger);
            _frameCompositor = new ScreenshotFrameCompositor(logger);
            _templateResolver = new AchievementToastTemplateResolver(
                api,
                logger,
                customTemplatesDirectory: AchievementToastTemplateResolver.GetCustomTemplatesDirectory(
                    PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath()));
            PlayniteAchievementsPlugin.AchievementUnlocked += OnAchievementUnlocked;
        }

        /// <summary>
        /// Raised when a non-preview toast wave is fully on screen (slide-in finished and
        /// placement snapped) — a liveness signal for the recording service's track wait (clip
        /// windows themselves are unlock-anchored). Fires on the UI thread.
        /// </summary>
        internal event EventHandler<ToastWaveDisplayedEventArgs> WaveDisplayed;

        /// <summary>
        /// Raised once per wave after the slide-out, carrying the recorded overlay track of every
        /// toasted item for export-time clip compositing.
        /// </summary>
        internal event EventHandler<ToastTracksCompletedEventArgs> TracksCompleted;

        private void RaiseWaveDisplayed(IReadOnlyList<AchievementToastViewModel> wave, DateTime? soundPlayedUtc)
        {
            if (wave == null || wave.Count == 0 || wave[0].IsPreview)
            {
                return;
            }

            try
            {
                WaveDisplayed?.Invoke(this, new ToastWaveDisplayedEventArgs(wave, DateTime.UtcNow, soundPlayedUtc));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast wave-displayed handler failed.");
            }
        }

        /// <summary>
        /// Drains the wave's track recorder (compression worker) off the UI thread and raises
        /// <see cref="TracksCompleted"/>. Fire-and-forget from the wave's cleanup.
        /// </summary>
        private async Task CompleteAndRaiseTracksAsync(ToastOverlayTrackRecorder recorder)
        {
            if (recorder == null)
            {
                return;
            }

            try
            {
                var tracks = await recorder.CompleteAsync().ConfigureAwait(false);
                if (tracks.Count > 0)
                {
                    TracksCompleted?.Invoke(this, new ToastTracksCompletedEventArgs(tracks));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast overlay track completion failed.");
            }
        }

        /// <summary>
        /// The effective toast display duration (theme override included), callable from any
        /// thread: marshals to the UI thread with a short timeout and falls back to the raw
        /// setting. Sizes the toast slot of unlock clip windows.
        /// </summary>
        internal int GetEffectiveToastDurationSecondsSafe()
        {
            var fallback = Math.Max(2, _settings?.Persisted?.ToastDurationSeconds ?? 6);
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return fallback;
                }

                if (dispatcher.CheckAccess())
                {
                    return EffectiveDurationSeconds();
                }

                var operation = dispatcher.InvokeAsync(EffectiveDurationSeconds);
                return operation.Task.Wait(500) ? operation.Task.Result : fallback;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve effective toast duration off-thread.");
                return fallback;
            }
        }

        private void OnAchievementUnlocked(object sender, AchievementUnlockedEventArgs e)
        {
            if (_disposed || !ShouldProcess(e))
            {
                return;
            }

            // Resolved here, synchronously with the recording service's own handler on this same
            // event, so a capture session starting or stopping cannot make the two disagree by
            // the time the enqueue lands on the UI thread.
            var needsOverlayTrack = NeedsOverlayTrack(e);

            var dispatcher = GetDispatcher();
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                EnqueueOnUi(e, needsOverlayTrack);
                return;
            }

            dispatcher.BeginInvoke(
                new Action(() => EnqueueOnUi(e, needsOverlayTrack)), DispatcherPriority.Background);
        }

        /// <summary>
        /// Whether an unlock enters the wave pipeline at all. It qualifies if it shows a
        /// notification, or (own unlocks only) if it owes notification imagery to something that
        /// is not the screen: a screenshot variant, or a clip overlay track. A wave with no
        /// on-screen notification runs unrevealed when a card is owed, and windowless when it
        /// is not.
        /// </summary>
        private bool ShouldProcess(AchievementUnlockedEventArgs args)
        {
            if (args == null)
            {
                return false;
            }

            if (ShouldToast(args.IsPreview, args.IsFriendUnlock, args.ProviderKey, args.PlayniteGameId, args.IsGameCompleted))
            {
                return true;
            }

            if (args.IsFriendUnlock)
            {
                return false;
            }

            // A clip being cut for this unlock needs a rendered card for its overlay track, even
            // with every notification and screenshot turned off.
            if (NeedsOverlayTrack(args))
            {
                return true;
            }

            var persisted = _settings?.Persisted;
            if (persisted?.EnableUnlockScreenshots != true)
            {
                return false;
            }

            var custom = ProviderRegistry.Settings<LocalSettings>();
            var useCustomNaming = ShouldUseCustomScreenshotNaming(custom, persisted);
            if ((!useCustomNaming || string.IsNullOrWhiteSpace(custom?.EffectiveScreenshotSaveFolder)) &&
                string.IsNullOrWhiteSpace(persisted.UnlockScreenshotDirectory))
            {
                return false;
            }

            return UnlockScreenshotVariantPolicy.Resolve(
                ResolveRarity(args), IsCompletionUnlock(args), args.ProviderKey, persisted)
                != ScreenshotVariants.None;
        }

        /// <summary>
        /// Whether a clip is being cut for this unlock, so its card must be realized and sampled
        /// into an overlay track regardless of what shows on screen. False when no recording
        /// service is wired in.
        /// </summary>
        private bool NeedsOverlayTrack(AchievementUnlockedEventArgs args) =>
            args != null && !args.IsPreview && !args.IsFriendUnlock &&
            (_needsOverlayTrack?.Invoke(args) ?? false);

        private static RarityTier ResolveRarity(AchievementUnlockedEventArgs args) =>
            System.Enum.TryParse(args.RarityTier, true, out RarityTier rarity) ? rarity : RarityTier.Common;

        private static bool IsCompletionUnlock(AchievementUnlockedEventArgs args) =>
            args.IsGameCompleted || args.IsCompletionAchievement || args.IsCapstone;

        /// <summary>
        /// Whether this unlock shows an on-screen toast. Previews always toast; otherwise the
        /// policy ANDs the EnableNotifications master switch into both toast flags and resolves
        /// all-false for null settings.
        /// </summary>
        private bool ShouldToast(
            bool isPreview,
            bool isFriendUnlock,
            string providerKey,
            Guid playniteGameId = default,
            bool isGameCompleted = false)
        {
            if (isPreview)
            {
                return true;
            }

            if (_usesCustomNotification?.Invoke(new AchievementUnlockedEventArgs
            {
                PlayniteGameId = playniteGameId,
                ProviderKey = providerKey,
                IsFriendUnlock = isFriendUnlock,
                IsGameCompleted = isGameCompleted
            }) == true)
            {
                return false;
            }

            var effective = ProviderNotificationPolicy.Resolve(_settings?.Persisted, providerKey);
            return isFriendUnlock
                ? effective.FriendUnlockToasts
                : effective.UnlockToasts;
        }

        private void EnqueueOnUi(AchievementUnlockedEventArgs args, bool needsOverlayTrack)
        {
            // An owed overlay track survives the re-check: the clip request already exists, so
            // dropping the item here would leave that clip waiting out its track timeout.
            if (_disposed || !(needsOverlayTrack || ShouldProcess(args)))
            {
                return;
            }

            // PreviewStyleOverride is set only by settings fire-tests, so the fired notification
            // renders the exact style the editor mockup shows; real unlocks resolve normally.
            _queue.Enqueue(new AchievementToastViewModel(
                args,
                _settings?.Persisted,
                styleOverride: args.PreviewStyleOverride,
                gameCustomDataStore: _gameCustomDataStore)
            {
                NeedsOverlayTrack = needsOverlayTrack,
            });
            if (!_processing)
            {
                _processing = true;
                _ = ProcessQueueAsync();
            }
        }

        /// <summary>
        /// Drops any queued (not-yet-shown) unlock toasts. Called when a game stops so stale
        /// unlocks from the session don't pop after the game has closed. Any toast already on
        /// screen finishes its animation.
        /// </summary>
        public void ClearPending()
        {
            if (_disposed)
            {
                return;
            }

            var dispatcher = GetDispatcher();
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                _queue.Clear();
                return;
            }

            dispatcher.BeginInvoke(new Action(() => _queue.Clear()), DispatcherPriority.Background);
        }

        /// <summary>
        /// Drops queued (not-yet-shown) unlock toasts belonging to one game. Called when that game
        /// stops so its stale unlocks don't pop after it closed, while queued toasts from other
        /// still-running games stay untouched.
        /// </summary>
        public void ClearPending(Guid gameId)
        {
            if (_disposed || gameId == Guid.Empty)
            {
                return;
            }

            var dispatcher = GetDispatcher();
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                RemovePendingForGame(gameId);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => RemovePendingForGame(gameId)), DispatcherPriority.Background);
        }

        private void RemovePendingForGame(Guid gameId)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            var kept = _queue.Where(vm => vm.PlayniteGameId != gameId).ToList();
            if (kept.Count == _queue.Count)
            {
                return;
            }

            _queue.Clear();
            foreach (var vm in kept)
            {
                _queue.Enqueue(vm);
            }
        }

        /// <summary>
        /// The current wave's game window handle as learned by the foreground tracker, or
        /// IntPtr.Zero when no tracker/game is available (callers fall back to pid resolution).
        /// </summary>
        private IntPtr ResolveWaveWindowHandle()
        {
            return _activeWaveGameId.HasValue && _windowTracker != null
                ? _windowTracker.TryGetWindowHandle(_activeWaveGameId.Value)
                : IntPtr.Zero;
        }

        /// <summary>
        /// Whether the wave's game is running, with its resolved window handle and process id.
        /// The single source for the in-game/out-of-game split shared by the base capture and
        /// the composite geometry. UI thread only.
        /// </summary>
        private bool TryResolveWaveGame(out IntPtr waveHwnd, out int? processId)
        {
            waveHwnd = ResolveWaveWindowHandle();
            processId = _getGameProcessId?.Invoke(_activeWaveGameId);
            return waveHwnd != IntPtr.Zero || (processId.HasValue && processId.Value > 0);
        }

        /// <summary>
        /// Starts the screen capture for the current wave. The running game's window is captured
        /// when one is resolvable. Out of game a real unlock keeps the foreground-window fallback
        /// (inside <see cref="UnlockScreenshotService.CaptureGameWindow(IntPtr, int?)"/>), but a
        /// manual test fire captures the whole monitor the Playnite window sits on, since the
        /// notification is placed there and there is no game screen to show. Window handles are
        /// resolved here on the UI thread; the blit runs on the pool.
        /// </summary>
        private Task<System.Drawing.Bitmap> StartWaveSurfaceCapture(bool isTestFire)
        {
            var gameRunning = TryResolveWaveGame(out var waveHwnd, out var processId);
            if (!gameRunning && isTestFire)
            {
                var appHwnd = ResolveAppWindowHandle();
                return Task.Run(() => _screenshotService.CaptureMonitor(appHwnd));
            }

            // All running-game shots capture the game window (WGC per-window, HDR-correct, client
            // area). The with-notification card is composited onto this same capture per item (see
            // ComposeWaveWithToastAsync) — the toast is a separate window; a monitor capture would
            // grab whatever is actually on top, not the game.
            return Task.Run(() => _screenshotService.CaptureGameWindow(waveHwnd, processId));
        }

        /// <summary>
        /// Builds the with-notification screenshot for each qualifying item in the wave: an
        /// independent clone of the shared base capture with only that item's toast card
        /// composited at the anchor corner — where a genuine single-toast notification would sit —
        /// so every saved file reads as a normal single-unlock screenshot regardless of wave size,
        /// and every variant shares one identical frame. In game the base is the client-area
        /// window capture and cards anchor to the client rect; the out-of-game test fire reuses
        /// the wave's single monitor capture and anchors cards to the monitor work area (the same
        /// anchor the live toast is placed against). The source window may never be revealed — the
        /// card render is layout-driven and does not read window opacity.
        /// <para>
        /// Invariant: a saved with-notification screenshot always contains a rendered notification
        /// card. It degrades to a plain clone of the base capture only when the card cannot be
        /// rendered or the anchor geometry cannot be resolved — never because notifications are
        /// turned off. A null base capture yields null (with-toast files are skipped).
        /// </para>
        /// Never disposes or mutates the base bitmap — the save pipeline owns it via the capture
        /// task.
        /// </summary>
        private async Task<Dictionary<AchievementToastViewModel, System.Drawing.Bitmap>> ComposeWaveWithToastAsync(
            WaveScreenshotPlan plan, Window window, bool isTestFire,
            Task<System.Drawing.Bitmap> baseCaptureTask)
        {
            var withToastVms = plan.Items
                .Where(i => (i.Variants & ScreenshotVariants.WithToast) != 0)
                .Select(i => i.Vm)
                .ToList();
            if (withToastVms.Count == 0)
            {
                return null;
            }

            var baseBitmap = baseCaptureTask != null
                ? await baseCaptureTask.ConfigureAwait(true)
                : null;
            if (baseBitmap == null)
            {
                return null;
            }

            // Geometry for placing cards into the base capture: the corner math runs against the
            // anchor rect (game client rect, or the work area for the out-of-game test fire) and
            // the composite maps through the rect the capture covers (client rect, or the full
            // monitor bounds — a monitor capture includes the taskbar area the work area excludes).
            var gameRunning = TryResolveWaveGame(out _, out _);
            var anchorPhys = System.Drawing.Rectangle.Empty;
            var capturePhys = System.Drawing.Rectangle.Empty;
            var haveGeometry = false;
            if (!gameRunning && isTestFire)
            {
                var monitorBounds = _screenshotService.TryGetGameMonitorBounds(ResolveAppWindowHandle(), null);
                if (monitorBounds.HasValue &&
                    TryResolveAnchor(out anchorPhys) && anchorPhys.Width > 0 && anchorPhys.Height > 0)
                {
                    capturePhys = monitorBounds.Value;
                    haveGeometry = true;
                }
            }
            else if (_activeIsGame &&
                     TryResolveAnchor(out anchorPhys) && anchorPhys.Width > 0 && anchorPhys.Height > 0)
            {
                capturePhys = anchorPhys;
                haveGeometry = true;
            }

            // UI thread: render each card and compute its synthetic single-toast corner rect. Map
            // by VM identity — the screenshot plan and the wave's realized cards can differ, since
            // each variant carries its own rarity policy; an item with no realized card degrades
            // to the plain base clone.
            var itemsControl = window?.Content as ItemsControl;
            var overlays = new List<(AchievementToastViewModel Vm, System.Drawing.Bitmap Overlay, System.Drawing.Rectangle Rect)>();
            foreach (var vm in withToastVms)
            {
                System.Drawing.Bitmap overlay = null;
                var rect = System.Drawing.Rectangle.Empty;
                if (haveGeometry && itemsControl != null)
                {
                    var container = ResolveCardContainer(itemsControl, vm);
                    if (container != null)
                    {
                        var webView = FindWebView(container);
                        System.Drawing.Size physSize;
                        if (webView != null)
                        {
                            WebViewCaptureState captureState = null;
                            if (_activeWebViewFrames.TryGetValue(vm, out captureState))
                            {
                                for (var wait = 0; wait < 100 && captureState.Capturing; wait++)
                                {
                                    await Task.Delay(10).ConfigureAwait(true);
                                }
                            }

                            var frame = captureState?.Capturing == true
                                ? captureState.Latest
                                : await CaptureWebViewFrameAsync(webView).ConfigureAwait(true);
                            overlay = frame != null
                                ? CreatePArgbBitmap(frame.Pixels, frame.Width, frame.Height)
                                : null;
                            physSize = frame != null
                                ? new System.Drawing.Size(frame.Width, frame.Height)
                                : System.Drawing.Size.Empty;
                        }
                        else
                        {
                            overlay = TryRenderToastItemOverlay(window, container, out physSize);
                        }

                        if (overlay != null)
                        {
                            ComputeActiveCorner(
                                anchorPhys, physSize.Width, physSize.Height, out var ix, out var iy);
                            rect = new System.Drawing.Rectangle(ix, iy, physSize.Width, physSize.Height);
                        }
                    }
                }

                overlays.Add((vm, overlay, rect));
            }

            // Pool: GDI+ clone + composite per item. This completes before the save pipeline takes
            // the base capture, so nothing touches the base bitmap concurrently.
            return await Task.Run(() =>
            {
                var byVm = new Dictionary<AchievementToastViewModel, System.Drawing.Bitmap>();
                var full = new System.Drawing.Rectangle(0, 0, baseBitmap.Width, baseBitmap.Height);
                foreach (var entry in overlays)
                {
                    try
                    {
                        // Clone(rect, format) preserves the capture's pixel format; new Bitmap(Image)
                        // would convert it and change the alpha semantics.
                        var clone = baseBitmap.Clone(full, baseBitmap.PixelFormat);
                        if (entry.Overlay != null)
                        {
                            try
                            {
                                CompositeToastOverlay(clone, entry.Overlay, entry.Rect, capturePhys);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Debug(ex, "Toast card composite failed; with-notification shot omits the toast.");
                            }
                        }

                        byVm[entry.Vm] = clone;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Base capture clone failed; with-notification shot skipped for one item.");
                    }
                    finally
                    {
                        entry.Overlay?.Dispose();
                    }
                }

                return byVm.Count > 0 ? byVm : null;
            }).ConfigureAwait(true);
        }

        /// <summary>
        /// Wraps a tightly-packed premultiplied-BGRA buffer in a GDI bitmap for GDI+ compositing.
        /// Returns null (and the shot omits the toast) on failure.
        /// </summary>
        private System.Drawing.Bitmap CreatePArgbBitmap(byte[] pixels, int pw, int ph)
        {
            try
            {
                var bitmap = new System.Drawing.Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var data = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, pw, ph),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast overlay render failed; with-notification shot omits the toast.");
                return null;
            }
        }

        /// <summary>
        /// Renders one live toast card (its item container inside the wave's ItemsControl) to a
        /// tightly-packed premultiplied-BGRA buffer at the physical pixel size it renders on screen.
        /// The card is drawn through a VisualBrush into a DrawingVisual at the origin: rendering the
        /// container directly would bake in its stacked offset within the window, and cropping a
        /// whole-window render would bleed the neighbouring cards' glow into the crop (stacked
        /// containers overlap via negative margins). The card's own glow room is part of the
        /// container's RenderSize (the template root carries the ToastGlowMargin), so the result is
        /// dimensionally identical to a single-toast window's content. Must be called on the UI
        /// thread (renders the live visual). Returns false when the container can't be rendered.
        /// </summary>
        private bool TryRenderToastItemBytes(
            Window window, FrameworkElement container,
            out byte[] pixels, out int width, out int height)
        {
            pixels = null;
            width = 0;
            height = 0;
            try
            {
                if (window == null || container == null ||
                    container.RenderSize.Width <= 0 || container.RenderSize.Height <= 0 ||
                    window.ActualWidth <= 0 || window.ActualHeight <= 0 ||
                    !ToastWindowPlacer.TryGetPhysicalRect(window, out var windowPhys))
                {
                    return false;
                }

                // Same DIP->physical factor as the whole-window render: the window rect is the
                // content's physical size, ActualWidth its DIP size.
                var pxPerDipX = (double)windowPhys.Width / window.ActualWidth;
                var pxPerDipY = (double)windowPhys.Height / window.ActualHeight;

                // Window-DIP bounds include the ItemsControl LayoutTransform (fit scale * DPI
                // compensation); RenderSize is the local, pre-transform size.
                var local = container.RenderSize;
                var bounds = container.TransformToAncestor(window)
                    .TransformBounds(new Rect(local));
                var pw = Math.Max(1, (int)Math.Ceiling(bounds.Width * pxPerDipX));
                var ph = Math.Max(1, (int)Math.Ceiling(bounds.Height * pxPerDipY));

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // Absolute viewbox pins the mapping to the layout bounds so effect bleed can't
                    // inflate the brush content; clipping matches where the live window edge clips.
                    // The viewbox coordinate space includes the container's offset within its
                    // parent panel (a stacked card's offset is non-zero), so the viewbox must be
                    // anchored at that offset — at (0,0) a stacked card renders shifted down and
                    // cropped out of the bitmap.
                    var offset = VisualTreeHelper.GetOffset(container);
                    var brush = new VisualBrush(container)
                    {
                        Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(offset.X, offset.Y, local.Width, local.Height),
                    };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, local.Width, local.Height));
                }

                // The physical/local DPI ratio carries both the LayoutTransform scale and the
                // window's physical render scale in one factor.
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pw, ph, 96.0 * pw / local.Width, 96.0 * ph / local.Height,
                    PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();

                var stride = pw * 4;
                var buffer = new byte[stride * ph];
                rtb.CopyPixels(buffer, stride, 0);

                pixels = buffer;
                width = pw;
                height = ph;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast card render failed.");
                return false;
            }
        }

        /// <summary>
        /// Renders one live toast card to a premultiplied-alpha GDI bitmap at its physical pixel
        /// size (see <see cref="TryRenderToastItemBytes"/>). Returns null (the caller degrades to
        /// the plain game capture) when the card can't be rendered.
        /// </summary>
        private System.Drawing.Bitmap TryRenderToastItemOverlay(
            Window window, FrameworkElement container, out System.Drawing.Size physSize)
        {
            physSize = System.Drawing.Size.Empty;
            if (!TryRenderToastItemBytes(window, container, out var pixels, out var pw, out var ph))
            {
                return null;
            }

            var bitmap = CreatePArgbBitmap(pixels, pw, ph);
            if (bitmap != null)
            {
                physSize = new System.Drawing.Size(pw, ph);
            }

            return bitmap;
        }

        private FrameworkElement ResolveCardContainer(
            ItemsControl itemsControl, AchievementToastViewModel vm)
        {
            if (itemsControl == null || vm == null)
            {
                return null;
            }

            object item = vm;
            if (_activeCardItems != null && _activeCardItems.TryGetValue(vm, out var customItem))
            {
                item = customItem;
            }

            return itemsControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement
                ?? item as FrameworkElement;
        }

        private static WebView2 FindWebView(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            if (root is WebView2 webView)
            {
                return webView;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindWebView(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private async Task<WebViewCapturedFrame> CaptureWebViewFrameAsync(WebView2 webView)
        {
            if (webView == null)
            {
                return null;
            }

            // WebView2 is initialized asynchronously from the host's Loaded handler. Wait for both
            // the controller and the document before taking the first media frame; later samples
            // pass through immediately.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var core = webView.CoreWebView2;
                if (core != null)
                {
                    try
                    {
                        var ready = await core.ExecuteScriptAsync("document.readyState").ConfigureAwait(true);
                        if (string.Equals(ready, "\"complete\"", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ready, "\"interactive\"", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // Navigation may be between documents; retry briefly.
                    }
                }

                await Task.Delay(50).ConfigureAwait(true);
            }

            if (webView.CoreWebView2 == null)
            {
                return null;
            }

            try
            {
                using (var stream = new MemoryStream())
                {
                    await webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, stream).ConfigureAwait(true);
                    stream.Position = 0;
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var source = decoder.Frames.FirstOrDefault();
                    if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
                    {
                        return null;
                    }

                    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        source, PixelFormats.Pbgra32, null, 0);
                    converted.Freeze();
                    var stride = converted.PixelWidth * 4;
                    var pixels = new byte[stride * converted.PixelHeight];
                    converted.CopyPixels(pixels, stride, 0);
                    var opacity = Math.Max(
                        0.35,
                        Math.Min(1.0,
                            ProviderRegistry.Settings<LocalSettings>()?.OverlayCustomOpacity ?? 1.0));
                    if (opacity < 0.999)
                    {
                        // Pbgra32 is premultiplied, so scale color and alpha together exactly as
                        // the live overlay window's Opacity property does at its settled value.
                        for (var i = 0; i < pixels.Length; i++)
                        {
                            pixels[i] = (byte)Math.Round(pixels[i] * opacity);
                        }
                    }

                    return new WebViewCapturedFrame
                    {
                        Pixels = pixels,
                        Width = converted.PixelWidth,
                        Height = converted.PixelHeight,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "WebView2 notification frame capture failed.");
                return null;
            }
        }

        private async Task RefreshWebViewFrameAsync(
            AchievementToastViewModel vm, WebView2 webView, WebViewCaptureState state)
        {
            if (vm == null || webView == null || state == null || state.Capturing)
            {
                return;
            }

            state.Capturing = true;
            try
            {
                var frame = await CaptureWebViewFrameAsync(webView).ConfigureAwait(true);
                if (frame != null && _activeWebViewFrames.TryGetValue(vm, out var active) &&
                    ReferenceEquals(active, state))
                {
                    state.Latest = frame;
                }
            }
            finally
            {
                state.Capturing = false;
            }
        }

        /// <summary>
        /// The shared geometry for one track operation: the wave's ItemsControl, the game client
        /// rect and toast window rect (physical pixels), and the window's DIP-to-physical factors.
        /// False when this isn't a game anchor or a rect can't be resolved.
        /// </summary>
        private bool TryGetTrackGeometry(
            Window window, out ItemsControl itemsControl,
            out System.Drawing.Rectangle clientPhys, out System.Drawing.Rectangle windowPhys,
            out double pxPerDipX, out double pxPerDipY)
        {
            itemsControl = window?.Content as ItemsControl;
            windowPhys = System.Drawing.Rectangle.Empty;
            pxPerDipX = 0;
            pxPerDipY = 0;
            if (itemsControl == null || !_activeIsGame ||
                window.ActualWidth <= 0 || window.ActualHeight <= 0 ||
                !TryResolveAnchor(out clientPhys) ||
                clientPhys.Width <= 0 || clientPhys.Height <= 0 ||
                !ToastWindowPlacer.TryGetPhysicalRect(window, out windowPhys))
            {
                clientPhys = System.Drawing.Rectangle.Empty;
                return false;
            }

            pxPerDipX = windowPhys.Width / window.ActualWidth;
            pxPerDipY = windowPhys.Height / window.ActualHeight;
            return true;
        }

        /// <summary>
        /// Records one animation tick of every toast card into the wave's overlay track recorder:
        /// per item, the card's rendered pixels plus its client-relative physical rect. The
        /// per-item tracks are re-timed into each achievement's unlock clip at export (WGC's
        /// per-window video capture can't see the separate toast window). Called by the caller once per
        /// recording frame, with that frame's composition time; a no-op when not a game anchor. The
        /// window may never be revealed — rendering a card reads layout, not visibility. UI thread
        /// only.
        /// </summary>
        private void SampleWaveTracks(
            ToastOverlayTrackRecorder recorder, Window window,
            IReadOnlyList<AchievementToastViewModel> toastItems, double elapsedMs)
        {
            if (recorder == null ||
                !TryGetTrackGeometry(window, out var itemsControl, out var clientPhys, out var windowPhys,
                    out var pxPerDipX, out var pxPerDipY))
            {
                return;
            }

            for (var i = 0; i < toastItems.Count; i++)
            {
                var vm = toastItems[i];
                var container = ResolveCardContainer(itemsControl, vm);
                if (container == null)
                {
                    continue;
                }

                byte[] pixels;
                int pw;
                int ph;
                var webView = FindWebView(container);
                if (webView != null)
                {
                    if (!_activeWebViewFrames.TryGetValue(vm, out var state))
                    {
                        state = new WebViewCaptureState();
                        _activeWebViewFrames[vm] = state;
                    }

                    if (!state.Capturing)
                    {
                        _ = RefreshWebViewFrameAsync(vm, webView, state);
                    }

                    var frame = state.Latest;
                    if (frame == null)
                    {
                        continue;
                    }

                    pixels = frame.Pixels;
                    pw = frame.Width;
                    ph = frame.Height;
                }
                else if (!TryRenderToastItemBytes(window, container, out pixels, out pw, out ph))
                {
                    continue;
                }

                // Card origin in window DIPs (includes the LayoutTransform) -> screen physical ->
                // client-relative. Relative rects cancel game-window motion (the toast follows the
                // window on screen while the game content never moves inside the captured frame)
                // but keep the slide animation.
                var origin = container.TransformToAncestor(window).Transform(new Point(0, 0));
                var relX = windowPhys.X + (int)Math.Round(origin.X * pxPerDipX) - clientPhys.X;
                var relY = windowPhys.Y + (int)Math.Round(origin.Y * pxPerDipY) - clientPhys.Y;
                recorder.Sample(
                    vm, pixels, pw, ph, relX, relY, clientPhys.Width, clientPhys.Height, elapsedMs);
            }
        }

        /// <summary>
        /// Computes, for every toast card, the constant translation from its settled stacked
        /// position to the synthetic single-toast corner — where a genuine lone toast would sit —
        /// and stores it on the card's track. Called once at the placement snap, when layout and
        /// position are final. UI thread only.
        /// </summary>
        private void SetTrackCornerOffsets(
            ToastOverlayTrackRecorder recorder, Window window,
            IReadOnlyList<AchievementToastViewModel> toastItems)
        {
            if (recorder == null ||
                !TryGetTrackGeometry(window, out var itemsControl, out var clientPhys, out var windowPhys,
                    out var pxPerDipX, out var pxPerDipY))
            {
                return;
            }

            foreach (var vm in toastItems)
            {
                var container = ResolveCardContainer(itemsControl, vm);
                if (container == null || container.RenderSize.Width <= 0 || container.RenderSize.Height <= 0)
                {
                    continue;
                }

                var bounds = container.TransformToAncestor(window)
                    .TransformBounds(new Rect(container.RenderSize));
                var physW = Math.Max(1, (int)Math.Ceiling(bounds.Width * pxPerDipX));
                var physH = Math.Max(1, (int)Math.Ceiling(bounds.Height * pxPerDipY));
                var settledRelX = windowPhys.X + (int)Math.Round(bounds.X * pxPerDipX) - clientPhys.X;
                var settledRelY = windowPhys.Y + (int)Math.Round(bounds.Y * pxPerDipY) - clientPhys.Y;

                ComputeActiveCorner(
                    clientPhys, physW, physH, out var cornerX, out var cornerY);
                recorder.SetCornerOffset(
                    vm,
                    (cornerX - clientPhys.X) - settledRelX,
                    (cornerY - clientPhys.Y) - settledRelY);
            }
        }

        /// <summary>
        /// Draws the toast overlay onto the base capture at the given physical rect relative to
        /// the rect the capture covers (game client rect, or monitor bounds for the out-of-game
        /// test fire). Physical-pixel coordinates map 1:1 into the capture when it equals that
        /// rect; the width/height ratio absorbs any rounding or DPI difference. Mutates
        /// <paramref name="game"/> in place.
        /// </summary>
        private static void CompositeToastOverlay(
            System.Drawing.Bitmap game,
            System.Drawing.Bitmap overlay,
            System.Drawing.Rectangle toastPhys,
            System.Drawing.Rectangle clientPhys)
        {
            var sx = (double)game.Width / clientPhys.Width;
            var sy = (double)game.Height / clientPhys.Height;
            var x = (int)Math.Round((toastPhys.X - clientPhys.X) * sx);
            var y = (int)Math.Round((toastPhys.Y - clientPhys.Y) * sy);
            var w = Math.Max(1, (int)Math.Round(overlay.Width * sx));
            var h = Math.Max(1, (int)Math.Round(overlay.Height * sy));

            using (var g = System.Drawing.Graphics.FromImage(game))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(overlay, new System.Drawing.Rectangle(x, y, w, h));
            }
        }

        /// <summary>
        /// Diagnostic only: records that the queue is holding waves because no queued game is
        /// foreground, logging the foreground window responsible once when the hold starts and at
        /// most every <see cref="HoldLogIntervalSeconds"/> while it persists — enough to identify
        /// the culprit window without spamming the 1s hold loop.
        /// </summary>
        private void LogWaveHeld()
        {
            var now = DateTime.UtcNow;
            if (_holdStartedUtc == null)
            {
                _holdStartedUtc = now;
                _lastHoldLogUtc = now;
                _logger?.Debug(
                    $"[Toast] Holding {_queue.Count} queued notification(s); no queued game is foreground. {DescribeForeground()}");
                return;
            }

            if ((now - _lastHoldLogUtc).TotalSeconds >= HoldLogIntervalSeconds)
            {
                _lastHoldLogUtc = now;
                _logger?.Debug(
                    $"[Toast] Still holding {_queue.Count} notification(s) after {(now - _holdStartedUtc.Value).TotalSeconds:F0}s. {DescribeForeground()}");
            }
        }

        /// <summary>
        /// Diagnostic only: closes out a hold that a now-ready wave ends, reporting how long the
        /// wave waited for the game to regain focus. No-op when nothing was being held.
        /// </summary>
        private void LogWaveReleased(int waveCount)
        {
            if (_holdStartedUtc == null)
            {
                return;
            }

            _logger?.Debug(
                $"[Toast] Releasing hold after {(DateTime.UtcNow - _holdStartedUtc.Value).TotalSeconds:F1}s; displaying {waveCount} notification(s).");
            _holdStartedUtc = null;
        }

        private string DescribeForeground()
        {
            var description = _windowTracker?.DescribeForegroundWindow();
            return string.IsNullOrEmpty(description) ? "foreground=unknown (no tracker)" : description;
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                await Task.Delay(125).ConfigureAwait(true);
                while (!_disposed && _queue.Count > 0)
                {
                    var wave = DequeueNextReadyWave();
                    if (wave.Count == 0)
                    {
                        // Every queued wave belongs to a running game whose window is minimized right
                        // now (unfocused/occluded games are ready — the toast interleaves with them).
                        // Hold and re-check; a game's pending toasts are dropped by ClearPending when
                        // it stops.
                        LogWaveHeld();
                        await Task.Delay(1000).ConfigureAwait(true);
                        continue;
                    }

                    LogWaveReleased(wave.Count);
                    await ShowWaveAsync(wave).ConfigureAwait(true);
                    await Task.Delay(250).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast notification queue failed.");
            }
            finally
            {
                _processing = false;
                if (!_disposed && _queue.Count > 0)
                {
                    _processing = true;
                    _ = ProcessQueueAsync();
                }
            }
        }

        /// <summary>
        /// Dequeues the next wave whose game is ready to receive it (window visible — focused,
        /// unfocused, or occluded — or not a running game at all). Waves batch by friend/own and by
        /// game: a cross-game wave would share one screenshot window and one placement anchor between
        /// two different game windows. A held wave (its game minimized) is skipped over so it never
        /// blocks another game's ready toasts; per-game ordering is preserved.
        /// </summary>
        private List<AchievementToastViewModel> DequeueNextReadyWave()
        {
            var max = Math.Max(1, _settings?.Persisted?.MaxConcurrentToasts ?? 3);
            var result = new List<AchievementToastViewModel>(max);
            if (_queue.Count == 0)
            {
                return result;
            }

            var items = _queue.ToList();
            var anchorIndex = items.FindIndex(IsWaveGameReady);
            if (anchorIndex < 0)
            {
                return result;
            }

            var anchor = items[anchorIndex];
            var end = anchorIndex;
            // Completion notifications never share a wave with achievement unlocks: they follow
            // in their own wave (multiple completions of the same kind may stack together).
            while (end < items.Count &&
                   result.Count < max &&
                   items[end].IsFriendUnlock == anchor.IsFriendUnlock &&
                   items[end].PlayniteGameId == anchor.PlayniteGameId &&
                   items[end].IsGameCompleted == anchor.IsGameCompleted)
            {
                result.Add(items[end]);
                end++;
            }

            _queue.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                if (i < anchorIndex || i >= end)
                {
                    _queue.Enqueue(items[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// A wave may show whenever its game's window is visible — focused, unfocused, or covered by
        /// another window all count. Previews, unlocks without a game id, and games that aren't
        /// running (e.g. friend unlocks for unowned titles) are always ready. The only thing that
        /// holds a running game's wave is a minimized window: a minimized window cannot be
        /// WGC-captured, and offers no surface to place a visible notification over (the toast
        /// z-orders above the game), so visible and unrevealed waves both hold. The toast is owned by
        /// the game window, so an unfocused/occluded game still gets a correctly-interleaved toast.
        /// </summary>
        private bool IsWaveGameReady(AchievementToastViewModel vm)
        {
            if (vm == null || vm.IsPreview || vm.PlayniteGameId == Guid.Empty || _windowTracker == null)
            {
                return true;
            }

            if (!_windowTracker.IsTracked(vm.PlayniteGameId))
            {
                return true;
            }

            // Hold only while minimized; a live check (rather than the last hook event) avoids a
            // stale state holding the wave when the window is actually on screen.
            return _windowTracker.IsGameWindowVisible(vm.PlayniteGameId);
        }

        /// <summary>
        /// How a wave runs, decided by two independent questions: does anything need a rendered
        /// notification card, and should that card be shown?
        /// <list type="bullet">
        /// <item><see cref="Windowless"/> — no card needed, so no window at all.</item>
        /// <item><see cref="Unrevealed"/> — a card is needed as an image only.</item>
        /// <item><see cref="Visible"/> — a card is needed and shown.</item>
        /// </list>
        /// The first two both produce nothing on screen, which is why they are easy to conflate;
        /// they differ in whether a window is created and a card rendered.
        /// </summary>
        private enum WaveMode
        {
            /// <summary>
            /// Nothing needs a card: only the clean and/or framed screenshot variants qualify, and
            /// no clip is being cut. Captures the base surface, saves it, and never creates a
            /// window — the cheapest mode, and the one that keeps a screenshots-only setup off the
            /// wave queue for a full display duration.
            /// </summary>
            Windowless,

            /// <summary>
            /// A card is needed as an image but not on screen: the with-notification screenshot
            /// variant must composite one, or a clip needs its overlay track. The window is
            /// created, laid out, animated and sampled exactly as a visible wave, but is never
            /// revealed and plays no chime.
            /// </summary>
            Unrevealed,

            /// <summary>The on-screen notification: chime, vibration, reveal, hold, slide-out.</summary>
            Visible,
        }

        private sealed class WavePlan
        {
            public WaveMode Mode { get; set; }

            /// <summary>Items realized as cards in the window. Empty only for Windowless.</summary>
            public IReadOnlyList<AchievementToastViewModel> CardItems { get; set; }

            public WaveScreenshotPlan Screenshots { get; set; }

            public bool IsVisible => Mode == WaveMode.Visible;
        }

        /// <summary>
        /// Splits a wave into what is shown and what is merely rendered. Any toasting item makes
        /// the wave <see cref="WaveMode.Visible"/> and those items are its cards. Otherwise a card
        /// is realized — invisibly — for every item that owes a with-notification screenshot or a
        /// clip overlay track, which is <see cref="WaveMode.Unrevealed"/>; if no item owes one,
        /// there is nothing to render and the wave is <see cref="WaveMode.Windowless"/>.
        /// </summary>
        private WavePlan ResolveWavePlan(IReadOnlyList<AchievementToastViewModel> wave)
        {
            var screenshots = BuildScreenshotPlan(wave);

            // Toasts, screenshots and clips gate independently: a wave can contain items that
            // toast, items that only produce capture output, or a mix (waves batch by friend/own
            // only).
            var toastItems = wave
                .Where(vm => ShouldToast(
                    vm.IsPreview,
                    vm.IsFriendUnlock,
                    vm.ProviderKey,
                    vm.PlayniteGameId,
                    vm.IsGameCompleted))
                .ToList();
            if (toastItems.Count > 0)
            {
                return new WavePlan
                {
                    Mode = WaveMode.Visible,
                    CardItems = toastItems,
                    Screenshots = screenshots,
                };
            }

            var withToastVms = new HashSet<AchievementToastViewModel>(
                screenshots?.Items
                    .Where(i => (i.Variants & ScreenshotVariants.WithToast) != 0)
                    .Select(i => i.Vm)
                    ?? Enumerable.Empty<AchievementToastViewModel>());
            var cardItems = wave
                .Where(vm => vm.NeedsOverlayTrack || withToastVms.Contains(vm))
                .ToList();

            return new WavePlan
            {
                Mode = cardItems.Count > 0 ? WaveMode.Unrevealed : WaveMode.Windowless,
                CardItems = cardItems,
                Screenshots = screenshots,
            };
        }

        private async Task ShowWaveAsync(IReadOnlyList<AchievementToastViewModel> wave)
        {
            if (wave == null || wave.Count == 0)
            {
                return;
            }

            _ensureResourcesLoaded?.Invoke();

            var waveIsTestFire = wave[0].IsTestFire;
            _activeToastThemeStylingEnabled = wave[0].ToastUseThemeStyling;
            // Placement state is per-wave: the correction is measured on this wave's first settled
            // placement, and the anomaly warning is emitted at most once for it.
            _placementCorrection = default(ToastWindowPlacer.PlacementCorrection);
            _placementAnomalyLogged = false;
            var waveGameId = wave[0].PlayniteGameId;
            _activeWaveGameId = waveGameId != Guid.Empty ? waveGameId : (Guid?)null;

            var wavePlan = ResolveWavePlan(wave);
            var cardItems = wavePlan.CardItems;
            var visible = wavePlan.IsVisible;
            var plan = wavePlan.Screenshots;
            _activeUsesCustomMediaSurface = !visible && cardItems.Count > 0 &&
                _createCustomNotificationContent != null &&
                cardItems.All(vm => _usesCustomNotification?.Invoke(vm.SourceEventArgs) == true);
            var customPosition = ProviderRegistry.Settings<LocalSettings>()?.UnlockOverlayPosition
                ?? LocalUnlockOverlayPosition.BottomRight;
            _activeCenterHorizontally = _activeUsesCustomMediaSurface &&
                (customPosition == LocalUnlockOverlayPosition.TopCenter ||
                 customPosition == LocalUnlockOverlayPosition.BottomCenter);
            // Media-only waves owned by the custom renderer use its configured corner and have no
            // upstream glow margin. Ordinary waves retain the theme/plugin toast placement.
            _activePosition = _activeUsesCustomMediaSurface
                ? ResolveCustomMediaPosition()
                : EffectivePosition();
            _activeCardGlow = _activeUsesCustomMediaSurface ? 0 : wave[0].ToastGlowMargin.Top;

            // The base capture must precede window.Show(); overlapping it with the sound-align
            // delay below adds no latency to the toast itself. It feeds every variant: clean saves
            // it as-is, framed composites the frame onto it, and with-notification composites each
            // item's rendered card onto a copy of it.
            Task<System.Drawing.Bitmap> baseCaptureTask = null;
            if (plan != null)
            {
                if (plan.CaptureDelayMilliseconds > 0)
                {
                    await Task.Delay(plan.CaptureDelayMilliseconds).ConfigureAwait(true);
                }

                baseCaptureTask = StartWaveSurfaceCapture(waveIsTestFire);
            }

            // Nothing needs a card: capture and save, no window, no delays. Running this inside
            // the sequential wave pipeline is what keeps the out-of-game monitor capture free of
            // an earlier wave's toast, and keeps the per-wave placement state single-owner.
            if (wavePlan.Mode == WaveMode.Windowless)
            {
                if (plan != null)
                {
                    _ = SaveWaveScreenshotsAsync(plan, baseCaptureTask, null);
                }

                return;
            }

            // Chime and vibration belong to the on-screen notification, so an unrevealed wave skips
            // both — and skips the alignment delay that exists only to line them up with the
            // reveal. Its clips carry no chime because none was played.
            DateTime? soundPlayedUtc = null;
            if (visible)
            {
                // Play the sound first, then show the toast after a short delay so the audio onset
                // and the slide-in visually align.
                soundPlayedUtc = PlayWaveSound(cardItems);
                await Task.Delay(SoundAlignmentDelayMs).ConfigureAwait(true);
                if (_disposed)
                {
                    DisposeCaptureTask(baseCaptureTask);
                    return;
                }

                // Pulse after the same alignment delay: the motors start in-process, so firing at
                // launch time would put the vibration ahead of the audible chime.
                VibrateControllers();
            }

            var window = PlayniteUiProvider.CreateBorderlessTopmostWindow(
                _api,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            _activeWindow = window;

            // A wave is game-homogeneous, so scope the custom template to this wave's game and
            // provider (game > provider > global) for real unlocks. The template decision (fire-test
            // preview source vs normal theme-styling resolve) and the host element are built through
            // the shared ToastSurfaceFactory so the live toast and the settings inline preview
            // cannot drift.
            var waveProviderKey = cardItems.FirstOrDefault()?.ProviderKey;
            var waveScopeGameId = _activeWaveGameId ?? Guid.Empty;
            // A fire-test carries a forced preview source; captured here for the render-failure
            // handler below (the template decision itself lives in ToastSurfaceFactory).
            var previewSource = cardItems
                .Select(vm => vm.PreviewTemplateSource)
                .FirstOrDefault(source => source.HasValue);
            DataTemplate template = null;
            ItemsControl items = null;
            _activeCardItems = null;
            if (_activeUsesCustomMediaSurface)
            {
                var customItems = new ItemsControl();
                var customMap = new Dictionary<AchievementToastViewModel, FrameworkElement>();
                foreach (var vm in cardItems)
                {
                    FrameworkElement content = null;
                    try
                    {
                        content = _createCustomNotificationContent(vm.SourceEventArgs);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Custom notification media surface failed; using the original toast surface.");
                    }

                    if (content == null)
                    {
                        customItems = null;
                        customMap = null;
                        break;
                    }

                    customItems.Items.Add(content);
                    customMap[vm] = content;
                }

                if (customItems != null)
                {
                    items = customItems;
                    _activeCardItems = customMap;
                }
                else
                {
                    _activeUsesCustomMediaSurface = false;
                    _activeCenterHorizontally = false;
                    _activePosition = EffectivePosition();
                    _activeCardGlow = wave[0].ToastGlowMargin.Top;
                }
            }

            if (items == null)
            {
                template = ToastSurfaceFactory.ResolveToastTemplate(
                    _templateResolver, cardItems, ToastThemeStylingEnabled, waveProviderKey, waveScopeGameId);
                items = ToastSurfaceFactory.BuildToastSurface(cardItems, template);
            }

            LogWaveDiagnostics(cardItems, template, wavePlan.Mode);

            window.Content = items;

            // Resolve the anchor the toast follows. The toast is realized Per-Monitor-V2 and positioned
            // in physical pixels relative to that anchor, so it renders crisply on whatever monitor the
            // anchor is on. A real unlock anchors to the running game's window (its client rect, so the
            // toast sits over the game and inside the screenshot); a fire-test preview, or an unlock
            // with no resolvable game, anchors to the current Playnite window's monitor work area (a
            // screen corner where Playnite is). _activeMonitorScale (the anchor monitor's true scale)
            // drives both the content DPI compensation and the physical placement.
            _activeReferenceHwnd = IntPtr.Zero;
            _activeIsGame = false;
            if (!previewSource.HasValue)
            {
                _activeReferenceHwnd = _screenshotService.ResolveGameWindowHandle(
                    ResolveWaveWindowHandle(),
                    _getGameProcessId?.Invoke(_activeWaveGameId));
                _activeIsGame = _activeReferenceHwnd != IntPtr.Zero;
            }

            if (_activeReferenceHwnd == IntPtr.Zero)
            {
                _activeReferenceHwnd = ResolveAppWindowHandle();
            }

            _activeMonitorScale = ToastWindowPlacer.ResolveMonitorScale(_activeReferenceHwnd);
            _activeMonitorRefreshHz =
                ToastWindowPlacer.TryGetMonitorRefreshHz(_activeReferenceHwnd, out var refreshHz) ? refreshHz : 0;

            // When anchored to a running game, drop topmost so the toast can be occluded. It is NOT
            // owned by the game window — ownership raises the owner (game), pushing overlapping
            // windows behind it. Instead the toast is inserted directly above the game in the z-order
            // each frame (see the follow below), which leaves the game and every other window in
            // place: the toast just sits over the game and is naturally occluded by anything above it.
            // Out-of-game / preview keeps the topmost float over Playnite. A unrevealed wave is never
            // revealed, so it drops topmost unconditionally and stays out of the z-order entirely
            // (see _activeSuppressZOrder) — it has no business floating over anything.
            _activeSuppressZOrder = !visible;
            if (!visible || (_activeIsGame && _activeReferenceHwnd != IntPtr.Zero))
            {
                window.Topmost = false;
            }

            // Bound the toast to a fraction of the available area (fitScale) and fold in the DPI
            // compensation so the per-monitor toast ends at the monitor's true physical size. The real
            // compensation is monitorScale / renderScale (the scale WPF actually renders the window at):
            // ~1 when WPF already lays the per-monitor window out at the monitor DPI, or the missing
            // factor when it lays it out at the system scale. renderScale can only be read once the
            // window is shown on the target monitor, so this pre-show pass uses the system scale as the
            // estimate and ApplyDpiCompensation() corrects it right after Show (while still invisible).
            var fitScale = ResolveFitScale(items);
            var systemScale = ToastWindowPlacer.SystemScale();
            var dpiComp = systemScale > 0 ? _activeMonitorScale / systemScale : 1.0;
            if (dpiComp <= 0)
            {
                dpiComp = 1.0;
            }

            var contentScale = fitScale * dpiComp;
            items.LayoutTransform = Math.Abs(contentScale - 1.0) > ContentScaleEpsilon
                ? new ScaleTransform(contentScale, contentScale)
                : null;
            window.Opacity = 0;
            // Do not move the window during Loaded: a SizeToContent window moved before it is first
            // presented at DPI > 100% gets an HWND sized from unscaled DIPs, which clips content
            // inside the card. Pre-place before Show() so the HWND is created at its final rect on
            // the anchor monitor; ContentRendered/shown/snap remain as post-presentation corrections.
            window.ContentRendered += (s, e) => PlaceWindow(window, "rendered");

            EventHandler onRendering = null;
            EventHandler onTrackSample = null;
            ToastOverlayTrackRecorder trackRecorder = null;
            // Overlay-track sampling stats for the wave's diagnostic line: the composed frames the
            // sampler saw against the samples it actually took is what shows the cadence landing where
            // the recording frame rate asks for it rather than aliasing to half of it.
            RenderTickCounter trackTicks = null;
            var trackSampleCount = 0;
            try
            {
                // Realize the toast HWND under Per-Monitor-V2 so Windows does not bitmap-rescale it on
                // a monitor whose scale differs from the process's system DPI. A window's DPI awareness
                // is fixed at HWND creation; all HWND-affecting props were set in
                // CreateBorderlessTopmostWindow before any handle existed, so no recreation escapes this.
                // Only when the anchor monitor's scale actually differs from the system scale: on a
                // same-DPI monitor Windows never virtualizes the window, so a plain system-aware HWND
                // is already pixel-perfect — and skipping the per-monitor window avoids routing
                // WM_DPICHANGED through WPF's shared DPI state in this system-aware host process,
                // which has been observed to rescale sibling windows and hard-crash the process on
                // single-monitor high-DPI setups.
                var needsPerMonitorWindow = systemScale > 0 &&
                    Math.Abs(_activeMonitorScale - systemScale) >= DpiSettleTolerance;
                _logger?.Info(
                    $"[Toast] Fire: monitorScale={_activeMonitorScale:0.###}, systemScale={systemScale:0.###}, " +
                    $"perMonitorWindow={needsPerMonitorWindow}, isGame={_activeIsGame}, " +
                    $"revealed={visible}");
                if (needsPerMonitorWindow)
                {
                    using (Common.DpiAwarenessScope.PerMonitorV2())
                    {
                        new WindowInteropHelper(window).EnsureHandle();
                    }
                }
                else
                {
                    new WindowInteropHelper(window).EnsureHandle();
                }

                PlaceWindow(window, "preshow");
                window.Show();

                // Moving the per-monitor window onto the target monitor raises WM_DPICHANGED
                // asynchronously; WPF then resizes/repositions the window. Wait for that to settle (the
                // window's render scale reaches the target monitor's scale) while it is still invisible
                // (Opacity=0), so the reveal below does not flicker across the monitor boundary. Bounded
                // so it never hangs; already-settled cases (e.g. the system monitor) exit immediately.
                var settleFrameMs = Math.Max(1, (int)Math.Ceiling(MonitorFramePeriodMs()));
                for (var settle = 0;
                    settle < MaxDpiSettleFrames && !_disposed &&
                        Math.Abs(ToastWindowPlacer.RenderScale(window) - _activeMonitorScale) >= DpiSettleTolerance;
                    settle++)
                {
                    await Task.Delay(settleFrameMs).ConfigureAwait(true);
                }

                if (_disposed)
                {
                    return;
                }

                // Now on the target monitor with the DPI settled: correct the compensation from the
                // actual render scale, snap to the corner, and (for a visible wave) reveal.
                ApplyDpiCompensation(window, items, fitScale);
                PlaceWindow(window, "shown");
                SlideInPhysical(window, reveal: visible);

                // Start recording each card's overlay track now, at the slide-in — revealed or not
                // — so the slide-in animation lands in the tracks (not just the settled toast).
                // Tracks are sampled at the
                // recording frame rate and re-timed into each achievement's clip at export. Independent
                // of the placement hooks below (sampling only reads, never moves the window). Game anchor
                // only — a test fire out of game has no video — and only with recordings enabled, since
                // nothing else consumes a track.
                if (_activeIsGame && _activeReferenceHwnd != IntPtr.Zero &&
                    (_settings?.Persisted?.EnableUnlockRecordings ?? false))
                {
                    var sampleIntervalMs = TrackSampleIntervalMs();
                    trackRecorder = new ToastOverlayTrackRecorder(_logger, sampleIntervalMs);
                    SampleWaveTracks(trackRecorder, window, cardItems, 0d);
                    trackSampleCount = 1;
                    // The unconditional resample also carries animation frames into the tracks (min
                    // effective frame delay is 100ms for both GIF and WebP per AnimatedImageHelper) — do
                    // not reduce this to sample-on-position-change.
                    var recorder = trackRecorder;
                    // Sampling is due every recording frame, but can only happen on a composed frame, so
                    // take the tick nearest each due instant: comparing against the due time less half a
                    // monitor frame is what stops a recording rate at or near the refresh rate from
                    // aliasing. When the two rates match, every tick is a sample; when the recording rate
                    // is the lower one, ticks are skipped evenly.
                    var dueTolerance = MonitorFramePeriodMs() / 2d;
                    var nextDueMs = sampleIntervalMs;
                    trackTicks = new RenderTickCounter();
                    var counter = trackTicks;
                    onTrackSample = (s, e) =>
                    {
                        try
                        {
                            if (!counter.TryAdvance(e, out var elapsedMs) ||
                                elapsedMs < nextDueMs - dueTolerance)
                            {
                                return;
                            }

                            // Advance past the elapsed time so a stall or a dropped frame resumes on
                            // cadence instead of bunching the samples it missed onto this tick.
                            do
                            {
                                nextDueMs += sampleIntervalMs;
                            }
                            while (nextDueMs <= elapsedMs);

                            trackSampleCount++;
                            SampleWaveTracks(recorder, window, cardItems, elapsedMs);
                        }
                        catch
                        {
                            // Ignore transient render/placement failures (e.g. window closing).
                        }
                    };
                    CompositionTarget.Rendering += onTrackSample;
                }

                // Let the cards finish sliding in and paint so each renders at its final laid-out
                // size (achievement icons and badge images load asynchronously), then snap,
                // composite the with-notification shots, and hold for the remaining display time.
                // The base capture itself already ran, before the window existed.
                const int captureDelayMs = 300;
                await Task.Delay(captureDelayMs).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                // Stop the slide so placement can move the window directly, and snap to the anchor
                // corner now that the toast is fully laid out.
                StopActiveSlide();
                PlaceWindow(window, "snap");

                // The wave has settled, revealed or not: signal the recording service (a liveness
                // bump for its track wait, plus this wave's chime time for the clip audio mix —
                // clip windows themselves are unlock-anchored). A unrevealed wave passes a null
                // chime time, so its clips are mixed without one.
                RaiseWaveDisplayed(cardItems, soundPlayedUtc);

                // Layout and placement are final: pin each card's synthetic single-toast corner so
                // its recorded motion lands where a genuine lone toast would sit.
                SetTrackCornerOffsets(trackRecorder, window, cardItems);

                // The with-notification composites happen here: the toast has slid in and settled,
                // so each item's card renders at its final laid-out size. Cards render on the UI
                // thread (live visuals); the clones and blits run on the thread pool.
                Dictionary<AchievementToastViewModel, System.Drawing.Bitmap> toastByVm = null;
                if (plan != null && plan.NeedsToastComposite)
                {
                    toastByVm = await ComposeWaveWithToastAsync(plan, window, waveIsTestFire, baseCaptureTask)
                        .ConfigureAwait(true);
                }

                if (plan != null)
                {
                    _ = SaveWaveScreenshotsAsync(plan, baseCaptureTask, toastByVm);
                    baseCaptureTask = null;
                }

                // An invisible wave that owes no overlay track has already produced everything it
                // exists for: nothing left to follow, animate, hold or slide out. The finally block
                // closes the window. Skipping the hold is what keeps a screenshot-only
                // configuration from occupying the sequential queue for a full display duration.
                if (!visible && trackRecorder == null)
                {
                    return;
                }

                // Follow the anchor window every rendered frame (smooth while dragging). The anchor
                // handle was resolved once at wave start (game window, else the Playnite window) and
                // stays valid even if focus later changes. The overlay tracks are sampled separately
                // by onTrackSample (started at the reveal so the slide-in is recorded too).
                if (_activeReferenceHwnd != IntPtr.Zero)
                {
                    var followTicks = new RenderTickCounter();
                    onRendering = (s, e) =>
                    {
                        try
                        {
                            // Once per composed frame: WPF can raise Rendering more than once for the
                            // same frame, and a second SetWindowPos to the same point is pure cost.
                            if (followTicks.TryAdvance(e, out _))
                            {
                                PlaceWindowToHandle(window);
                            }
                        }
                        catch
                        {
                            // Ignore transient placement failures (e.g. window closing).
                        }
                    };
                    CompositionTarget.Rendering += onRendering;
                }

                var durationMs = EffectiveWaveDurationMilliseconds();
                var remainingMs = Math.Max(0, durationMs - captureDelayMs);
                try
                {
                    AnimateCountdownBars(window, remainingMs);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Toast countdown animation failed.");
                }

                var endedHidden = await HoldWaveAsync(remainingMs).ConfigureAwait(true);

                if (onRendering != null)
                {
                    CompositionTarget.Rendering -= onRendering;
                    onRendering = null;
                }

                // Track sampling keeps running through the slide-out so the exit motion (and any
                // still-animating GIF/countdown pixels) land in the tracks; an endedHidden wave
                // played no slide-out on screen, so its tracks simply end at the last sample.
                if (!endedHidden)
                {
                    var slideOutMs = SlideOutPhysical(window);
                    await Task.Delay((int)Math.Round(slideOutMs) + SlideSettleBufferMs).ConfigureAwait(true);
                }

                if (onTrackSample != null)
                {
                    CompositionTarget.Rendering -= onTrackSample;
                    onTrackSample = null;
                }

                LogWaveCadence(trackTicks, trackSampleCount);
            }
            catch (Exception ex) when (previewSource.HasValue)
            {
                // A fire-test preview of a theme template can throw when the template references
                // resources defined only in that theme's own dictionaries (e.g. a theme style
                // key), which are not loaded unless that theme is the running theme. Surface it
                // instead of silently showing nothing.
                _logger?.Warn(ex, $"Failed to render {previewSource} notification preview template.");
                NotifyPreviewRenderFailed(previewSource.Value, ex);
            }
            finally
            {
                // Null after the save pipeline takes ownership; disposes the pending capture when
                // the wave aborts (dispose, exception) before the hand-off.
                DisposeCaptureTask(baseCaptureTask);

                if (onRendering != null)
                {
                    CompositionTarget.Rendering -= onRendering;
                }

                if (onTrackSample != null)
                {
                    CompositionTarget.Rendering -= onTrackSample;
                }

                // Finalize and hand the recorded card tracks to the recording service. The raw
                // pixels are already captured, so this safely outlives window.Close() below.
                _ = CompleteAndRaiseTracksAsync(trackRecorder);

                StopActiveSlide();
                _activeReferenceHwnd = IntPtr.Zero;
                _activeIsGame = false;
                _activeSuppressZOrder = false;
                _activeMonitorScale = 1.0;
                _activeUsesCustomMediaSurface = false;
                _activeCenterHorizontally = false;
                _activeCardItems = null;
                _activeWebViewFrames.Clear();

                try
                {
                    window.Close();
                }
                catch
                {
                }

                if (ReferenceEquals(_activeWindow, window))
                {
                    _activeWindow = null;
                }
            }
        }

        /// <summary>
        /// Tells the user why a theme fire-test preview showed nothing: the theme's template
        /// depends on resources only present while that theme is the running theme, so it cannot
        /// render from the settings window. Preview-only, so a plain message is fine.
        /// </summary>
        private void NotifyPreviewRenderFailed(NotificationTemplatePreviewSource source, Exception ex)
        {
            if (source == NotificationTemplatePreviewSource.PluginStyle)
            {
                return;
            }

            try
            {
                _api?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Notification_ThemePreviewUnavailable"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
            catch (Exception dialogEx)
            {
                _logger?.Debug(dialogEx, "Failed to surface notification preview render failure.");
            }
        }

        /// <summary>
        /// Holds the wave for its remaining display time. The toast is owned by / z-ordered above the
        /// game window (see <see cref="ShowWaveAsync"/>), so the OS occludes and hides it in lockstep
        /// with the game — covered when the game is covered, hidden when the game is minimized — with
        /// no manual focus-based hide/show. The countdown-bar animation runs on wall-clock time. A
        /// unrevealed wave holds for the same duration so its overlay track spans the clip's toast
        /// slot. Returns false (the toast is never hidden by us) so the caller always runs the normal
        /// slide-out.
        /// </summary>
        private async Task<bool> HoldWaveAsync(int remainingMs)
        {
            await Task.Delay(Math.Max(0, remainingMs)).ConfigureAwait(true);
            return false;
        }

        /// <summary>
        /// Fires a single UniPlaySong sound for the wave, using the rarest tier present so a burst
        /// of unlocks does not stack overlapping sounds. UniPlaySong owns enablement and audio
        /// selection for the "playniteachievements/&lt;tier&gt;" URI; if it is not installed the URI
        /// is unhandled and the call is ignored. Returns the launch moment (null when no sound
        /// fired) so the recording service can locate the chime in its sidecar audio track.
        /// </summary>
        /// <summary>
        /// Pulses connected controllers when enabled, called after SoundAlignmentDelayMs so the
        /// motors start with the chime rather than ahead of it. Fires for every toast wave — own
        /// unlocks, friend unlocks, and fire-tests — so the strength setting can be tuned live
        /// from the settings preview.
        /// </summary>
        private void VibrateControllers()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || !persisted.EnableControllerVibration)
            {
                return;
            }

            ControllerVibrationService.Pulse(
                persisted.ControllerVibrationStrengthPercent,
                persisted.ControllerVibrationDurationMs,
                _logger);
        }

        private DateTime? PlayWaveSound(IReadOnlyList<AchievementToastViewModel> wave)
        {
            var tier = wave?
                .OrderByDescending(vm => vm.SoundTierRank)
                .Select(vm => vm.SoundTierSegment)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(tier))
            {
                return null;
            }

            try
            {
                Process.Start($"playnite://uniplaysong/playniteachievements/{tier}");
                return DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast unlock sound URI could not be launched.");
                return null;
            }
        }

        private sealed class WaveScreenshotPlan
        {
            public List<(AchievementToastViewModel Vm, ScreenshotVariants Variants)> Items { get; } =
                new List<(AchievementToastViewModel, ScreenshotVariants)>();

            public string BaseDirectory { get; set; }

            // Filename suffixes snapshotted from settings at plan-build time so the
            // fire-and-forget save uses the values as of capture. Null means no suffix.
            public string CleanSuffix { get; set; }

            public string WithToastSuffix { get; set; }

            public string FramedSuffix { get; set; }

            public LocalSettings CustomSettings { get; set; }

            public bool UsesCustomNaming => CustomSettings != null;

            public int CaptureDelayMilliseconds { get; set; }

            public bool IsTestFire { get; set; }

            public bool NeedsCleanCapture => Items.Any(i =>
                (i.Variants & (ScreenshotVariants.Clean | ScreenshotVariants.Framed)) != 0);

            public bool NeedsToastCapture => Items.Any(i =>
                (i.Variants & ScreenshotVariants.WithToast) != 0);

            public bool NeedsToastComposite => NeedsToastCapture;

            public bool NeedsFrame => Items.Any(i =>
                (i.Variants & ScreenshotVariants.Framed) != 0);
        }

        /// <summary>
        /// Decides which screenshot variants each item in this wave should produce, resolving the
        /// per-provider notification policy per item (a wave can mix providers). Returns null when
        /// nothing should be captured (previews, friend waves, screenshots disabled, no directory,
        /// or every item resolved to no variants).
        /// </summary>
        private WaveScreenshotPlan BuildScreenshotPlan(IReadOnlyList<AchievementToastViewModel> wave)
        {
            if (wave == null || wave.Count == 0)
            {
                return null;
            }

            var first = wave[0];
            if (first.IsPreview || first.IsFriendUnlock)
            {
                return null;
            }

            var persisted = _settings?.Persisted;
            if (persisted?.EnableUnlockScreenshots != true)
            {
                return null;
            }

            var custom = ProviderRegistry.Settings<LocalSettings>();
            var useCustomNaming = ShouldUseCustomScreenshotNaming(custom, persisted);
            var baseDir = useCustomNaming ? custom.EffectiveScreenshotSaveFolder : persisted.UnlockScreenshotDirectory;
            if (string.IsNullOrWhiteSpace(baseDir) && custom != null)
            {
                useCustomNaming = true;
                baseDir = custom.EffectiveScreenshotSaveFolder;
            }

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return null;
            }

            // A manual test fire lands in a separate "Test" subfolder so it never mixes with a
            // game's genuine unlock captures.
            if (first.IsTestFire && !useCustomNaming)
            {
                baseDir = System.IO.Path.Combine(baseDir, UnlockScreenshotService.TestFolderName);
            }

            var plan = new WaveScreenshotPlan
            {
                BaseDirectory = baseDir,
                CleanSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixClean),
                WithToastSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixWithToast),
                FramedSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixFramed),
                CustomSettings = useCustomNaming ? custom : null,
                CaptureDelayMilliseconds = custom?.ScreenshotDelayMilliseconds ?? 0,
                IsTestFire = first.IsTestFire,
            };
            foreach (var vm in wave)
            {
                // Each variant is gated independently by its own per-variant rarity threshold and
                // completion bypass. The policy ANDs the EnableUnlockScreenshots master switch into
                // each variant flag.
                var variants = UnlockScreenshotVariantPolicy.Resolve(
                    vm.Rarity,
                    vm.IsGameCompleted || vm.IsCompletionAchievement || vm.IsCapstone,
                    vm.ProviderKey,
                    persisted);

                if (variants != ScreenshotVariants.None)
                {
                    plan.Items.Add((vm, variants));
                }
            }

            return plan.Items.Count > 0 ? plan : null;
        }

        private static bool ShouldUseCustomScreenshotNaming(LocalSettings custom, PersistedSettings persisted)
        {
            if (custom == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(persisted?.UnlockScreenshotDirectory) ||
                   !string.IsNullOrWhiteSpace(custom.ScreenshotSaveFolder) ||
                   !string.Equals(
                       custom.ScreenshotFilenameTemplate,
                       LocalSettings.DefaultScreenshotFilenameTemplate,
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Trims a configured screenshot filename suffix; blank collapses to null so the path
        /// builder emits no suffix at all.
        /// </summary>
        private static string NormalizeSuffix(string suffix)
        {
            return string.IsNullOrWhiteSpace(suffix) ? null : suffix.Trim();
        }

        /// <summary>
        /// Saves all requested screenshot variants for a wave. Starts on the UI thread
        /// (fire-and-forget from the toast pipeline): framed composites render on the dispatcher
        /// at Background priority so the toast animation stays smooth, and all PNG/file I/O is
        /// offloaded to the thread pool. Owns disposal of the base capture and every per-item
        /// with-notification composite.
        /// </summary>
        private async Task SaveWaveScreenshotsAsync(
            WaveScreenshotPlan plan,
            Task<System.Drawing.Bitmap> baseCaptureTask,
            Dictionary<AchievementToastViewModel, System.Drawing.Bitmap> toastByVm)
        {
            System.Drawing.Bitmap baseBitmap = null;
            try
            {
                if (baseCaptureTask != null)
                {
                    baseBitmap = await baseCaptureTask.ConfigureAwait(true);
                }

                if (baseBitmap == null)
                {
                    _logger?.Warn("[Screenshot] Capture returned no image; no unlock screenshot was saved.");
                }

                var framedByVm = new Dictionary<AchievementToastViewModel, System.Windows.Media.Imaging.BitmapSource>();
                if (plan.NeedsFrame && baseBitmap != null)
                {
                    var captured = baseBitmap;
                    var cleanSource = await Task.Run(() => ScreenshotFrameCompositor.ToBitmapSource(captured))
                        .ConfigureAwait(true);
                    if (cleanSource != null)
                    {
                        foreach (var item in plan.Items)
                        {
                            if ((item.Variants & ScreenshotVariants.Framed) == 0)
                            {
                                continue;
                            }

                            await Dispatcher.Yield(DispatcherPriority.Background);
                            if (_disposed)
                            {
                                break;
                            }

                            // Scope the frame template to each item's game/provider (game >
                            // provider > global) so a per-game or per-platform custom frame applies.
                            var frameTemplate = _templateResolver.ResolveFrameTemplate(
                                item.Vm.FrameUseThemeStyling,
                                item.Vm.ProviderKey,
                                item.Vm.PlayniteGameId);
                            if (frameTemplate == null)
                            {
                                continue;
                            }

                            var framed = _frameCompositor.ComposeFramed(cleanSource, frameTemplate, item.Vm);
                            if (framed != null)
                            {
                                framedByVm[item.Vm] = framed;
                            }
                        }
                    }
                }

                var baseDir = plan.BaseDirectory;
                var items = plan.Items;
                var clean = baseBitmap;
                var toasts = toastByVm;
                baseBitmap = null;
                toastByVm = null;
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var item in items)
                        {
                            var vm = item.Vm;
                            if ((item.Variants & ScreenshotVariants.Clean) != 0 && clean != null)
                            {
                                if (plan.UsesCustomNaming)
                                {
                                    _screenshotService.SaveUsingCustomSettings(
                                        clean,
                                        plan.CustomSettings,
                                        _api?.Database?.Games?.Get(vm.PlayniteGameId),
                                        vm.ProviderKey,
                                        vm.AchievementName,
                                        vm.AchievementNumber,
                                        vm.TotalCount,
                                        plan.CleanSuffix,
                                        plan.IsTestFire);
                                }
                                else
                                {
                                    _screenshotService.Save(
                                        clean, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                        vm.AchievementNumber, vm.TotalCount,
                                        plan.CleanSuffix);
                                }
                            }

                            if ((item.Variants & ScreenshotVariants.WithToast) != 0 &&
                                toasts != null && toasts.TryGetValue(vm, out var toastShot) && toastShot != null)
                            {
                                if (plan.UsesCustomNaming)
                                {
                                    _screenshotService.SaveUsingCustomSettings(
                                        toastShot, plan.CustomSettings,
                                        _api?.Database?.Games?.Get(vm.PlayniteGameId), vm.ProviderKey,
                                        vm.AchievementName, vm.AchievementNumber, vm.TotalCount,
                                        plan.WithToastSuffix, plan.IsTestFire);
                                }
                                else
                                {
                                    _screenshotService.Save(
                                        toastShot, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                        vm.AchievementNumber, vm.TotalCount,
                                        plan.WithToastSuffix);
                                }
                            }

                            if (framedByVm.TryGetValue(vm, out var framed))
                            {
                                if (plan.UsesCustomNaming)
                                {
                                    _screenshotService.SaveUsingCustomSettings(
                                        framed, plan.CustomSettings,
                                        _api?.Database?.Games?.Get(vm.PlayniteGameId), vm.ProviderKey,
                                        vm.AchievementName, vm.AchievementNumber, vm.TotalCount,
                                        plan.FramedSuffix, plan.IsTestFire);
                                }
                                else
                                {
                                    _screenshotService.Save(
                                        framed, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                        vm.AchievementNumber, vm.TotalCount,
                                        plan.FramedSuffix);
                                }
                            }
                        }

                        // Drop cached capture scans for the games in this wave so an already-open
                        // grid lights up its Captures button on its next rebuild.
                        foreach (var gameName in items
                            .Select(i => i.Vm?.GameName)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?.Invalidate(gameName);
                        }
                    }
                    finally
                    {
                        clean?.Dispose();
                        DisposeAll(toasts);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Unlock screenshot pipeline failed.");
            }
            finally
            {
                baseBitmap?.Dispose();
                DisposeAll(toastByVm);
            }
        }

        /// <summary>
        /// Disposes every per-item composite in the dictionary (null-safe).
        /// </summary>
        private static void DisposeAll(Dictionary<AchievementToastViewModel, System.Drawing.Bitmap> byVm)
        {
            if (byVm == null)
            {
                return;
            }

            foreach (var bitmap in byVm.Values)
            {
                bitmap?.Dispose();
            }
        }

        /// <summary>
        /// Disposes the bitmap of an in-flight base capture when the wave aborts before the save
        /// pipeline takes ownership.
        /// </summary>
        private static void DisposeCaptureTask(Task<System.Drawing.Bitmap> captureTask)
        {
            captureTask?.ContinueWith(
                t => t.Result?.Dispose(),
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        /// <summary>
        /// Emits the once-per-wave header and display-environment diagnostic lines (gated behind the
        /// compile-time perf tracing flag). Together with the per-placement lines these let a remote
        /// user's log answer whether a mixed-DPI topology or a SizeToContent/DPI HWND mismatch is
        /// behind toast clipping.
        /// </summary>
        private void LogWaveDiagnostics(
            IReadOnlyList<AchievementToastViewModel> cardItems, DataTemplate template, WaveMode mode)
        {
            if (!Common.PerfScope.PerfTracingEnabled)
            {
                return;
            }

            try
            {
                var overridePath = _templateResolver?.ResolveActiveThemeOverridePath();
                var templateSource = template == null
                    ? "null-template"
                    : (string.IsNullOrEmpty(overridePath) ? "default" : $"theme({overridePath})");
                var gameHwnd = ResolveWaveWindowHandle();

                _logger?.Info(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Toast wave: mode={0} corner={1} template={2} items={3} gameHwnd=0x{4:X}",
                    mode,
                    _activePosition,
                    templateSource,
                    cardItems?.Count ?? 0,
                    gameHwnd.ToInt64()));
                _logger?.Info(ToastPlacementDiagnostics.DescribeEnvironment(_api));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast wave diagnostics failed.");
            }
        }

        /// <summary>
        /// One line per wave describing the cadences that were actually achieved (gated behind the
        /// compile-time perf tracing flag): the anchor monitor's refresh rate against the mean interval
        /// between composed frames, and the overlay sampler's frames-seen against samples-taken.
        ///
        /// That last pair is the one worth reading. Sampling is due every recording frame but can only
        /// happen on a composed frame, so samples should equal frames when the two rates match and fall
        /// to the expected fraction when the recording rate is lower. Half the expected count is the
        /// signature of the sampler aliasing against the refresh rate.
        /// </summary>
        private void LogWaveCadence(RenderTickCounter trackTicks, int trackSampleCount)
        {
            if (!Common.PerfScope.PerfTracingEnabled)
            {
                return;
            }

            try
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                var monitorHz = _activeMonitorRefreshHz > 0
                    ? _activeMonitorRefreshHz.ToString(culture)
                    : "unknown";
                if (trackTicks == null)
                {
                    _logger?.Info(string.Format(
                        culture, "Toast cadence: monitorHz={0} track=off", monitorHz));
                    return;
                }

                _logger?.Info(string.Format(
                    culture,
                    "Toast cadence: monitorHz={0} tickMean={1:0.00}ms frames={2} sampleTarget={3:0.00}ms samples={4}",
                    monitorHz,
                    trackTicks.MeanIntervalMs,
                    trackTicks.Frames,
                    TrackSampleIntervalMs(),
                    trackSampleCount));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast cadence diagnostics failed.");
            }
        }

        private void PlaceWindow(Window window)
        {
            PlaceWindow(window, null);
        }

        /// <summary>
        /// Positions the toast within the current placement area. When <paramref name="stage"/> is
        /// supplied and perf tracing is compiled on, emits one diagnostic line describing exactly
        /// what drove this placement (coordinate spaces, DPI transform, resulting rect and HWND).
        /// The per-frame follow path passes no stage so the hot path stays silent.
        /// </summary>
        private void PlaceWindow(Window window, string stage)
        {
            if (window == null)
            {
                return;
            }

            // Verify where the window really landed only on the settled stages: "preshow" runs before
            // the window has a laid-out size, and "rendered" can still be mid-DPI-settle.
            var measure = stage == "shown" || stage == "snap";

            // Position the per-monitor toast in physical pixels relative to the anchor.
            if (TryPlacePhysical(window, measure, out var outcome) &&
                stage != null && Common.PerfScope.PerfTracingEnabled)
            {
                _logger?.Info(ToastPlacementDiagnostics.DescribePhysicalPlacement(
                    stage, window, _activeReferenceHwnd, _activeMonitorScale, outcome.TargetX, outcome.TargetY));
            }
        }

        /// <summary>
        /// Repositions the toast against the anchor (cheap per-frame path used while following it).
        /// Leaves the toast where it is if the anchor can't be measured.
        /// </summary>
        private void PlaceWindowToHandle(Window window)
        {
            if (window == null)
            {
                return;
            }

            TryPlacePhysical(window, false, out _);
        }

        private bool AlignRight()
        {
            return _activePosition == ToastScreenCorner.TopRight || _activePosition == ToastScreenCorner.BottomRight;
        }

        private bool AlignBottom()
        {
            return _activePosition == ToastScreenCorner.BottomLeft || _activePosition == ToastScreenCorner.BottomRight;
        }

        // The window-edge gap in DIPs: the visible-body gap (CornerGapDip) less the card's own glow
        // margin, so the body sits a constant distance from the corner whether or not the glow is on.
        private double EffectiveGapDip()
        {
            return (_activeUsesCustomMediaSurface ? 16d : CornerGapDip) - _activeCardGlow;
        }

        private void ComputeActiveCorner(
            System.Drawing.Rectangle anchorPhys, int width, int height, out int x, out int y)
        {
            if (_activeCenterHorizontally)
            {
                var gap = (int)Math.Round(EffectiveGapDip() * Math.Max(0.1, _activeMonitorScale));
                x = anchorPhys.Left + ((anchorPhys.Width - width) / 2);
                y = AlignBottom()
                    ? anchorPhys.Bottom - height - gap
                    : anchorPhys.Top + gap;
                return;
            }

            ToastWindowPlacer.ComputeCorner(
                anchorPhys, width, height, _activeMonitorScale,
                AlignRight(), AlignBottom(), EffectiveGapDip(), out x, out y);
        }

        private ToastScreenCorner ResolveCustomMediaPosition()
        {
            switch (ProviderRegistry.Settings<LocalSettings>()?.UnlockOverlayPosition)
            {
                case LocalUnlockOverlayPosition.TopLeft:
                    return ToastScreenCorner.TopLeft;
                case LocalUnlockOverlayPosition.BottomLeft:
                    return ToastScreenCorner.BottomLeft;
                case LocalUnlockOverlayPosition.TopCenter:
                case LocalUnlockOverlayPosition.TopRight:
                    return ToastScreenCorner.TopRight;
                case LocalUnlockOverlayPosition.BottomCenter:
                case LocalUnlockOverlayPosition.BottomRight:
                default:
                    return ToastScreenCorner.BottomRight;
            }
        }

        /// <summary>
        /// The physical-pixel anchor rect the toast is placed against: the game's client rect when a
        /// game is running (so the toast sits over the game and inside the screenshot), otherwise the
        /// Playnite window's monitor work area. False when the anchor can't be measured.
        /// </summary>
        private bool TryResolveAnchor(out System.Drawing.Rectangle anchorPhys)
        {
            anchorPhys = System.Drawing.Rectangle.Empty;
            if (_activeReferenceHwnd == IntPtr.Zero)
            {
                return false;
            }

            if (!_activeIsGame)
            {
                return ToastWindowPlacer.TryGetMonitorWorkAreaPhysical(_activeReferenceHwnd, out anchorPhys);
            }

            // Read the game client rect as true device pixels (Per-Monitor-V2 scope), matching the
            // physical SetWindowPos and the monitor-work-area anchor. In a system-aware context these
            // client-rect APIs return system-virtualized coordinates, which would place the toast wrong
            // on a monitor whose scale differs from the system DPI.
            using (Common.DpiAwarenessScope.PerMonitorV2())
            {
                return _screenshotService.TryGetClientBounds(_activeReferenceHwnd, out anchorPhys);
            }
        }

        /// <summary>
        /// Positions the toast at the anchor corner in physical pixels via
        /// <see cref="ToastWindowPlacer"/>. Returns false (doing nothing) when the anchor can't be
        /// measured.
        /// </summary>
        private bool TryPlacePhysical(Window window, bool measure, out ToastWindowPlacer.PlacementOutcome outcome)
        {
            outcome = default(ToastWindowPlacer.PlacementOutcome);
            if (!TryResolveAnchor(out var anchorPhys))
            {
                return false;
            }

            var renderScale = ToastWindowPlacer.RenderScale(window);
            bool placed;
            if (_activeCenterHorizontally && window.ActualWidth > 0 && window.ActualHeight > 0)
            {
                var width = Math.Max(1, (int)Math.Round(window.ActualWidth * renderScale));
                var height = Math.Max(1, (int)Math.Round(window.ActualHeight * renderScale));
                ComputeActiveCorner(anchorPhys, width, height, out var x, out var y);
                ToastWindowPlacer.MovePhysical(window, x, y);
                placed = true;
            }
            else
            {
                placed = ToastWindowPlacer.PositionPhysical(
                    window, anchorPhys, renderScale, _activeMonitorScale, AlignRight(), AlignBottom(), EffectiveGapDip(),
                    measure, ref _placementCorrection, out outcome);
                LogPlacementAnomaly(window, anchorPhys, renderScale, outcome);
            }

            // Keep the toast directly above the game window in the z-order (not owned, so the game is
            // never raised). Re-asserted every placement/follow frame so it stays interleaved as the
            // user moves between windows. Only for a running-game anchor; the Playnite/preview case
            // keeps its topmost float, and an unrevealed wave stays out of the z-order entirely.
            if (!_activeSuppressZOrder && _activeIsGame && _activeReferenceHwnd != IntPtr.Zero)
            {
                ToastWindowPlacer.SetZOrderAbove(window, _activeReferenceHwnd);
            }

            return placed;
        }

        /// <summary>
        /// Emits one warning per wave when a placement had to be rescued — the computed corner fell
        /// outside the anchor, or the HWND did not land where <c>SetWindowPos</c> was asked to put it.
        /// Both mean the coordinate spaces feeding the corner math disagree, which is what makes a
        /// toast invisible at a display scale we cannot reproduce, so the line carries everything
        /// needed to identify the setup. Silent on a healthy placement, and capped at one line because
        /// the per-frame follow path runs through here on every rendered frame.
        /// </summary>
        private void LogPlacementAnomaly(
            Window window,
            System.Drawing.Rectangle anchorPhys,
            double renderScale,
            ToastWindowPlacer.PlacementOutcome outcome)
        {
            if (_placementAnomalyLogged || (!outcome.Clamped && !outcome.Mismatched))
            {
                return;
            }

            _placementAnomalyLogged = true;
            try
            {
                var achieved = outcome.Achieved.IsEmpty
                    ? "unread"
                    : $"({outcome.Achieved.Left},{outcome.Achieved.Top} {outcome.Achieved.Width}x{outcome.Achieved.Height})";
                _logger?.Warn(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[Toast] Placement corrected: corner={0} clamped={1} mismatched={2} target=({3},{4}) actual={5} " +
                    "offset=({6},{7}) anchor=({8},{9} {10}x{11}) monScale={12:0.###} sysScale={13:0.###} " +
                    "render={14:0.###} size={15:0.0}x{16:0.0} thread={17} isGame={18}",
                    _activePosition,
                    outcome.Clamped,
                    outcome.Mismatched,
                    outcome.TargetX,
                    outcome.TargetY,
                    achieved,
                    _placementCorrection.OffsetX,
                    _placementCorrection.OffsetY,
                    anchorPhys.Left,
                    anchorPhys.Top,
                    anchorPhys.Width,
                    anchorPhys.Height,
                    _activeMonitorScale,
                    ToastWindowPlacer.SystemScale(),
                    renderScale,
                    window?.ActualWidth ?? 0,
                    window?.ActualHeight ?? 0,
                    Common.DpiAwarenessScope.DescribeThreadContext(),
                    _activeIsGame));
                _logger?.Warn(ToastPlacementDiagnostics.DescribeEnvironment(_api));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast placement anomaly logging failed.");
            }
        }

        private bool TryComputeRestingCorner(Window window, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!TryResolveAnchor(out var anchorPhys))
            {
                return false;
            }

            var renderScale = ToastWindowPlacer.RenderScale(window);
            if (_activeCenterHorizontally && window.ActualWidth > 0 && window.ActualHeight > 0)
            {
                var width = Math.Max(1, (int)Math.Round(window.ActualWidth * renderScale));
                var height = Math.Max(1, (int)Math.Round(window.ActualHeight * renderScale));
                ComputeActiveCorner(anchorPhys, width, height, out x, out y);
                return true;
            }

            return ToastWindowPlacer.TryComputeCorner(
                window, anchorPhys, renderScale, _activeMonitorScale, AlignRight(), AlignBottom(), EffectiveGapDip(),
                out x, out y, out _);
        }

        /// <summary>
        /// The HWND of the current Playnite window (an open settings popup sits on the same monitor),
        /// used as the toast anchor when no game window is running so the toast lands on the monitor
        /// Playnite is on. IntPtr.Zero if none can be resolved.
        /// </summary>
        private IntPtr ResolveAppWindowHandle()
        {
            try
            {
                var appWindow = _api?.Dialogs?.GetCurrentAppWindow() ?? Application.Current?.MainWindow;
                return appWindow != null ? new WindowInteropHelper(appWindow).Handle : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Largest fraction of the anchor's width / height the toast card is allowed to occupy. Sizing
        /// by proportion instead of a fixed size keeps the toast modest across every resolution and
        /// display-scale combination: on a small panel the natural card is a large fraction of the area
        /// and gets shrunk to fit; on a roomy display it stays at its natural, readable size.
        /// </summary>
        private const double MaxToastWidthFraction = 0.40;
        private const double MaxToastHeightFraction = 0.25;

        /// <summary>
        /// The scale applied to the toast content so it fits within the per-axis width/height fractions
        /// of the anchor. Returns 1.0 (no scaling) when the content already fits or the anchor/natural
        /// size cannot be resolved; only ever shrinks. The anchor is in physical pixels and the natural
        /// card is in DIPs, so the comparison is made in physical pixels (natural * monitorScale, the
        /// size the DPI-compensated card will actually render at).
        /// </summary>
        private double ResolveFitScale(FrameworkElement content)
        {
            try
            {
                if (!TryResolveAnchor(out var anchorPhys) || anchorPhys.Width <= 0 || anchorPhys.Height <= 0)
                {
                    return 1.0;
                }

                // Natural (unscaled) DIP size the content wants. Measuring an unshown element can throw,
                // so fall back to the default card footprint.
                var natural = new Size(ToastWindowPlacer.DefaultCardWidthDip, ToastWindowPlacer.DefaultCardHeightDip);
                try
                {
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (content.DesiredSize.Width > 0 && content.DesiredSize.Height > 0)
                    {
                        natural = content.DesiredSize;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Toast fit measure failed; using default card size.");
                }

                var scaleToPhys = _activeMonitorScale > 0 ? _activeMonitorScale : 1.0;
                var widthScale = (MaxToastWidthFraction * anchorPhys.Width) / (natural.Width * scaleToPhys);

                // Cap height per individual card, not for the whole stack: a wave of several toasts
                // stacks vertically, and should grow tall rather than shrinking every card. Only a
                // single card that is itself taller than the fraction gets scaled down.
                var itemCount = (content as ItemsControl)?.Items.Count ?? 1;
                var perItemHeight = (natural.Height / Math.Max(1, itemCount)) * scaleToPhys;
                var heightScale = (MaxToastHeightFraction * anchorPhys.Height) / perItemHeight;
                var scale = Math.Min(widthScale, heightScale);

                return scale < 1.0 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// Re-applies the content DPI compensation using the scale WPF actually renders the (now shown,
        /// on-target-monitor) per-monitor window at, which can only be read post-Show:
        /// comp = monitorScale / renderScale. That is ~1 when WPF already scales the per-monitor window
        /// to the monitor, or the missing factor when it renders at the system scale, so the card lands
        /// at the monitor's true physical size either way. Forces layout so the following placement uses
        /// the corrected size; the window is still Opacity=0, so any resize is invisible.
        /// </summary>
        private void ApplyDpiCompensation(Window window, FrameworkElement content, double fitScale)
        {
            var renderScale = ToastWindowPlacer.RenderScale(window);
            if (renderScale <= 0)
            {
                return;
            }

            var comp = _activeMonitorScale / renderScale;
            if (comp <= 0)
            {
                comp = 1.0;
            }

            var scale = fitScale * comp;
            content.LayoutTransform = Math.Abs(scale - 1.0) > ContentScaleEpsilon
                ? new ScaleTransform(scale, scale)
                : null;

            try
            {
                window.UpdateLayout();
            }
            catch
            {
                // Best-effort; the later placement passes still correct the size if layout defers.
            }
        }

        // Physical-pixel slide for the in-game path: the window is positioned by SetWindowPos, so the
        // WPF Window.Top animation can't be used. The physical Y is interpolated using the easing and
        // duration authored in the themeable slide storyboards (SlideIn/SlideOutStoryboardKey); these
        // are only the fallbacks used when a theme defines no slide storyboard.
        private const double SlideOvershootAmplitude = 0.35;
        private const int SlideInDurationMs = 240;
        private const int SlideOutDurationMs = 200;
        // Extra travel beyond the card height so the card fully clears the screen edge in and out.
        private const double SlideTravelPaddingDip = 40d;
        // Small pause after a slide-out finishes before the window is torn down.
        private const int SlideSettleBufferMs = 10;
        // Below this, the content scale is treated as 1.0 and no LayoutTransform is applied.
        private const double ContentScaleEpsilon = 0.001;
        // Post-Show wait for the per-monitor DPI change to settle before revealing the toast: poll the
        // window's render scale up to MaxDpiSettleFrames times, one monitor frame apart, until it reaches
        // the target monitor's scale (within DpiSettleTolerance). Bounds a worst case at ~1-2 frames for
        // the common case and never hangs.
        private const int MaxDpiSettleFrames = 8;
        private const double DpiSettleTolerance = 0.01;
        // Frame period assumed when the anchor monitor's refresh rate can't be read (60 Hz).
        private const double FallbackFramePeriodMs = 1000d / 60d;
        // Recording frame rate assumed when settings are unreachable; matches PersistedSettings' default.
        private const int FallbackRecordingFps = 30;

        /// <summary>
        /// One frame of the monitor the current wave is on (ms), or a 60 Hz frame when its refresh rate
        /// could not be read. The upper bound on how often anything on screen can change.
        /// </summary>
        private double MonitorFramePeriodMs()
        {
            return _activeMonitorRefreshHz > 0 ? 1000d / _activeMonitorRefreshHz : FallbackFramePeriodMs;
        }

        /// <summary>
        /// One frame of the configured recording frame rate (ms) — the interval the overlay tracks are
        /// sampled at, since the tracks exist only to be composited into clips at that rate. Sampling
        /// faster would hand export samples it cannot place; slower is what made a 60 fps clip show every
        /// toast position twice.
        /// </summary>
        private double TrackSampleIntervalMs()
        {
            var fps = _settings?.Persisted?.RecordingFps ?? FallbackRecordingFps;
            return 1000d / Math.Max(1, fps);
        }

        private static readonly IEasingFunction DefaultSlideInEase =
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = SlideOvershootAmplitude };
        private static readonly IEasingFunction DefaultSlideOutEase =
            new CubicEase { EasingMode = EasingMode.EaseIn };

        /// <summary>
        /// Per-frame bookkeeping for one <c>CompositionTarget.Rendering</c> subscription: hands the
        /// handler the composing frame's timestamp, reports a repeated tick so the work is done once per
        /// composed frame, and keeps the frame count and span so the wave's real tick rate is reportable.
        ///
        /// The timestamp is the frame's composition time rather than the time the handler happened to
        /// run, so motion driven from it is spaced as evenly as the frames themselves. The source is
        /// fixed on the first tick — <see cref="RenderingEventArgs.RenderingTime"/> when WPF supplies it,
        /// else a local stopwatch — so the epoch never changes mid-subscription.
        /// </summary>
        private sealed class RenderTickCounter
        {
            private readonly System.Diagnostics.Stopwatch _fallbackClock =
                System.Diagnostics.Stopwatch.StartNew();
            private bool _sourceChosen;
            private bool _useRenderingTime;
            private double _firstMs;
            private double _lastMs = double.NegativeInfinity;

            /// <summary>Distinct composed frames observed.</summary>
            public int Frames { get; private set; }

            /// <summary>Mean interval between the observed frames (ms); 0 below two frames.</summary>
            public double MeanIntervalMs => Frames > 1 ? (_lastMs - _firstMs) / (Frames - 1) : 0d;

            /// <summary>
            /// True when this event carries a frame not seen yet, with <paramref name="elapsedMs"/> set to
            /// that frame's time since the first observed one.
            /// </summary>
            public bool TryAdvance(EventArgs e, out double elapsedMs)
            {
                elapsedMs = 0d;
                var renderingTime = (e as RenderingEventArgs)?.RenderingTime;
                if (!_sourceChosen)
                {
                    _useRenderingTime = renderingTime.HasValue;
                    _sourceChosen = true;
                }

                var nowMs = _useRenderingTime && renderingTime.HasValue
                    ? renderingTime.Value.TotalMilliseconds
                    : _fallbackClock.Elapsed.TotalMilliseconds;
                if (nowMs <= _lastMs)
                {
                    return false;
                }

                if (Frames == 0)
                {
                    _firstMs = nowMs;
                }

                _lastMs = nowMs;
                Frames++;
                elapsedMs = nowMs - _firstMs;
                return true;
            }
        }

        /// <summary>
        /// Runs the slide-in and, when <paramref name="reveal"/> is set, makes the window visible.
        /// A unrevealed wave slides without revealing: the motion is what the overlay track records,
        /// so the composited clip shows the same slide-in a visible notification would.
        /// </summary>
        private void SlideInPhysical(Window window, bool reveal)
        {
            if (window == null)
            {
                return;
            }

            if (reveal)
            {
                window.Opacity = 1;
            }

            if (!TryComputeRestingCorner(window, out var rx, out var ry))
            {
                return;
            }

            ResolveSlideTiming(
                AchievementToastTemplateResolver.SlideInStoryboardKey, DefaultSlideInEase, SlideInDurationMs,
                out var ease, out var durationMs);
            var distance = SlideDistancePhysical(window);
            var startY = SlideFromBottom() ? ry + distance : ry - distance;
            RunPhysicalSlide(window, rx, startY, ry, ease, durationMs);
        }

        // Returns the slide-out duration (ms) so the caller waits exactly that long; 0 if it didn't run.
        private double SlideOutPhysical(Window window)
        {
            if (window == null)
            {
                return 0;
            }

            if (!TryComputeRestingCorner(window, out var rx, out var ry))
            {
                return 0;
            }

            ResolveSlideTiming(
                AchievementToastTemplateResolver.SlideOutStoryboardKey, DefaultSlideOutEase, SlideOutDurationMs,
                out var ease, out var durationMs);
            var distance = SlideDistancePhysical(window);
            var endY = SlideFromBottom() ? ry + distance : ry - distance;
            RunPhysicalSlide(window, rx, ry, endY, ease, durationMs);
            return durationMs;
        }

        // Easing + duration for a physical slide, taken from the themeable storyboard when it defines
        // them, else the supplied fallbacks. Reuses ResolveAnimation, which clones the storyboard's
        // first DoubleAnimation (the same resource the countdown bar reads).
        private void ResolveSlideTiming(
            string storyboardKey, IEasingFunction fallbackEase, double fallbackMs,
            out IEasingFunction ease, out double durationMs)
        {
            ease = fallbackEase;
            durationMs = fallbackMs;

            var animation = ResolveAnimation(storyboardKey);
            if (animation == null)
            {
                return;
            }

            if (animation.EasingFunction != null)
            {
                ease = animation.EasingFunction;
            }

            if (animation.Duration.HasTimeSpan)
            {
                durationMs = animation.Duration.TimeSpan.TotalMilliseconds;
            }
        }

        private int SlideDistancePhysical(Window window)
        {
            return (int)Math.Round(SlideDistance(window) * ToastWindowPlacer.RenderScale(window));
        }

        // Animates the toast's physical Y from fromY to toY over durationMs, eased per `ease`, moving
        // the HWND each frame. Any prior slide is stopped first. Replaces the WPF Window.Top slide for
        // the physical (in-game) path.
        private void RunPhysicalSlide(Window window, int x, int fromY, int toY, IEasingFunction ease, double durationMs)
        {
            StopActiveSlide();
            if (durationMs <= 0)
            {
                MoveCorrected(window, x, toY);
                return;
            }

            // Progress comes from each frame's composition time, not from when the handler ran: the
            // window moves once per composed frame either way, but timing it off the frame keeps the
            // steps as evenly spaced as the frames, at whatever rate the monitor presents.
            var ticks = new RenderTickCounter();
            EventHandler tick = null;
            tick = (s, e) =>
            {
                if (!ticks.TryAdvance(e, out var elapsedMs))
                {
                    return;
                }

                var t = Math.Min(1.0, elapsedMs / durationMs);
                var k = ease != null ? ease.Ease(t) : t;
                var y = (int)Math.Round(fromY + ((toY - fromY) * k));
                MoveCorrected(window, x, y);
                if (t >= 1.0)
                {
                    CompositionTarget.Rendering -= tick;
                    if (ReferenceEquals(_activeSlideTick, tick))
                    {
                        _activeSlideTick = null;
                    }
                }
            };

            _activeSlideTick = tick;
            MoveCorrected(window, x, fromY);
            CompositionTarget.Rendering += tick;
        }

        // Slide frames move the HWND directly (deliberately off the anchor corner, so they must not be
        // clamped), but they still go through the wave's learned placement correction — otherwise a
        // corrected resting position would jump from an uncorrected slide.
        private void MoveCorrected(Window window, int x, int y)
        {
            ToastWindowPlacer.MovePhysical(
                window, x + _placementCorrection.OffsetX, y + _placementCorrection.OffsetY);
        }

        private void StopActiveSlide()
        {
            if (_activeSlideTick != null)
            {
                CompositionTarget.Rendering -= _activeSlideTick;
                _activeSlideTick = null;
            }
        }

        private static double SlideDistance(Window window)
        {
            var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            if (double.IsNaN(height) || height <= 0)
            {
                height = ToastWindowPlacer.DefaultCardHeightDip;
            }

            return height + SlideTravelPaddingDip;
        }

        private bool SlideFromBottom()
        {
            switch (_activePosition)
            {
                case ToastScreenCorner.TopLeft:
                case ToastScreenCorner.TopRight:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// The corner the toast uses: a theme override (string resource
        /// <see cref="AchievementToastTemplateResolver.PositionResourceKey"/>, e.g. "TopRight") when
        /// present and valid, otherwise the plugin's ToastPosition setting.
        /// </summary>
        private ToastScreenCorner EffectivePosition()
        {
            var setting = _settings?.Persisted?.ToastPosition ?? ToastScreenCorner.BottomRight;
            try
            {
                var raw = _templateResolver?.ResolveResourceValue(
                    AchievementToastTemplateResolver.PositionResourceKey,
                    ToastThemeStylingEnabled);
                var text = raw?.ToString().Trim();
                if (!string.IsNullOrEmpty(text) &&
                    Enum.TryParse(text, ignoreCase: true, result: out ToastScreenCorner parsed) &&
                    Enum.IsDefined(typeof(ToastScreenCorner), parsed))
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve toast position theme override.");
            }

            return setting;
        }

        /// <summary>
        /// The toast display time in seconds: a theme override (numeric or string resource
        /// <see cref="AchievementToastTemplateResolver.DurationSecondsResourceKey"/>) when present and
        /// valid, otherwise the plugin's ToastDurationSeconds setting. Clamped to a 2s minimum to
        /// match the setting's own clamp.
        /// </summary>
        private int EffectiveDurationSeconds()
        {
            var setting = Math.Max(2, _settings?.Persisted?.ToastDurationSeconds ?? 6);
            try
            {
                var raw = _templateResolver?.ResolveResourceValue(
                    AchievementToastTemplateResolver.DurationSecondsResourceKey,
                    ToastThemeStylingEnabled);
                if (raw is double d)
                {
                    return Math.Max(2, (int)Math.Round(d));
                }

                if (raw is int i)
                {
                    return Math.Max(2, i);
                }

                var text = raw?.ToString().Trim();
                if (!string.IsNullOrEmpty(text) &&
                    double.TryParse(
                        text,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    return Math.Max(2, (int)Math.Round(parsed));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve toast duration theme override.");
            }

            return setting;
        }

        private int EffectiveWaveDurationMilliseconds()
        {
            if (_activeUsesCustomMediaSurface)
            {
                return Math.Max(
                    1200,
                    ProviderRegistry.Settings<LocalSettings>()?.UnlockOverlayDurationMilliseconds ?? 3400);
            }

            return EffectiveDurationSeconds() * 1000;
        }

        /// <summary>
        /// Animates every countdown bar in the wave (one per toast) from full to empty over the
        /// display duration, so it reads as an auto-dismiss timer. The animation's shape (from/to,
        /// easing) comes from the themeable PlayAch.Storyboard.ToastCountdown storyboard; its duration
        /// is always the toast's runtime display time so the bar depletes exactly as it dismisses.
        /// </summary>
        private void AnimateCountdownBars(DependencyObject root, int milliseconds)
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            foreach (var bar in FindCountdownBars(root))
            {
                // Assign a fresh, mutable ScaleTransform: a Freezable declared inline in a
                // DataTemplate is frozen/shared, and BeginAnimation on a frozen transform throws.
                var scale = new ScaleTransform(1.0, 1.0);
                bar.RenderTransform = scale;

                var animation = ResolveAnimation(AchievementToastTemplateResolver.CountdownStoryboardKey)
                    ?? new DoubleAnimation(1.0, 0.0, duration) { FillBehavior = FillBehavior.HoldEnd };
                // The countdown must track the actual display time, so the runtime duration always
                // wins over whatever placeholder the storyboard authored.
                animation.Duration = duration;
                // No Timeline.DesiredFrameRate here: a WPF timeline already advances once per composed
                // frame, so requesting the monitor's rate buys nothing, and requesting a rate below the
                // real composition rate (a 59.94 Hz panel reporting 60, adaptive sync, plain rounding)
                // throttles the whole render loop — measured dropping a 163 Hz tick to 90 Hz, which would
                // coarsen the slide as well as the bar.
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            }
        }

        private static IEnumerable<FrameworkElement> FindCountdownBars(DependencyObject root)
        {
            var results = new List<FrameworkElement>();
            if (root == null)
            {
                return results;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element &&
                    string.Equals(element.Name, "PART_ToastCountdown", StringComparison.Ordinal))
                {
                    results.Add(element);
                }

                results.AddRange(FindCountdownBars(child));
            }

            return results;
        }

        /// <summary>
        /// Resolves the first <see cref="DoubleAnimation"/> from a themeable toast storyboard and
        /// returns a detached, mutable clone the caller can patch (from/to/duration) and apply. Returns
        /// null when no storyboard resolves or it declares no DoubleAnimation, signalling the caller to
        /// use its code-built fallback. Only the first DoubleAnimation is used; the window slide and
        /// countdown each drive a single property.
        /// </summary>
        /// <summary>
        /// The toast theme opt-out covers the whole theme toast surface (template, storyboards,
        /// position, duration) since they all ship in the same theme override file.
        /// </summary>
        private bool ToastThemeStylingEnabled => _activeToastThemeStylingEnabled;

        private DoubleAnimation ResolveAnimation(string storyboardKey)
        {
            try
            {
                var storyboard = _templateResolver?.ResolveStoryboard(storyboardKey, ToastThemeStylingEnabled);
                var animation = storyboard == null ? null : GetFirstDoubleAnimation(storyboard);
                return (DoubleAnimation)animation?.Clone();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to resolve toast storyboard '{storyboardKey}'.");
                return null;
            }
        }

        private static DoubleAnimation GetFirstDoubleAnimation(Storyboard storyboard)
        {
            foreach (var child in storyboard.Children)
            {
                if (child is DoubleAnimation animation)
                {
                    return animation;
                }
            }

            return null;
        }

        private Dispatcher GetDispatcher()
        {
            return _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
        }

        public void Dispose()
        {
            _disposed = true;
            PlayniteAchievementsPlugin.AchievementUnlocked -= OnAchievementUnlocked;
            _queue.Clear();
            try
            {
                _activeWindow?.Close();
            }
            catch
            {
            }
        }
    }
}
