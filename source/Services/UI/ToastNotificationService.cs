using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Capture;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

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
        // Window-bearing waves shown by this process; see the [Toast] Fire diagnostic line.
        private int _waveSequence;
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
        // The wave's card surface (the ItemsControl holding the cards) and the slide host wrapping it.
        // The window is sized to the host, which reserves the slide's travel past the card on the entry
        // side; the card itself moves inside that room via _activeSlideTransform. Placement therefore
        // measures the card, not the window — see ToastWindowPlacer.TryMeasureCardPhysical.
        private ItemsControl _activeCardSurface;
        // Per-wave rasterization surfaces for the track sampler, one per card; created with the
        // recorder, dropped in the wave's finally. Null while no wave is recording tracks.
        private Dictionary<AchievementToastViewModel, CardRenderScratch> _trackRenderScratch;
        private FrameworkElement _activeSlideHost;
        private TranslateTransform _activeSlideTransform;
        // Frame counter attached for a running slide's span. It does no work beyond counting: the slide
        // itself is a WPF storyboard, and this exists only so the [Toast] Slide line can still report the
        // cadence the motion actually got.
        private EventHandler _activeSlideTick;
        // The running slide's frame bookkeeping, direction and requested duration, kept out of the tick
        // closure so the one diagnostic line each slide emits can be written from either exit: the slide
        // is routinely force-stopped (the post-slide snap, teardown) before its final frame runs.
        // ReportActiveSlide nulls the counter, so whichever exit comes first reports and the other is a
        // no-op.
        private RenderTickCounter _activeSlideTicks;
        private string _activeSlideLabel;
        private double _activeSlideRequestedMs;
        // Peak-to-peak travel the running slide actually produced, in host DIPs. Reported so a slide
        // that animated nothing is visible in the log rather than looking identical to a healthy one.
        private double _activeSlideMovedDip;
        // The wave's slide storyboards and their durations, resolved once per wave by
        // ResolveWaveSlideTiming from the themeable resources. The storyboards are what actually run;
        // the durations are kept alongside because the wave waits a computed duration rather than a
        // Completed event, so a misauthored theme storyboard cannot stall the lifecycle. Null means
        // "no usable storyboard" and the built-in animation is used, so a slide can never run on nothing.
        private Storyboard _activeSlideInStoryboard;
        private double _activeSlideInMs = SlideInDurationMs;
        private Storyboard _activeSlideOutStoryboard;
        private double _activeSlideOutMs = SlideOutDurationMs;
        // Whether each resolved storyboard actually moves the card along the slide axis. A theme is free
        // to replace the slide with a fade or a scale, which animates nothing positional — and then the
        // card must stay at its resting corner rather than be parked at the slide's start, and the window
        // needs no travel room reserved. True for the built-in slide.
        private bool _activeSlideInTravels = true;
        private bool _activeSlideOutTravels = true;
        // The storyboard currently running, so StopActiveSlide can stop the right one.
        private Storyboard _runningSlideStoryboard;
        // Per-wave placement state: the offset between where SetWindowPos is asked to put the toast
        // and where its HWND lands (measured once, on the wave's first settled placement), and whether
        // this wave has already logged that its placement needed rescuing.
        private ToastWindowPlacer.PlacementCorrection _placementCorrection;
        private bool _placementAnomalyLogged;
        // One drift warning per wave; see WarnOnSettledCardDrift.
        private bool _placementDriftLogged;

        public ToastNotificationService(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            Action ensureResourcesLoaded,
            Func<Guid?, int?> getGameProcessId = null,
            ActiveGameWindowTracker windowTracker = null,
            GameCustomDataStore gameCustomDataStore = null,
            Func<AchievementUnlockedEventArgs, bool> needsOverlayTrack = null)
        {
            _api = api;
            _settings = settings;
            _logger = logger;
            _ensureResourcesLoaded = ensureResourcesLoaded;
            _getGameProcessId = getGameProcessId;
            _windowTracker = windowTracker;
            _gameCustomDataStore = gameCustomDataStore;
            _needsOverlayTrack = needsOverlayTrack;
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
                WaveDisplayed?.Invoke(this, new ToastWaveDisplayedEventArgs(wave, CaptureTimelineClock.UtcNow, soundPlayedUtc));
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
                if (tracks.Count == 0)
                {
                    _logger?.Warn(
                        "[Recording] Toast overlay recorder completed without card samples; " +
                        "unlock videos for this wave cannot composite the notification.");
                    return;
                }

                TracksCompleted?.Invoke(this, new ToastTracksCompletedEventArgs(tracks));
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

            if (ShouldToast(args.IsPreview, args.IsFriendUnlock, args.ProviderKey))
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
            if (persisted?.EnableUnlockScreenshots != true ||
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
        private bool ShouldToast(bool isPreview, bool isFriendUnlock, string providerKey)
        {
            if (isPreview)
            {
                return true;
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
        /// (inside <see cref="UnlockScreenshotService.CaptureGameWindow(IntPtr, int?, int)"/>), but
        /// a manual test fire captures the whole monitor the Playnite window sits on, since the
        /// notification is placed there and there is no game screen to show. Window handles are
        /// resolved here on the UI thread; the blit runs on the pool.
        /// <para>
        /// The configured resolution cap is read per wave, so a settings change takes effect on the
        /// next unlock. Capping the base capture here — rather than the saved files — is what makes
        /// the notification card and frame chrome scale with a downscaled screenshot.
        /// </para>
        /// </summary>
        private Task<System.Drawing.Bitmap> StartWaveSurfaceCapture(bool isTestFire)
        {
            var capHeight = ResolutionCapMath.CapHeightFor(
                _settings?.Persisted?.ScreenshotResolution ?? ScreenshotResolution.Native);
            var gameRunning = TryResolveWaveGame(out var waveHwnd, out var processId);
            if (!gameRunning && isTestFire)
            {
                var appHwnd = ResolveAppWindowHandle();
                return Task.Run(() => _screenshotService.CaptureMonitor(appHwnd, capHeight));
            }

            // All running-game shots capture the game window (WGC per-window, HDR-correct, client
            // area). The with-notification card is composited onto this same capture per item (see
            // ComposeWaveWithToastAsync) — the toast is a separate window; a monitor capture would
            // grab whatever is actually on top, not the game.
            return Task.Run(() => _screenshotService.CaptureGameWindow(waveHwnd, processId, capHeight));
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
            var itemsControl = _activeCardSurface;
            var overlays = new List<(AchievementToastViewModel Vm, System.Drawing.Bitmap Overlay, System.Drawing.Rectangle Rect)>();
            foreach (var vm in withToastVms)
            {
                System.Drawing.Bitmap overlay = null;
                var rect = System.Drawing.Rectangle.Empty;
                if (haveGeometry && itemsControl != null)
                {
                    var container = itemsControl.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;
                    if (container != null)
                    {
                        overlay = TryRenderToastItemOverlay(window, container, out var physSize);
                        if (overlay != null)
                        {
                            ToastWindowPlacer.ComputeCorner(
                                anchorPhys, physSize.Width, physSize.Height, _activeMonitorScale,
                                AlignRight(), AlignBottom(), EffectiveGapDip(), out var ix, out var iy);
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
        /// <summary>
        /// One card's reusable sampler state: the RenderTargetBitmap re-rendered in place every
        /// tick while the card's pixel size and DPI stay put, plus the shadow-layer bookkeeping —
        /// which effects the halo capture covered, the layer's pixel size, and the glow effect
        /// whose animated opacity the per-sample glow scale follows.
        /// </summary>
        private sealed class CardRenderScratch
        {
            public System.Windows.Media.Imaging.RenderTargetBitmap Rtb;
            public int PixelW;
            public int PixelH;
            public double DpiX;
            public double DpiY;

            /// <summary>Pixel size the shadow layer was captured at; 0 when none was captured.</summary>
            public int ShadowW;
            public int ShadowH;

            /// <summary>The exact effect instances the shadow layer baked, in tree order.</summary>
            public List<Effect> ShadowEffectSignature;

            /// <summary>The effect whose animated opacity drives the per-sample glow scale.</summary>
            public DropShadowEffect GlowEffect;
            public double GlowRefOpacity = 1.0;
        }

        /// <summary>
        /// Every effect-carrying element under <paramref name="root"/>, in visual-tree order.
        /// </summary>
        private static void CollectEffects(
            DependencyObject root, List<KeyValuePair<FrameworkElement, Effect>> results)
        {
            if (root is FrameworkElement fe && fe.Effect != null)
            {
                results.Add(new KeyValuePair<FrameworkElement, Effect>(fe, fe.Effect));
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                CollectEffects(VisualTreeHelper.GetChild(root, i), results);
            }
        }

        /// <summary>
        /// Detaches the given effects for the span of a synchronous render, returning what each
        /// element's local Effect value was so <see cref="RestoreEffects"/> can put provenance
        /// back exactly: a style/trigger-supplied effect is restored by clearing the local value
        /// (a lingering local null would permanently override the trigger), a locally-set one by
        /// setting it back. Elements whose local value is an expression are left alone — stripping
        /// them would break the binding — at the cost of rendering that element's effect.
        /// Everything happens inside one dispatcher callback, so composition never sees the gap.
        /// </summary>
        private static List<(FrameworkElement Element, object LocalValue)> StripEffects(
            List<KeyValuePair<FrameworkElement, Effect>> effects)
        {
            var stripped = new List<(FrameworkElement, object)>(effects.Count);
            foreach (var pair in effects)
            {
                var local = pair.Key.ReadLocalValue(UIElement.EffectProperty);
                if (local != DependencyProperty.UnsetValue && !(local is Effect))
                {
                    continue;
                }

                stripped.Add((pair.Key, local));
                pair.Key.Effect = null;
            }

            return stripped;
        }

        private static void RestoreEffects(List<(FrameworkElement Element, object LocalValue)> stripped)
        {
            foreach (var (element, local) in stripped)
            {
                if (local == DependencyProperty.UnsetValue)
                {
                    element.ClearValue(UIElement.EffectProperty);
                }
                else
                {
                    element.SetValue(UIElement.EffectProperty, local);
                }
            }
        }

        private bool TryRenderToastItemBytes(
            Window window, FrameworkElement container,
            CardRenderScratch scratch, Func<int, byte[]> takeBuffer, bool applyHostOpacity,
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

                // Opacity animated on the slide host (a theme may fade the notification in or out
                // instead of sliding it) lives above the card, so rendering the card alone would miss
                // it and the clip would show an opaque card while the screen showed a fade. Folded in
                // here as a draw-time push, which the rasteriser applies for free — rather than as a
                // second pass over the pixel buffer.
                var hostOpacity = applyHostOpacity ? _activeSlideHost?.Opacity ?? 1d : 1d;
                var fading = hostOpacity < 1d;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    if (fading)
                    {
                        dc.PushOpacity(Math.Max(0d, hostOpacity));
                    }

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

                    if (fading)
                    {
                        dc.Pop();
                    }
                }

                // The physical/local DPI ratio carries both the LayoutTransform scale and the
                // window's physical render scale in one factor.
                var dpiX = 96.0 * pw / local.Width;
                var dpiY = 96.0 * ph / local.Height;
                System.Windows.Media.Imaging.RenderTargetBitmap rtb;
                if (scratch != null && scratch.Rtb != null &&
                    scratch.PixelW == pw && scratch.PixelH == ph &&
                    scratch.DpiX == dpiX && scratch.DpiY == dpiY)
                {
                    rtb = scratch.Rtb;
                    rtb.Clear();
                    rtb.Render(visual);
                }
                else
                {
                    rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        pw, ph, dpiX, dpiY, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    if (scratch != null)
                    {
                        // Not frozen: the sampler re-renders this surface next tick. CopyPixels below
                        // is an immediate same-thread read, so nothing outlives the mutation.
                        scratch.Rtb = rtb;
                        scratch.PixelW = pw;
                        scratch.PixelH = ph;
                        scratch.DpiX = dpiX;
                        scratch.DpiY = dpiY;
                    }
                    else
                    {
                        rtb.Freeze();
                    }
                }

                var stride = pw * 4;
                var buffer = takeBuffer(stride * ph);
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
            if (!TryRenderToastItemBytes(
                    window, container, scratch: null, len => new byte[len], applyHostOpacity: true,
                    out var pixels, out var pw, out var ph))
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
            // The card is wrapped in a slide host, so window.Content is no longer the ItemsControl.
            // Keep sampling the surface itself; otherwise every tick silently produces no track.
            itemsControl = _activeCardSurface;
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
        /// per item, the card's rendered pixels plus the slide transform's current value in
        /// physical pixels. The composited position is synthesized at export as the lone-toast
        /// corner plus that slide offset — measured screen geometry never reaches the track, so
        /// live window moves and stacking cannot reach the clip. The per-item tracks are re-timed
        /// into each achievement's unlock clip at export (WGC's per-window video capture can't see
        /// the separate toast window). Called by the caller once per recording frame, with that
        /// frame's composition time; a no-op when not a game anchor. The window may never be
        /// revealed — rendering a card reads layout, not visibility. UI thread only.
        /// </summary>
        private void SampleWaveTracks(
            ToastOverlayTrackRecorder recorder, Window window,
            IReadOnlyList<AchievementToastViewModel> toastItems, double elapsedMs, int tickIndex)
        {
            if (recorder == null ||
                !TryGetTrackGeometry(window, out var itemsControl, out var clientPhys, out var windowPhys,
                    out var pxPerDipX, out var pxPerDipY))
            {
                return;
            }

            // The slide transform lives on the slide host, outside the surface's LayoutTransform,
            // so its value is plain window DIPs; the window's own DIP-to-physical ratio converts
            // it. One read covers every card — the whole surface slides as one.
            var slideXPhys = (_activeSlideTransform?.X ?? 0d) * pxPerDipX;
            var slideYPhys = (_activeSlideTransform?.Y ?? 0d) * pxPerDipY;
            var hostOpacity = Math.Max(0d, Math.Min(1d, _activeSlideHost?.Opacity ?? 1d));

            for (var i = 0; i < toastItems.Count; i++)
            {
                var vm = toastItems[i];
                var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null)
                {
                    continue;
                }

                var scratch = GetCardScratch(vm);
                var glowScale = ComputeGlowScale(scratch, hostOpacity);

                // A stacked wave staggers rasterization — one card's pixels per tick — so the
                // per-tick UI cost stays a single card render. Positions and glow scale are
                // recorded for every card every tick regardless (they are what the export
                // interpolates), so only pixel freshness divides by the card count. The same
                // repeat path covers the worker's backlog refusal.
                var rendersThisTick = toastItems.Count == 1 || tickIndex % toastItems.Count == i;
                if (!rendersThisTick || !recorder.CanAcceptFrame(vm))
                {
                    recorder.Sample(
                        vm, null, 0, 0, slideXPhys, slideYPhys, glowScale,
                        clientPhys.Width, clientPhys.Height, elapsedMs);
                    continue;
                }

                // Effects are detached for the render: the software blur they cost per tick is the
                // dominant sampler expense (measured ~6x the whole rest of the card), and their
                // halo is static apart from opacity — captured once as the shadow layer and
                // re-applied at export as layer x glowScale.
                var effects = new List<KeyValuePair<FrameworkElement, Effect>>();
                CollectEffects(container, effects);
                var stripped = StripEffects(effects);
                byte[] pixels;
                int pw, ph;
                bool rendered;
                try
                {
                    rendered = TryRenderToastItemBytes(
                        window, container, scratch, len => recorder.RentBuffer(vm, len),
                        applyHostOpacity: true, out pixels, out pw, out ph);
                }
                finally
                {
                    RestoreEffects(stripped);
                }

                if (!rendered)
                {
                    continue;
                }

                // (Re)capture the halo when its inputs changed: the card's pixel size, or the set
                // of effect instances (a trigger swapping the neutral shadow for the rarity glow).
                if (effects.Count > 0 &&
                    (pw != scratch.ShadowW || ph != scratch.ShadowH ||
                     !SameEffectSignature(scratch.ShadowEffectSignature, effects)))
                {
                    CaptureShadowLayer(recorder, window, container, vm, scratch, effects);
                }

                recorder.Sample(
                    vm, pixels, pw, ph, slideXPhys, slideYPhys, glowScale,
                    clientPhys.Width, clientPhys.Height, elapsedMs);
            }
        }

        private CardRenderScratch GetCardScratch(AchievementToastViewModel vm)
        {
            var scratchByVm = _trackRenderScratch;
            if (scratchByVm == null)
            {
                return new CardRenderScratch();
            }

            if (!scratchByVm.TryGetValue(vm, out var scratch))
            {
                scratch = new CardRenderScratch();
                scratchByVm[vm] = scratch;
            }

            return scratch;
        }

        /// <summary>
        /// The shadow-layer multiplier for this tick: the glow effect's current animated opacity
        /// relative to the opacity the layer was captured at, times the slide host's opacity (the
        /// halo must fade with a fade theme even though the card pixels carry that fade already).
        /// </summary>
        private static double ComputeGlowScale(CardRenderScratch scratch, double hostOpacity)
        {
            if (scratch.GlowEffect != null && scratch.GlowRefOpacity > 0.001)
            {
                return hostOpacity * Math.Max(0d, scratch.GlowEffect.Opacity) / scratch.GlowRefOpacity;
            }

            return hostOpacity;
        }

        private static bool SameEffectSignature(
            List<Effect> signature, List<KeyValuePair<FrameworkElement, Effect>> effects)
        {
            if (signature == null || signature.Count != effects.Count)
            {
                return false;
            }

            for (var i = 0; i < effects.Count; i++)
            {
                if (!ReferenceEquals(signature[i], effects[i].Value))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Captures every card's shadow layer before the wave's slide starts, so the one software
        /// blur rasterization each card pays lands outside the slide's clock. Cards without
        /// effects record nothing (their glow scale degenerates to the host opacity).
        /// </summary>
        private void CaptureWaveShadowLayers(
            ToastOverlayTrackRecorder recorder, Window window,
            IReadOnlyList<AchievementToastViewModel> toastItems)
        {
            if (recorder == null ||
                !TryGetTrackGeometry(window, out var itemsControl, out _, out _, out _, out _))
            {
                return;
            }

            for (var i = 0; i < toastItems.Count; i++)
            {
                var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (container == null)
                {
                    continue;
                }

                var effects = new List<KeyValuePair<FrameworkElement, Effect>>();
                CollectEffects(container, effects);
                if (effects.Count > 0)
                {
                    CaptureShadowLayer(
                        recorder, window, container, toastItems[i], GetCardScratch(toastItems[i]), effects);
                }
            }
        }

        /// <summary>
        /// Captures one card's shadow/glow halo as a difference layer: the card rendered with its
        /// effects minus the card rendered without them, both in this same dispatcher callback so
        /// the content (GIF frame, countdown) is identical in the pair. Host opacity is excluded
        /// from both renders — the per-sample glow scale carries it instead, so a fade theme's
        /// mid-fade capture doesn't bake a dimmed halo. This pays the software blur exactly once
        /// per capture; every subsequent tick renders effect-free.
        /// </summary>
        private void CaptureShadowLayer(
            ToastOverlayTrackRecorder recorder, Window window, FrameworkElement container,
            AchievementToastViewModel vm, CardRenderScratch scratch,
            List<KeyValuePair<FrameworkElement, Effect>> effects)
        {
            if (!TryRenderToastItemBytes(
                    window, container, scratch: null, len => new byte[len], applyHostOpacity: false,
                    out var withEffects, out var pw1, out var ph1))
            {
                return;
            }

            var stripped = StripEffects(effects);
            byte[] withoutEffects;
            int pw0, ph0;
            bool rendered;
            try
            {
                rendered = TryRenderToastItemBytes(
                    window, container, scratch: null, len => new byte[len], applyHostOpacity: false,
                    out withoutEffects, out pw0, out ph0);
            }
            finally
            {
                RestoreEffects(stripped);
            }

            if (!rendered || pw0 != pw1 || ph0 != ph1)
            {
                return;
            }

            // With-effects minus without, in place: non-negative in premultiplied space (an
            // effect's shadow only ever adds under the content), clamped against rounding.
            for (var i = 0; i < withEffects.Length; i++)
            {
                var delta = withEffects[i] - withoutEffects[i];
                withEffects[i] = delta > 0 ? (byte)delta : (byte)0;
            }

            recorder.SetShadowLayer(vm, withEffects, pw1, ph1);
            scratch.ShadowW = pw1;
            scratch.ShadowH = ph1;
            var signature = new List<Effect>(effects.Count);
            foreach (var pair in effects)
            {
                signature.Add(pair.Value);
            }

            scratch.ShadowEffectSignature = signature;

            // The pulse animates a DropShadowEffect's opacity; the first one found is the scale
            // driver. Its opacity right now is what the layer baked, so it is the reference.
            scratch.GlowEffect = null;
            scratch.GlowRefOpacity = 1.0;
            foreach (var pair in effects)
            {
                if (pair.Value is DropShadowEffect dropShadow)
                {
                    scratch.GlowEffect = dropShadow;
                    scratch.GlowRefOpacity = Math.Max(0.05, dropShadow.Opacity);
                    break;
                }
            }
        }

        /// <summary>
        /// Measures where a single-card wave's card settled against the corner the placement math
        /// says it belongs on, feeding <see cref="WarnOnSettledCardDrift"/>. Purely diagnostic:
        /// the clip's composited position is synthesized at export and never reads this. Called
        /// once at the placement snap, when layout and position are final. UI thread only.
        /// </summary>
        private void ReportSettledCornerDrift(
            Window window, IReadOnlyList<AchievementToastViewModel> toastItems)
        {
            if (toastItems == null || toastItems.Count != 1 ||
                !TryGetTrackGeometry(window, out var itemsControl, out var clientPhys, out var windowPhys,
                    out var pxPerDipX, out var pxPerDipY))
            {
                return;
            }

            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(toastItems[0]) as FrameworkElement;
            if (container == null || container.RenderSize.Width <= 0 || container.RenderSize.Height <= 0)
            {
                return;
            }

            var bounds = container.TransformToAncestor(window)
                .TransformBounds(new Rect(container.RenderSize));
            var physW = Math.Max(1, (int)Math.Ceiling(bounds.Width * pxPerDipX));
            var physH = Math.Max(1, (int)Math.Ceiling(bounds.Height * pxPerDipY));
            var settledRelX = windowPhys.X + (int)Math.Round(bounds.X * pxPerDipX) - clientPhys.X;
            var settledRelY = windowPhys.Y + (int)Math.Round(bounds.Y * pxPerDipY) - clientPhys.Y;

            ToastWindowPlacer.ComputeCorner(
                clientPhys, physW, physH, _activeMonitorScale,
                AlignRight(), AlignBottom(), EffectiveGapDip(), out var cornerX, out var cornerY);
            WarnOnSettledCardDrift(toastItems.Count, cornerX - clientPhys.X, cornerY - clientPhys.Y,
                settledRelX, settledRelY);
        }

        /// <summary>
        /// Warns when a lone card did not settle where the corner math says it belongs.
        ///
        /// The toast window is larger than the card — it reserves the slide's travel — so the card's
        /// resting position is the window's position plus a measured offset rather than the window's
        /// position itself. If those two measurements ever disagree, the card lands off the corner, and
        /// on the clip side that is invisible: the composited position is synthesized from the same
        /// corner math, so the clip looks fine while the on-screen card is wrong. This makes it a log
        /// line instead.
        ///
        /// Only for a single-card wave. A stacked wave's cards are legitimately away from the corner,
        /// so the difference carries no information there.
        /// </summary>
        private void WarnOnSettledCardDrift(int cardCount, int cornerRelX, int cornerRelY, int settledRelX, int settledRelY)
        {
            if (cardCount != 1 || _placementDriftLogged)
            {
                return;
            }

            var dx = cornerRelX - settledRelX;
            var dy = cornerRelY - settledRelY;
            if (Math.Abs(dx) <= ToastWindowPlacer.PlacementTolerancePx &&
                Math.Abs(dy) <= ToastWindowPlacer.PlacementTolerancePx)
            {
                return;
            }

            _placementDriftLogged = true;
            _logger?.Warn(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[Toast] Settled card is {0},{1}px off its corner (settled {2},{3}; corner {4},{5}). The " +
                "window's slide-travel reservation and the measured card offset disagree.",
                dx, dy, settledRelX, settledRelY, cornerRelX, cornerRelY));
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
            var now = CaptureTimelineClock.UtcNow;
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
                $"[Toast] Releasing hold after {(CaptureTimelineClock.UtcNow - _holdStartedUtc.Value).TotalSeconds:F1}s; displaying {waveCount} notification(s).");
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
                   items[end].IsGameCompleted == anchor.IsGameCompleted &&
                   ShouldToast(items[end].IsPreview, items[end].IsFriendUnlock, items[end].ProviderKey) ==
                       ShouldToast(anchor.IsPreview, anchor.IsFriendUnlock, anchor.ProviderKey))
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
                .Where(vm => ShouldToast(vm.IsPreview, vm.IsFriendUnlock, vm.ProviderKey))
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
            // Resolve the corner once for this wave: a theme override wins, otherwise the plugin
            // setting. Positioning (including the per-frame game-window follow) and slide direction
            // both read the resolved value.
            _activePosition = EffectivePosition();
            // Same reason, and the reason it is here rather than at the slides: resolving the themeable
            // slide storyboards reaches the filesystem and the resource dictionaries, and doing that
            // inside SlideInPhysical/SlideOutPhysical put it on the UI thread on the very frame the
            // slide began. Called unconditionally so all four fields are always this wave's, never a
            // previous wave's. Only the storyboards' shape is resolved here; each is bound to this
            // wave's slide host at the slide itself, since the window does not exist yet.
            ResolveWaveSlideTiming();
            _activeCardGlow = wave[0].ToastGlowMargin.Top;
            // Placement state is per-wave: the correction is measured on this wave's first settled
            // placement, and the anomaly warning is emitted at most once for it.
            _placementCorrection = default(ToastWindowPlacer.PlacementCorrection);
            _placementAnomalyLogged = false;
            _placementDriftLogged = false;
            var waveGameId = wave[0].PlayniteGameId;
            _activeWaveGameId = waveGameId != Guid.Empty ? waveGameId : (Guid?)null;

            var wavePlan = ResolveWavePlan(wave);
            var cardItems = wavePlan.CardItems;
            var visible = wavePlan.IsVisible;
            var plan = wavePlan.Screenshots;

            // The base capture must precede window.Show(); overlapping it with the sound-align
            // delay below adds no latency to the toast itself. It feeds every variant: clean saves
            // it as-is, framed composites the frame onto it, and with-notification composites each
            // item's rendered card onto a copy of it.
            Task<System.Drawing.Bitmap> baseCaptureTask = null;
            if (plan != null)
            {
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

            // Everything the card needs that costs nothing on screen is built here, ahead of the chime
            // and its alignment delay: the template instantiation, the surface, and the icon decodes and
            // ray traces the card would otherwise finish while it is already sliding. None of it touches
            // an HWND or a pixel, so the base capture above still completes against a screen with no
            // toast on it, and the window is still created and shown at the same point as before.
            //
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
            var template = ToastSurfaceFactory.ResolveToastTemplate(
                _templateResolver, cardItems, ToastThemeStylingEnabled, waveProviderKey, waveScopeGameId);
            var items = ToastSurfaceFactory.BuildToastSurface(cardItems, template);

            LogWaveDiagnostics(cardItems, template, wavePlan.Mode);

            PrimeWaveVisuals(cardItems);

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

            // Counts the window-bearing waves this process has shown, so a diagnostic line says whether
            // it came from the session's first toast (which pays every one-time cost) or a later one.
            var waveSequence = ++_waveSequence;

            var window = PlayniteUiProvider.CreateBorderlessTopmostWindow(
                _api,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            _activeWindow = window;

            // The card surface goes inside a slide host: the window stays put and the host translates,
            // so the slide never issues a window move and never leaves the window's own DIP space. The
            // travel room the host needs is reserved once the card has a laid-out size (ApplySlideTravel
            // below, after the DPI compensation settles).
            var slideHost = ToastSurfaceFactory.BuildSlideHost(items, out var slideTransform);
            _activeCardSurface = items;
            _activeSlideHost = slideHost;
            _activeSlideTransform = slideTransform;
            window.Content = slideHost;

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
            Stopwatch trackRenderWatch = null;
            var trackRenderMaxMs = 0d;
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
                    $"[Toast] Fire: wave={waveSequence}, monitorScale={_activeMonitorScale:0.###}, " +
                    $"systemScale={systemScale:0.###}, perMonitorWindow={needsPerMonitorWindow}, " +
                    $"isGame={_activeIsGame}, revealed={visible}, mode={wavePlan.Mode}, " +
                    $"testFire={waveIsTestFire}, preview={previewSource.HasValue}, cards={cardItems.Count}, " +
                    $"shots={plan != null}, recordings={_settings?.Persisted?.EnableUnlockRecordings ?? false}");
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

                // Reserve the slide's travel now that the card has its final laid-out size, so the
                // window is large enough to hold the card at both ends of the slide. Placed between the
                // compensation and the settled placement because it changes the window's size, and the
                // placement below is what puts the (now larger) window where the card lands on the
                // corner.
                ReserveSlideTravel(window, items);
                PlaceWindow(window, "shown");

                // Let the card actually reach the screen before the slide starts timing itself.
                // ApplyDpiCompensation's UpdateLayout is measure/arrange, not pixels; on a monitor at
                // the system scale the settle loop above does not await at all, so without this the
                // slide's first frame is also the toast's first frame — the one paying for the layered
                // window's surface, the template's visuals, text realization and the shadow effects.
                // The slide reads progress from frame timestamps, so that cost does not slow the slide,
                // it skips it: the second frame reports a clock that has already run most of the
                // duration and the card jumps. Two composed frames put that work before the clock
                // starts. Bounded like the settle loop above, and it runs for an unrevealed wave too,
                // whose recorded track would otherwise carry the same jump.
                var warmFrames = await WaitForComposedFramesAsync(WarmFrameCount, WarmFrameTimeoutMs)
                    .ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                // Only the shortfall is logged, so no line means the toast did get its frames — which is
                // what makes the slide line below readable: a large first-frame gap under a silent warm
                // is a cost the warm frames failed to absorb, not a warm that never ran.
                if (warmFrames < WarmFrameCount)
                {
                    _logger?.Info($"[Toast] Warm: frames={warmFrames}/{WarmFrameCount}, timedOut=true");
                }

                // Recording setup runs before the slide so its one expensive render — the shadow
                // layer capture, which rasterizes the effects' software blur once per card — lands
                // before the slide clock starts instead of eating the slide's first frames. Game
                // anchor only — a test fire out of game has no video — and only with recordings
                // enabled, since nothing else consumes a track.
                if (_activeIsGame && _activeReferenceHwnd != IntPtr.Zero &&
                    (_settings?.Persisted?.EnableUnlockRecordings ?? false))
                {
                    trackRecorder = new ToastOverlayTrackRecorder(
                        _logger, TrackSampleIntervalMs(),
                        AlignRight(), AlignBottom(), EffectiveGapDip(), _activeMonitorScale);
                    _trackRenderScratch = new Dictionary<AchievementToastViewModel, CardRenderScratch>();
                    trackSampleCount = 0;
                    CaptureWaveShadowLayers(trackRecorder, window, cardItems);
                }

                SlideInPhysical(window, reveal: visible);

                // Start sampling each card's overlay track now, at the slide-in — revealed or not
                // — so the slide-in animation lands in the tracks (not just the settled toast).
                // Tracks are sampled at the
                // recording frame rate and re-timed into each achievement's clip at export. Independent
                // of the placement hooks below (sampling only reads, never moves the window).
                if (trackRecorder != null)
                {
                    var sampleIntervalMs = TrackSampleIntervalMs();
                    // The unconditional resample also carries animation frames into the tracks — a
                    // GIF advances on whatever per-frame delays its own file declares — so do not
                    // reduce this to sample-on-position-change.
                    var recorder = trackRecorder;
                    // Sampling is due every recording frame, but can only happen on a composed frame, so
                    // take the tick nearest each due instant: comparing against the due time less half a
                    // monitor frame is what stops a recording rate at or near the refresh rate from
                    // aliasing. When the two rates match, every tick is a sample; when the recording rate
                    // is the lower one, ticks are skipped evenly.
                    var dueTolerance = MonitorFramePeriodMs() / 2d;
                    // The first sample rides the first composed frame rather than running here
                    // synchronously: the slide storyboard has just begun, and its animated value is
                    // not applied until that frame, so a synchronous read would record the seeded
                    // rest value — one frame of the card sitting at its corner before the slide.
                    var nextDueMs = 0d;
                    trackTicks = new RenderTickCounter();
                    var counter = trackTicks;
                    var renderWatch = new Stopwatch();
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
                            // Accumulated, not per-call: the whole-wave total and worst tick are
                            // what the sampling summary reports at wave end.
                            var before = renderWatch.Elapsed.TotalMilliseconds;
                            renderWatch.Start();
                            SampleWaveTracks(recorder, window, cardItems, elapsedMs, trackSampleCount);
                            renderWatch.Stop();
                            var tickMs = renderWatch.Elapsed.TotalMilliseconds - before;
                            if (tickMs > trackRenderMaxMs)
                            {
                                trackRenderMaxMs = tickMs;
                            }
                        }
                        catch
                        {
                            // Ignore transient render/placement failures (e.g. window closing).
                        }
                    };
                    trackRenderWatch = renderWatch;
                    CompositionTarget.Rendering += onTrackSample;

                    // The ray-burst glow invalidates at its own fixed default rate; sampling above it
                    // stores duplicate ray frames with beat-dependent phase, which plays back as
                    // judder. Raise the driver to the sampling rate for the recording's span.
                    RayAnimationDriver.SetSamplingFps(1000.0 / sampleIntervalMs);
                }

                // Let the cards finish sliding in and paint so each renders at its final laid-out
                // size (achievement icons and badge images load asynchronously), then snap,
                // composite the with-notification shots, and hold for the remaining display time.
                // The base capture itself already ran, before the window existed. At least the
                // resolved slide-in duration plus a settle margin: a theme may author a slide
                // longer than the base delay, and the snap's StopActiveSlide would cut it
                // mid-flight — on screen and in the recorded track alike.
                var captureDelayMs = Math.Max(
                    300, (int)Math.Round(_activeSlideInMs) + (2 * SlideSettleBufferMs));
                await Task.Delay(captureDelayMs).ConfigureAwait(true);
                if (_disposed)
                {
                    return;
                }

                // Stop the slide so placement can move the window directly, and snap to the anchor
                // corner now that the toast is fully laid out.
                StopActiveSlide();
                PlaceWindow(window, "snap");
                ReportSettledCard(window);

                // The wave has settled, revealed or not: signal the recording service (a liveness
                // bump for its track wait, plus this wave's chime time for the clip audio mix —
                // clip windows themselves are unlock-anchored). A unrevealed wave passes a null
                // chime time, so its clips are mixed without one.
                RaiseWaveDisplayed(cardItems, soundPlayedUtc);

                // Layout and placement are final: verify a lone card actually settled on its corner.
                ReportSettledCornerDrift(window, cardItems);

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

                var durationMs = EffectiveDurationSeconds() * 1000;
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
                    RayAnimationDriver.ClearSamplingFps();
                }

                // Unconditional (unlike the PerfScope-gated cadence line): the card render is the
                // sampler's whole per-tick cost, and an average near or past the sample interval is
                // the UI-thread stall that turns a 60 fps request into a lower effective rate.
                if (trackRecorder != null && trackSampleCount > 0 && trackRenderWatch != null)
                {
                    _logger?.Info(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "[Recording] Toast sampling: {0} samples, card render avg {1:0.0} ms, max {2:0.0} ms " +
                        "(sample interval {3:0.0} ms)",
                        trackSampleCount,
                        trackRenderWatch.Elapsed.TotalMilliseconds / trackSampleCount,
                        trackRenderMaxMs,
                        TrackSampleIntervalMs()));
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

                // Unconditional: waves are sequential, so this only ever clears this wave's rate.
                RayAnimationDriver.ClearSamplingFps();

                // Finalize and hand the recorded card tracks to the recording service. The raw
                // pixels are already captured, so this safely outlives window.Close() below.
                _ = CompleteAndRaiseTracksAsync(trackRecorder);

                StopActiveSlide();
                _activeCardSurface = null;
                _trackRenderScratch = null;
                _activeSlideHost = null;
                _activeSlideTransform = null;
                _activeReferenceHwnd = IntPtr.Zero;
                _activeIsGame = false;
                _activeSuppressZOrder = false;
                _activeMonitorScale = 1.0;

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

        /// <summary>
        /// Fires a single UniPlaySong sound for the wave, using the highest-ranked tier present so
        /// a burst of unlocks does not stack overlapping sounds. UniPlaySong owns enablement and
        /// audio selection for the "playniteachievements/&lt;tier&gt;" URI; if it is not installed
        /// the URI is unhandled and the call is ignored. Returns the launch moment (null when no
        /// sound fired) so the recording service can locate the chime in its sidecar audio track.
        /// </summary>
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
                return CaptureTimelineClock.UtcNow;
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

            // A non-null plan always needs the base capture: BuildScreenshotPlan returns a plan
            // only when at least one item requests at least one variant, and every variant is
            // derived from the single base capture.
            public bool NeedsToastComposite => Items.Any(i =>
                (i.Variants & ScreenshotVariants.WithToast) != 0);

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
            var baseDir = persisted?.UnlockScreenshotDirectory;
            if (persisted?.EnableUnlockScreenshots != true || string.IsNullOrWhiteSpace(baseDir))
            {
                return null;
            }

            // A manual test fire lands in a separate "Test" subfolder so it never mixes with a
            // game's genuine unlock captures.
            if (first.IsTestFire)
            {
                baseDir = System.IO.Path.Combine(baseDir, UnlockScreenshotService.TestFolderName);
            }

            var plan = new WaveScreenshotPlan
            {
                BaseDirectory = baseDir,
                CleanSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixClean),
                WithToastSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixWithToast),
                FramedSuffix = NormalizeSuffix(persisted.UnlockScreenshotSuffixFramed),
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

                            // The frame renders synchronously into a bitmap, so the ray burst inside it
                            // can only read a track that is already cached. Warm it here, at the one
                            // seam in this path that can await, and cap the wait so a slow fetch costs
                            // the burst its silhouette rather than costing the capture its frame.
                            await WarmRayTrackAsync(item.Vm.IconPath);
                            if (_disposed)
                            {
                                break;
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
                                _screenshotService.Save(
                                    clean, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                    vm.AchievementNumber, vm.TotalCount,
                                    plan.CleanSuffix);
                            }

                            if ((item.Variants & ScreenshotVariants.WithToast) != 0 &&
                                toasts != null && toasts.TryGetValue(vm, out var toastShot) && toastShot != null)
                            {
                                _screenshotService.Save(
                                    toastShot, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                    vm.AchievementNumber, vm.TotalCount,
                                    plan.WithToastSuffix);
                            }

                            if (framedByVm.TryGetValue(vm, out var framed))
                            {
                                _screenshotService.Save(
                                    framed, baseDir, vm.ProviderKey, vm.GameName, vm.AchievementName,
                                    vm.AchievementNumber, vm.TotalCount,
                                    plan.FramedSuffix);
                            }
                        }

                        // Drop cached capture scans for the games in this wave. This also raises
                        // CapturesChanged, so an already-open grid lights up its Captures button.
                        foreach (var gameName in items
                            .Select(i => i.Vm?.GameName)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?.Invalidate(gameName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "Saving unlock screenshot files failed.");
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
            captureTask?.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    t.Result?.Dispose();
                }
                else
                {
                    // Observe capture faults even when a queued wave is cleared before awaiting it.
                    _ = t.Exception;
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
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
            return CornerGapDip - _activeCardGlow;
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
            var placed = ToastWindowPlacer.PositionPhysical(
                window, _activeCardSurface, SlideOffsetDipX(), SlideOffsetDipY(),
                anchorPhys, renderScale, _activeMonitorScale, AlignRight(), AlignBottom(), EffectiveGapDip(),
                measure, ref _placementCorrection, out outcome);
            LogPlacementAnomaly(window, anchorPhys, renderScale, outcome);

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

        // The slide transform's current value, removed from any card measurement so placement always
        // works from the card's resting offset. Reading the transform rather than tracking a flag keeps
        // this correct no matter which pass a placement lands in.
        private double SlideOffsetDipX()
        {
            return _activeSlideTransform?.X ?? 0d;
        }

        private double SlideOffsetDipY()
        {
            return _activeSlideTransform?.Y ?? 0d;
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

        /// <summary>
        /// What the slide animates: the translate in the slide host's transform group. Everything is
        /// aimed at the host — a real <c>UIElement</c> — so one target object serves the slide, an
        /// opacity fade, and the group's scale at index 0, and a theme can animate any combination.
        /// </summary>
        private const string SlideTargetPath =
            "(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)";

        /// <summary>
        /// What the bundled storyboards used to declare. It never animated anything — the slide read
        /// only the easing and duration off them — so a theme carrying it means "the plugin's slide,
        /// my timing" and is retargeted rather than rejected.
        /// </summary>
        private const string LegacySlideTargetPath = "(Window.Top)";

        /// <summary>
        /// The slide's target property, built with its dependency properties supplied directly rather
        /// than parsed from <see cref="SlideTargetPath"/>.
        ///
        /// This must not be recognised or constructed by string. XAML does not keep the text a
        /// storyboard was authored with: it normalises <c>Storyboard.TargetProperty</c> to indexed
        /// placeholders — <c>(0).(1)[1].(2)</c> — and puts the resolved properties in
        /// <c>PathParameters</c>. So comparing <c>PropertyPath.Path</c> to the authored spelling never
        /// matches for a themed or bundled storyboard, which is precisely how the slide shipped
        /// animating nothing: unrecognised, it got no From/To, and a DoubleAnimation without either
        /// animates a property from its own value to its own value. That neither moves nor throws.
        /// </summary>
        private static PropertyPath BuildSlidePath()
        {
            return new PropertyPath(
                "(0).(1)[1].(2)",
                UIElement.RenderTransformProperty,
                TransformGroup.ChildrenProperty,
                TranslateTransform.YProperty);
        }

        /// <summary>
        /// Whether a storyboard child is the one that moves the card, however it was spelled.
        ///
        /// Decided on the property the path actually resolves to — the last entry of
        /// <c>PathParameters</c> — so every spelling of the translate's Y is recognised: the bundled
        /// indexed form, the un-indexed form a theme author would naturally reach for, and a
        /// code-built path. An unset target property means the slide (a theme contributing only timing),
        /// as does the legacy <c>(Window.Top)</c>, which never animated anything.
        /// </summary>
        private static bool AnimatesSlide(Timeline child)
        {
            var path = Storyboard.GetTargetProperty(child);
            if (path == null || string.IsNullOrEmpty(path.Path) || path.Path == LegacySlideTargetPath)
            {
                return true;
            }

            var parameters = path.PathParameters;
            if (parameters != null && parameters.Count > 0)
            {
                return parameters[parameters.Count - 1] == TranslateTransform.YProperty;
            }

            return path.Path == SlideTargetPath;
        }
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
        // Composed frames of the invisible toast to wait for before starting the slide, and the ceiling
        // on that wait. Two, because the first frame after Show is the one that realizes the card, and a
        // second proves that frame was presented rather than merely queued.
        //
        // The ceiling is a stall guard, not the expected path: subscribing to Rendering itself keeps the
        // render loop ticking, so the frames arrive on their own even with nothing on the card animating.
        // Measured with tools\capture-harness SlideProbe on a static Opacity=0 window, this wait costs
        // 6-25 ms and never reaches the ceiling. Do not shorten it toward a frame period: its other
        // effect is bounding how much first-paint work the warm can absorb, so a tight ceiling would cut
        // an expensive first paint short and hand the remainder back to the slide's first frame, which is
        // the whole defect. It only costs latency in a pathological case where frames stop entirely.
        private const int WarmFrameCount = 2;
        private const int WarmFrameTimeoutMs = 150;
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

            /// <summary>Time from the first observed frame to the last (ms); 0 below two frames.</summary>
            public double SpanMs => Frames > 1 ? _lastMs - _firstMs : 0d;

            /// <summary>
            /// Interval between the first two observed frames (ms); 0 below two frames. For motion
            /// driven off frame timestamps this is the interval that shows as a jump rather than as
            /// slowness: whatever the first frame had to rasterize is charged entirely to it, and the
            /// eased progress the second frame reports has already skipped that far ahead.
            /// </summary>
            public double FirstIntervalMs { get; private set; }

            /// <summary>Largest interval between consecutive observed frames (ms); 0 below two frames.</summary>
            public double MaxIntervalMs { get; private set; }

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
                else
                {
                    var intervalMs = nowMs - _lastMs;
                    if (Frames == 1)
                    {
                        FirstIntervalMs = intervalMs;
                    }

                    if (intervalMs > MaxIntervalMs)
                    {
                        MaxIntervalMs = intervalMs;
                    }
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

            // The window is already at the resting corner and stays there for the whole slide; only the
            // card moves. Place once here so the slide starts from a settled position.
            PlaceWindow(window);

            var distance = SlideDistanceDip(window);
            var from = SlideFromBottom() ? distance : -distance;
            RunSlideStoryboard(
                _activeSlideInStoryboard, from, 0d, DefaultSlideInEase, _activeSlideInMs,
                _activeSlideInTravels, "in");
        }

        // Returns the slide-out duration (ms) so the caller waits exactly that long; 0 if it didn't run.
        private double SlideOutPhysical(Window window)
        {
            if (window == null)
            {
                return 0;
            }

            var distance = SlideDistanceDip(window);
            var to = SlideFromBottom() ? distance : -distance;
            RunSlideStoryboard(
                _activeSlideOutStoryboard, 0d, to, DefaultSlideOutEase, _activeSlideOutMs,
                _activeSlideOutTravels, "out");
            return _activeSlideOutMs;
        }

        /// <summary>
        /// Waits for <paramref name="frames"/> distinct composed frames, or <paramref name="timeoutMs"/>,
        /// whichever comes first; returns the frames actually observed.
        ///
        /// Counting distinct frames is the point. WPF raises <c>Rendering</c> more than once for a single
        /// frame, so waiting for N events can return within one frame; <see cref="RenderTickCounter"/>
        /// already de-duplicates by composition timestamp. Nothing else here proves a frame was
        /// presented: <c>DispatcherPriority.Render</c> only orders against the queued render operation
        /// (and outranks <c>Loaded</c>, so it would run before the card's images have even been asked
        /// for), and <c>ContentRendered</c> fires once per window and already carries placement work.
        /// </summary>
        private static async Task<int> WaitForComposedFramesAsync(int frames, int timeoutMs)
        {
            if (frames <= 0)
            {
                return 0;
            }

            var ticks = new RenderTickCounter();
            // RunContinuationsAsynchronously because TrySetResult below runs inside a Rendering handler,
            // and a TaskCompletionSource otherwise completes its continuations synchronously on the
            // thread that set it — resuming the caller in the middle of a composition pass, where it
            // would move the window and subscribe the slide. ConfigureAwait(true) on a captured
            // dispatcher context already posts rather than inlines, so this guards the invariant rather
            // than fixing a live defect; it costs nothing and does not depend on that reasoning holding.
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler tick = null;
            tick = (s, e) =>
            {
                if (ticks.TryAdvance(e, out _) && ticks.Frames >= frames)
                {
                    reached.TrySetResult(true);
                }
            };

            CompositionTarget.Rendering += tick;
            try
            {
                await Task.WhenAny(reached.Task, Task.Delay(timeoutMs)).ConfigureAwait(true);
            }
            finally
            {
                CompositionTarget.Rendering -= tick;
            }

            return ticks.Frames;
        }

        /// <summary>
        /// Resolves both slides' easing and duration for the wave that is starting. Called once per
        /// wave, off the render loop; the slides themselves then only read the fields.
        ///
        /// The countdown bar deliberately keeps resolving its own storyboard when it starts, since it
        /// is nowhere near a slide — so a theme author editing timing still sees the countdown change
        /// immediately, and the slides on the next notification.
        /// </summary>
        private void ResolveWaveSlideTiming()
        {
            _activeSlideInStoryboard = ResolveSlideStoryboard(
                AchievementToastTemplateResolver.SlideInStoryboardKey, SlideInDurationMs,
                out _activeSlideInMs, out _activeSlideInTravels);
            _activeSlideOutStoryboard = ResolveSlideStoryboard(
                AchievementToastTemplateResolver.SlideOutStoryboardKey, SlideOutDurationMs,
                out _activeSlideOutMs, out _activeSlideOutTravels);
        }

        /// <summary>
        /// Clones the themeable slide storyboard and retargets it onto the slide transform, so a theme
        /// gets real animation control (keyframes, and animating opacity or a scale alongside the
        /// slide) rather than only contributing an easing and a duration.
        ///
        /// The retarget rules keep every previously-valid theme storyboard working. A child with no
        /// target name goes to the slide host; a child with no target property — or the legacy
        /// <c>(Window.Top)</c>, which never actually animated anything — is pointed at the transform's
        /// Y. Anything that names its own target is left exactly as authored. From/To are filled in by
        /// the caller, because the travel distance depends on the card's laid-out height and a theme
        /// cannot know it.
        ///
        /// Returns null (and the fallback duration) when nothing usable is defined, which is what makes
        /// the built-in animation the floor rather than a special case.
        /// </summary>
        private Storyboard ResolveSlideStoryboard(
            string storyboardKey, double fallbackMs, out double durationMs, out bool travels)
        {
            durationMs = fallbackMs;
            travels = true;
            try
            {
                var authored = _templateResolver?.ResolveStoryboard(storyboardKey);
                if (authored == null)
                {
                    return null;
                }

                // Only the property is rewritten here; the target object is bound at slide time, because
                // this runs once per wave before the wave's window and transform exist.
                var storyboard = authored.Clone();
                var resolved = 0d;
                var movesCard = false;
                foreach (var child in storyboard.Children)
                {
                    if (!IsUntargeted(child))
                    {
                        continue;
                    }

                    // Always overwrite the slide child's path with the plugin's own, whatever spelling
                    // it arrived in: the un-indexed form cannot resolve against the host's transform
                    // group, and the indexed form is only equivalent, never identical, to ours.
                    if (AnimatesSlide(child))
                    {
                        Storyboard.SetTargetProperty(child, BuildSlidePath());
                        movesCard = true;
                    }

                    if (child.Duration.HasTimeSpan)
                    {
                        resolved = Math.Max(resolved, child.Duration.TimeSpan.TotalMilliseconds);
                    }
                }

                // A storyboard with no finite duration would leave the wave waiting on a number it never
                // produced, and Forever would never settle the card. Fall back rather than run it —
                // which means the built-in slide, so `travels` stays true.
                if (resolved <= 0)
                {
                    return null;
                }

                travels = movesCard;
                durationMs = resolved;
                return storyboard;
            }
            catch (Exception ex)
            {
                // A theme can put anything in this resource; a broken one must cost the slide its
                // customisation, not the notification.
                _logger?.Debug(ex, $"Toast slide storyboard '{storyboardKey}' unusable; using the built-in slide.");
                travels = true;
                return null;
            }
        }

        /// <summary>
        /// Runs one slide: the card translates from <paramref name="fromDip"/> to
        /// <paramref name="toDip"/> inside the stationary window. Any prior slide is stopped first.
        ///
        /// This is a real WPF animation rather than a per-frame interpolation. It advances at whatever
        /// rate WPF composes at and at sub-pixel precision, where the previous per-frame
        /// <c>SetWindowPos</c> both cost a window move every frame and quantised to whole physical
        /// pixels. <see cref="_activeSlideTick"/> is attached purely to count frames for the diagnostic.
        /// </summary>
        private void RunSlideStoryboard(
            Storyboard authored, double fromDip, double toDip, IEasingFunction fallbackEase,
            double durationMs, bool travels, string label)
        {
            StopActiveSlide();
            var host = _activeSlideHost;
            var transform = _activeSlideTransform;
            if (host == null || transform == null)
            {
                return;
            }

            // Where the card belongs once this slide is over: the slide's end, or its resting corner when
            // the animation moves nothing positional (a theme fade or scale).
            //
            // This is seeded as the transform's LOCAL value before the storyboard starts, and the
            // animation then overrides it for its duration. Seeding the slide's *start* instead is
            // wrong in a way that only shows up later: an animation is an override, not an assignment,
            // so Stop/Remove reverts the property to whatever local value was underneath. With the
            // start seeded, the settled snap reverted the card to the slide's start — off in the
            // reserved travel room — and it vanished until the slide-out reseeded it.
            var restDip = travels ? toDip : 0d;

            _activeSlideLabel = label;
            _activeSlideRequestedMs = durationMs;
            if (durationMs <= 0)
            {
                transform.Y = restDip;
                // Reported too: a theme authoring a zero duration gets a snap rather than a slide,
                // which is worth seeing in the log instead of an absent line.
                _activeSlideTicks = new RenderTickCounter();
                ReportActiveSlide("instant");
                return;
            }

            var storyboard = BuildSlideStoryboard(authored, host, fromDip, toDip, fallbackEase, durationMs);
            if (storyboard == null)
            {
                transform.Y = restDip;
                _activeSlideTicks = new RenderTickCounter();
                ReportActiveSlide("instant");
                return;
            }

            // Counting only. The slide no longer needs a per-frame callback to move anything, but the
            // cadence it achieved is the number this change is judged on, so it is still measured.
            // How far the card actually moved, watched per frame. A storyboard that resolves to no
            // property animates nothing and does NOT throw, so without this a slide that never moved is
            // indistinguishable in the log from one that ran perfectly — which is exactly how a target
            // path that did not match the host's transform shape shipped twice.
            var ticks = new RenderTickCounter();
            var minY = double.MaxValue;
            var maxY = double.MinValue;
            _activeSlideMovedDip = 0d;
            EventHandler tick = (s, e) =>
            {
                if (!ticks.TryAdvance(e, out _))
                {
                    return;
                }

                var y = transform.Y;
                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }

                _activeSlideMovedDip = maxY - minY;
            };
            _activeSlideTicks = ticks;
            _activeSlideTick = tick;
            _runningSlideStoryboard = storyboard;

            transform.Y = restDip;
            CompositionTarget.Rendering += tick;
            try
            {
                storyboard.Begin(host, isControllable: true);
            }
            catch (Exception ex)
            {
                // Begin can throw on a theme storyboard that survived resolution but cannot bind to this
                // tree. Land the card where the slide would have left it rather than mid-travel.
                _logger?.Debug(ex, "Toast slide storyboard failed to start; snapping to the slide's end.");
                CompositionTarget.Rendering -= tick;
                _activeSlideTick = null;
                _runningSlideStoryboard = null;
                transform.Y = restDip;
                ReportActiveSlide("failed");
            }
        }

        /// <summary>
        /// The storyboard actually begun: the theme's, with the travel filled in where it left the
        /// endpoints open, or the built-in animation when no usable one was resolved.
        ///
        /// A theme child that declares neither From nor To gets this slide's endpoints — it cannot know
        /// the card's laid-out height, so leaving them open is how a theme says "the plugin's travel,
        /// my timing". One that declares them is left alone.
        /// </summary>
        private static Storyboard BuildSlideStoryboard(
            Storyboard authored, FrameworkElement host, double fromDip, double toDip,
            IEasingFunction fallbackEase, double durationMs)
        {
            if (authored != null)
            {
                var storyboard = authored.Clone();
                foreach (var child in storyboard.Children)
                {
                    if (!IsUntargeted(child))
                    {
                        continue;
                    }

                    // Everything untargeted animates the host, so a theme child animating opacity or a
                    // scale lands on a real UIElement; only the slide child was pointed at the
                    // transform's Y, via the property path.
                    Storyboard.SetTarget(child, host);
                    if (child is DoubleAnimation slide &&
                        !slide.From.HasValue && !slide.To.HasValue &&
                        AnimatesSlide(child))
                    {
                        slide.From = fromDip;
                        slide.To = toDip;
                    }
                }

                return storyboard;
            }

            var animation = new DoubleAnimation
            {
                From = fromDip,
                To = toDip,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = fallbackEase,
                // The card must hold where the slide left it: the slide-out's end is off-screen, and the
                // window is not torn down until after the settle buffer.
                FillBehavior = FillBehavior.HoldEnd,
            };
            Storyboard.SetTarget(animation, host);
            Storyboard.SetTargetProperty(animation, BuildSlidePath());

            var built = new Storyboard();
            built.Children.Add(animation);
            return built;
        }

        /// <summary>
        /// A storyboard child the plugin is free to point at its own slide host: one the theme did not
        /// aim somewhere specific. A child that names its own target is left entirely alone.
        /// </summary>
        private static bool IsUntargeted(Timeline child)
        {
            return Storyboard.GetTargetName(child) == null && Storyboard.GetTarget(child) == null;
        }

        /// <summary>
        /// One line describing where the card actually sits once the wave has settled — after the slide
        /// has been stopped and the window snapped to its corner. This is the span the notification
        /// spends simply on screen, so anything other than a slide offset of 0 and a card rect on the
        /// corner means the card is somewhere the user cannot see it.
        ///
        /// Worth a line of its own because the slide's own diagnostic covers only the animation: a slide
        /// that ran perfectly and then had the card moved out from under it afterwards reports as
        /// healthy.
        /// </summary>
        private void ReportSettledCard(Window window)
        {
            try
            {
                var renderScale = ToastWindowPlacer.RenderScale(window);
                var measured = ToastWindowPlacer.TryMeasureCardPhysical(
                    window, _activeCardSurface, renderScale, SlideOffsetDipX(), SlideOffsetDipY(),
                    out var insetX, out var insetY, out var cardW, out var cardH);
                ToastWindowPlacer.TryGetPhysicalRect(window, out var windowPhys);
                _logger?.Info(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[Toast] Settled: slideDipY={0:0.0} measured={1} cardInset={2},{3} card={4}x{5} " +
                    "window={6},{7} {8}x{9} opacity={10:0.##}",
                    SlideOffsetDipY(),
                    measured,
                    insetX,
                    insetY,
                    cardW,
                    cardH,
                    windowPhys.X,
                    windowPhys.Y,
                    windowPhys.Width,
                    windowPhys.Height,
                    window?.Opacity ?? -1d));
            }
            catch
            {
                // Diagnostics only.
            }
        }

        /// <summary>
        /// Writes the running slide's one diagnostic line and clears the bookkeeping so it is written
        /// exactly once, from whichever of natural completion or <see cref="StopActiveSlide"/> comes
        /// first. Ungated (unlike the PerfScope-gated wave lines) because the numbers it carries —
        /// above all the gap between the slide's first two frames — are what distinguish a slide that
        /// ran short of frames from one that was merely slow.
        /// </summary>
        private void ReportActiveSlide(string end)
        {
            var ticks = _activeSlideTicks;
            if (ticks == null)
            {
                return;
            }

            _activeSlideTicks = null;
            try
            {
                _logger?.Info(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[Toast] Slide {0}: requestedMs={1:0} spanMs={2:0.00} frames={3} meanMs={4:0.00} " +
                    "firstGapMs={5:0.00} maxGapMs={6:0.00} monitorHz={7} movedDip={8:0.0} end={9}",
                    _activeSlideLabel,
                    _activeSlideRequestedMs,
                    ticks.SpanMs,
                    ticks.Frames,
                    ticks.MeanIntervalMs,
                    ticks.FirstIntervalMs,
                    ticks.MaxIntervalMs,
                    _activeMonitorRefreshHz,
                    _activeSlideMovedDip,
                    end));
            }
            catch
            {
                // Diagnostics only; a formatting or logging failure must never affect a wave.
            }
        }

        // Decode sizes the card's images are requested at. Hints, not cache keys: for an Image element
        // AsyncImage takes the larger of the authored value and one inferred from the element's laid-out
        // size and the monitor scale, so a configurable icon size or a scaled monitor produces a
        // different key and the prime warms the codec, the file read and any download but not the final
        // decode. The background is the exception — its host Image is laid out at zero size, so there is
        // nothing to infer and the authored value is used verbatim, making the prime hit exactly.
        private const int PrimeIconDecodePixel = 160;
        private const int PrimeRightBadgeDecodePixel = 96;
        private const int PrimeIconBadgeDecodePixel = 64;
        private const int PrimeBackgroundDecodePixel = 768;

        /// <summary>
        /// Starts the card's image decodes and ray-silhouette traces for a wave that is about to show,
        /// so the card is complete on its first frame instead of completing itself while it slides.
        /// Everything here is work the card would do anyway, only earlier — into the same caches — and
        /// none of it is awaited or required: on failure the card loads exactly as it does today.
        ///
        /// A late-arriving image is the second of the two visible defects. The window is
        /// SizeToContent, so an image landing mid-slide resizes the HWND while the slide is moving it
        /// with SWP_NOSIZE, and the resting Y was computed from the pre-resize height — so the card
        /// lands short and the placement snap jumps it the rest of the way.
        /// </summary>
        private void PrimeWaveVisuals(IReadOnlyList<AchievementToastViewModel> cardItems)
        {
            if (cardItems == null || cardItems.Count == 0)
            {
                return;
            }

            // Collected on the UI thread: these getters read the style, the provider registry and the
            // filesystem. The decodes themselves are thread-agnostic and hand back frozen bitmaps.
            var iconPaths = new List<string>();
            var requests = new List<KeyValuePair<string, int>>();
            var seenRequests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var vm in cardItems)
            {
                if (vm == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(vm.IconPath) && !iconPaths.Contains(vm.IconPath))
                {
                    iconPaths.Add(vm.IconPath);
                }

                AddPrimeRequest(requests, seenRequests, vm.IconPath, PrimeIconDecodePixel);
                if (vm.ShowRightBadge)
                {
                    AddPrimeRequest(
                        requests, seenRequests, vm.ToastBadgeSource as string, PrimeRightBadgeDecodePixel);
                }

                if (vm.ShowBadge)
                {
                    AddPrimeRequest(
                        requests, seenRequests, vm.ToastBadgeSource as string, PrimeIconBadgeDecodePixel);
                }

                if (vm.IsGameCompleted)
                {
                    AddPrimeRequest(
                        requests, seenRequests, vm.ToastCompletedBadgeSource as string, PrimeIconDecodePixel);
                }

                if (vm.HasToastBackground)
                {
                    // Must stay the string the template's background host resolves to — for a live
                    // toast ToastBackgroundRenderSource is this same path — since the cache key is the
                    // source string: priming a different one warms an entry nothing asks for and the
                    // image still arrives mid-slide, with nothing failing to say so.
                    AddPrimeRequest(
                        requests, seenRequests, vm.ToastBackgroundImagePath, PrimeBackgroundDecodePixel);
                }
            }

            // Traces and decodes run as two concurrent chains so a slow silhouette trace cannot starve
            // the image decodes inside the window before the toast shows.
            var imageService = PlayniteAchievementsPlugin.Instance?.ImageService;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        PrimeRayTracksAsync(iconPaths),
                        PrimeImagesAsync(imageService, requests)).ConfigureAwait(false);
                }
                catch
                {
                    // Priming is an optimization; a failure must never surface as an unobserved fault.
                }
            });
        }

        // A badge source is either a path string or an already-built image; only the former is fetched.
        private static void AddPrimeRequest(
            List<KeyValuePair<string, int>> requests, HashSet<string> seen, string uri, int decodePixel)
        {
            if (string.IsNullOrWhiteSpace(uri) || !seen.Add($"{decodePixel}{uri}"))
            {
                return;
            }

            requests.Add(new KeyValuePair<string, int>(uri, decodePixel));
        }

        private static async Task PrimeRayTracksAsync(IReadOnlyList<string> iconPaths)
        {
            for (var i = 0; i < iconPaths.Count; i++)
            {
                // Already bounded and exception-swallowing per icon.
                await WarmRayTrackAsync(iconPaths[i]).ConfigureAwait(false);
            }
        }

        private static async Task PrimeImagesAsync(
            MemoryImageService imageService, IReadOnlyList<KeyValuePair<string, int>> requests)
        {
            if (imageService == null)
            {
                return;
            }

            for (var i = 0; i < requests.Count; i++)
            {
                try
                {
                    await imageService
                        .GetAsync(requests[i].Key, requests[i].Value, System.Threading.CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A missing or undecodable image is the card's problem to handle, as it is today.
                }
            }
        }

        private const int RayTrackWarmupTimeoutMs = 250;

        /// <summary>
        /// Puts an icon's ray track in the cache so a synchronous offscreen render can find it. Never
        /// blocks the capture: past the timeout the burst simply falls back to a rounded rectangle.
        /// </summary>
        private static async Task WarmRayTrackAsync(string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return;
            }

            try
            {
                var service = PlayniteAchievementsPlugin.Instance?.RayTrackService;
                if (service == null || service.TryGet(iconPath, out _))
                {
                    return;
                }

                await Task.WhenAny(
                    service.GetAsync(iconPath, System.Threading.CancellationToken.None),
                    Task.Delay(RayTrackWarmupTimeoutMs));
            }
            catch
            {
            }
        }

        private void StopActiveSlide()
        {
            if (_activeSlideTick != null)
            {
                CompositionTarget.Rendering -= _activeSlideTick;
                _activeSlideTick = null;
            }

            // Stop the storyboard AND clear the animation off the property. Stop alone leaves the
            // animation holding the property at its base value, so a later direct write to Y would be
            // ignored and the card could never be nudged back to rest.
            var storyboard = _runningSlideStoryboard;
            _runningSlideStoryboard = null;
            if (storyboard != null && _activeSlideHost != null)
            {
                try
                {
                    storyboard.Stop(_activeSlideHost);
                    storyboard.Remove(_activeSlideHost);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Stopping the toast slide storyboard failed.");
                }
            }

            // Outside the guard: a slide that reached its final frame already unhooked itself and
            // reported, so this is a no-op for it and only a genuinely cut-short slide reports here.
            ReportActiveSlide("stopped");
        }

        /// <summary>
        /// How far the card travels, in the slide host's DIPs: the card's own laid-out height plus
        /// enough padding to clear the screen edge.
        ///
        /// Measured from the card surface, not the window. The window is deliberately taller than the
        /// card by exactly this distance (the travel room), so deriving it from the window would feed
        /// the reservation its own output and grow the window every pass.
        /// </summary>
        private double SlideDistanceDip(Window window)
        {
            var height = _activeCardSurface?.ActualHeight ?? 0d;
            if (double.IsNaN(height) || height <= 0)
            {
                height = window != null && window.ActualHeight > 0
                    ? window.ActualHeight
                    : ToastWindowPlacer.DefaultCardHeightDip;
            }

            return height + SlideTravelPaddingDip;
        }

        /// <summary>
        /// Reserves the slide's travel as empty room past the card on the side it enters from, so the
        /// window is big enough to hold the card at both ends. An HWND clips its content unconditionally,
        /// so without this the card is simply cut off while it slides.
        ///
        /// Runs once per wave, after the DPI compensation has settled the card's size and before the
        /// settled placement, which is what puts the now-larger window where the card lands on the
        /// corner.
        /// </summary>
        private void ReserveSlideTravel(Window window, ItemsControl surface)
        {
            if (window == null || surface == null)
            {
                return;
            }

            // Nothing to reserve when neither animation moves the card — a theme that fades or scales
            // instead of sliding gets a window that is exactly its card, which is also what keeps the
            // host's centre scale pivot on the card rather than on empty travel room.
            if (!_activeSlideInTravels && !_activeSlideOutTravels)
            {
                return;
            }

            try
            {
                ToastSurfaceFactory.ApplySlideTravel(surface, SlideDistanceDip(window), SlideFromBottom());
                window.UpdateLayout();
            }
            catch (Exception ex)
            {
                // Without the room the card would be clipped mid-slide, which is worse than not sliding.
                _logger?.Debug(ex, "Reserving toast slide travel failed; the slide is skipped this wave.");
                _activeSlideInStoryboard = null;
                _activeSlideOutStoryboard = null;
                _activeSlideInMs = 0;
                _activeSlideOutMs = 0;
            }
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
