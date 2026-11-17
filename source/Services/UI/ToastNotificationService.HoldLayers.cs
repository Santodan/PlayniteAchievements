using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.UI
{
    // A resting wave's layers: building and showing them, standing the wave's own window still
    // under them, the border glow's pulse handed to the presenter, and the hand-back before the
    // exit.
    internal sealed partial class ToastNotificationService
    {
        // The layers are shown no sooner than this after they are created, and are given up on
        // when their images have not loaded HoldLayerLoadTimeoutMs into the hold. Nothing reports
        // when a layer window's first frame has been presented, and shown before it the content
        // layer would be empty over the background. With no wait at all, no capture of the 30
        // compositions after the split missed content (TemplateBench --layers-compare under
        // GameLoad, 6 cycles); the wait is margin.
        private const int HoldLayerSettleMs = 100;
        private const int HoldLayerLoadTimeoutMs = 1600;
        private const int HoldLayerLoadPollMs = 20;

        // How long before the hold ends the wave's own window is made live again, so it has
        // presented the background's current frame by the time the layers over it go.
        private const int HoldLayerHandBackMs = 250;

        // Redraws per second, while the card's rays rest in a layer of their own, of the bursts
        // that are not served at every frame (the icon's); the card's burst is.
        private const double HoldLayerRayFps = 60d;

        /// <summary>
        /// Splits a resting wave into layers when each of its cards has an opaque base
        /// (ToastLayer.CanSplit): the backgrounds and the content each go to a cloaked window of
        /// their own, drawn by the presenter over the wave's own window, and the cards' rays to
        /// one drawn under it. The wave's own
        /// window then stands still under them (its animated images hold their frame and its
        /// content's glow pulses pause), so it is redrawn only by what shows around the card.
        /// <paramref name="prepared"/> are layers built ahead by <see cref="PrepareHoldLayers"/>,
        /// which are shown as soon as they are ready; without them, or when the cards have
        /// changed size since, the layers are built here.
        /// Returns null when the wave is not presented, has nothing to split, or the layers could
        /// not be built; the wave then rests as it is.
        /// </summary>
        private async Task<ToastHoldLayers> BeginHoldLayersAsync(
            Window window, ItemsControl surface, IReadOnlyList<AchievementToastViewModel> cards,
            DataTemplate template, bool perMonitorWindow, ToastHoldLayers prepared = null)
        {
            var layers = await BeginHoldLayersCoreAsync(
                window, surface, cards, template, perMonitorWindow, prepared).ConfigureAwait(true);
            if (layers == null)
            {
                ReleasePreHeldPulses();
                DropRaysAhead();
            }

            return layers;
        }

        // The wave's own rays hidden because a layer shows them for the whole wave.
        private List<FrameworkElement> _mainRaysHiddenAhead;

        /// <summary>
        /// Shows the cards' rays from their layer for the whole wave, entrance and exit included,
        /// and hides them in the wave's own window, while that window is still hidden. Rays that
        /// changed hands when the wave came to rest were drawn by both for as long as the window
        /// took to redraw without them, at twice their strength.
        /// </summary>
        private void ShowRaysAhead(ItemsControl surface, ToastHoldLayers layers)
        {
            try
            {
                if (layers.Rays == null || _mainRaysHiddenAhead != null || _activePresenter == null ||
                    !_activePresenter.AddUnderlayAhead(layers.Rays))
                {
                    return;
                }

                _mainRaysHiddenAhead = new List<FrameworkElement>();
                foreach (var rays in ToastLayer.Find(surface, ToastLayerRole.Rays))
                {
                    if (rays.Visibility == Visibility.Visible)
                    {
                        rays.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                        _mainRaysHiddenAhead.Add(rays);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast rays could not be shown from their layer ahead of the hold.");
            }
        }

        /// <summary>Gives the rays back to the wave's own window, when their layer is going.</summary>
        private void DropRaysAhead()
        {
            var hidden = _mainRaysHiddenAhead;
            _mainRaysHiddenAhead = null;
            if (hidden == null)
            {
                return;
            }

            try
            {
                foreach (var rays in hidden)
                {
                    if (rays.Visibility == Visibility.Hidden)
                    {
                        rays.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                    }
                }

                _activePresenter?.RemoveUnderlay();
            }
            catch
            {
            }
        }

        private async Task<ToastHoldLayers> BeginHoldLayersCoreAsync(
            Window window, ItemsControl surface, IReadOnlyList<AchievementToastViewModel> cards,
            DataTemplate template, bool perMonitorWindow, ToastHoldLayers prepared)
        {
            var presenter = _activePresenter;
            var layers = prepared;
            try
            {
                var waited = Stopwatch.StartNew();
                if (layers != null && (presenter == null || !layers.HasSizeOf(window)))
                {
                    DropRaysAhead();
                    layers.Dispose();
                    layers = null;
                }

                layers = layers ?? PrepareHoldLayers(window, surface, cards, template, perMonitorWindow);
                if (layers == null)
                {
                    return null;
                }

                while (layers.AgeMs < HoldLayerSettleMs || !layers.IsLoadedLike(surface))
                {
                    if (waited.ElapsedMilliseconds > HoldLayerLoadTimeoutMs)
                    {
                        break;
                    }

                    await Task.Delay(HoldLayerLoadPollMs).ConfigureAwait(true);
                    if (_disposed || !ReferenceEquals(_activePresenter, presenter))
                    {
                        layers.Dispose();
                        return null;
                    }
                }

                var loaded = layers.IsLoadedLike(surface);
                var added = loaded &&
                    (layers.Rays == null || _mainRaysHiddenAhead != null || presenter.AddUnderlay(layers.Rays)) &&
                    presenter.AddLayer(layers.Background) && presenter.AddLayer(layers.Content);
                if (!added)
                {
                    presenter.RemoveLayers();
                    layers.Dispose();
                    _logger?.Info(
                        $"[Toast] Hold layers: not shown (imagesLoaded={loaded}), the wave rests as one window.");
                    return null;
                }

                presenter.ShowLayers();
                if (layers.Rays != null)
                {
                    RayAnimationDriver.SetHeldFps(HoldLayerRayFps);
                }

                layers.MainHold = HoldMainUnderLayers(surface, out var mainIsStill);
                layers.MainIsStill = mainIsStill;
                _activeSampleLayers = layers;
                _logger?.Info(
                    $"[Toast] Hold layers: shown {waited.ElapsedMilliseconds} ms into the hold, " +
                    $"built {layers.AgeMs} ms ago, " +
                    $"mainStill={layers.MainIsStill}, pulsePresented={_activeHoldPulse != null}, " +
                    $"pulseHeldAhead={_pulseHeldAhead}, raysAhead={_mainRaysHiddenAhead != null}");
                return layers;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast hold layers failed; the wave rests as one window.");
                RayAnimationDriver.ClearHeldFps();
                _activeSampleLayers = null;
                try
                {
                    presenter?.RemoveLayers();
                    layers?.Dispose();
                }
                catch
                {
                }

                return null;
            }
        }

        /// <summary>
        /// Builds the layers of a wave whose cards each have an opaque base (ToastLayer.CanSplit),
        /// cloaked and not yet presented, or returns null when the wave is not presented, has
        /// nothing to split (which includes a background whose image has not loaded over a
        /// surface that draws nothing), or the layers could not be built.
        /// </summary>
        private ToastHoldLayers PrepareHoldLayers(
            Window window, ItemsControl surface, IReadOnlyList<AchievementToastViewModel> cards,
            DataTemplate template, bool perMonitorWindow, bool ahead = false)
        {
            try
            {
                if (_activePresenter == null || surface == null || cards == null)
                {
                    return null;
                }

                var whyNot = ToastLayer.WhyNotSplit(surface);
                if (whyNot != null)
                {
                    _logger?.Info($"[Toast] Hold layers: not split, {whyNot}; the wave rests as one window.");
                    return null;
                }

                var layers = ToastHoldLayers.TryCreate(
                    _api, window, surface, cards, template, perMonitorWindow);
                if (layers == null)
                {
                    _logger?.Info("[Toast] Hold layers: not built, the wave rests as one window.");
                }
                else if (_preHeldPulses == null)
                {
                    if (ahead)
                    {
                        ShowRaysAhead(surface, layers);
                    }

                    _preHeldPulses = PreHoldBackPulses(surface);
                }

                return layers;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast hold layers could not be built.");
                return null;
            }
        }

        /// <summary>
        /// Stands the wave's own window still under its layers: every animated image under the
        /// card host holds the frame it is on, the glow pulses in each card's content pause, and
        /// everything but the glow or shadow behind the card is hidden: the layers cover the card
        /// and a layer under the window shows its rays.
        /// </summary>
        private IDisposable HoldMainUnderLayers(ItemsControl surface, out bool mainIsStill)
        {
            var heldPulses = HoldBackPulses(surface, out mainIsStill);
            var stills = AsyncImage.HoldCurrentFrames(_activeSlideHost ?? surface);
            var contents = new List<DependencyObject>();
            foreach (var background in ToastLayer.Find(surface, ToastLayerRole.Background))
            {
                var content = VisualTreeHelper.GetParent(background);
                if (content != null && !contents.Contains(content))
                {
                    contents.Add(content);
                    RarityGlowPulse.PauseUnder(content);
                }
            }

            var showAll = ToastLayer.ShowOnlyBack(surface);

            // A blurred card's live background keeps changing; hidden here, it would still have
            // this window redrawn at each of its frames.
            var restoreImages = ToastLayer.ReleaseHiddenImages(surface);
            return new HeldMain(() =>
            {
                // heldPulses stay at their peak for what is left of the wave.
                FadeBackPulsesToPeak(HoldLayerHandBackMs / 2);
                restoreImages();
                showAll();
                stills.Dispose();
                foreach (var content in contents)
                {
                    RarityGlowPulse.ResumeUnder(content);
                }
            });
        }

        /// <summary>
        /// Hands the pulse of every card's border glow to the presenter: each glow stops at the
        /// pulse's peak and the wave's own thumbnail is dimmed along the same sweep, once per DWM
        /// composition. Dimming that thumbnail dims everything in the window alike, so this is
        /// done only when every card's glow runs the same pulse; otherwise the glows keep pulsing
        /// in the window. <paramref name="mainIsStill"/> reports whether anything behind the
        /// cards is left animating in the window.
        /// </summary>
        private List<FrameworkElement> HoldBackPulses(ItemsControl surface, out bool mainIsStill)
        {
            var held = new List<FrameworkElement>();
            mainIsStill = false;
            var ahead = _preHeldPulses;
            _preHeldPulses = null;
            _pulseHeldAhead = ahead != null;
            if (ahead != null)
            {
                // The glows already stand at their peak, so the presented pulse starts there at
                // full opacity: nothing changes on screen at this moment, whenever the window and
                // the thumbnail's opacity each reach it.
                _activeHoldPulse = new HoldPulse(ahead.Peak, ahead.Floor, ahead.Peak, ahead.HalfCycleSeconds);
                mainIsStill = true;
                UpdatePresenter();
                return ahead.Glows;
            }

            var glows = ToastLayer.Find(surface, ToastLayerRole.Back);

            var pulsing = glows.Where(RarityGlowPulse.GetIsActive).ToList();
            if (pulsing.Count == 0)
            {
                mainIsStill = true;
                return held;
            }

            if (pulsing.Count != glows.Count)
            {
                return held;
            }

            HoldPulse pulse = null;
            foreach (var glow in pulsing)
            {
                if (!RarityGlowPulse.TryHoldEffectPulseAtPeak(glow, out var shown, out var floor, out var peak, out var seconds) ||
                    (pulse != null && !pulse.Matches(floor, peak, seconds)))
                {
                    ReleaseBackPulses(held, pulse);
                    held.Clear();
                    return held;
                }

                held.Add(glow);
                pulse = pulse ?? new HoldPulse(shown, floor, peak, seconds);
            }

            _activeHoldPulse = pulse;
            mainIsStill = true;
            UpdatePresenter();
            return held;
        }

        /// <summary>
        /// Glows held at their peak before the wave is shown, for <see cref="HoldBackPulses"/> to
        /// take over when the wave comes to rest.
        /// </summary>
        private sealed class PreHeldPulses
        {
            public List<FrameworkElement> Glows;
            public double Floor;
            public double Peak;
            public double HalfCycleSeconds;
        }

        private PreHeldPulses _preHeldPulses;

        // Whether the pulse the presenter shows now was held before the wave was shown.
        private bool _pulseHeldAhead;

        /// <summary>
        /// Stands every card's border glow still at its pulse's peak, under the conditions
        /// <see cref="HoldBackPulses"/> hands a pulse to the presenter under, or returns null
        /// having changed nothing. Called while the wave is still hidden: the glow then shows at
        /// its peak through the entrance, and the presented pulse starts from that peak when the
        /// wave comes to rest. Holding a glow that is mid-sweep at that moment instead shows its
        /// jump to the peak for as long as the thumbnail's opacity takes to follow.
        /// </summary>
        private PreHeldPulses PreHoldBackPulses(ItemsControl surface)
        {
            try
            {
                var glows = ToastLayer.Find(surface, ToastLayerRole.Back);
                var pulsing = glows.Where(RarityGlowPulse.GetIsActive).ToList();
                if (pulsing.Count == 0 || pulsing.Count != glows.Count)
                {
                    return null;
                }

                var ahead = new PreHeldPulses { Glows = new List<FrameworkElement>() };
                foreach (var glow in pulsing)
                {
                    if (!RarityGlowPulse.TryHoldEffectPulseAtPeak(glow, out _, out var floor, out var peak, out var seconds) ||
                        (ahead.Glows.Count > 0 &&
                         (Math.Abs(floor - ahead.Floor) > 1e-6 || Math.Abs(peak - ahead.Peak) > 1e-6 ||
                          Math.Abs(seconds - ahead.HalfCycleSeconds) > 1e-6)))
                    {
                        ReleasePreHeldPulses(ahead);
                        return null;
                    }

                    ahead.Glows.Add(glow);
                    ahead.Floor = floor;
                    ahead.Peak = peak;
                    ahead.HalfCycleSeconds = seconds;
                }

                return ahead;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Lets glows held ahead pulse in the window again, from their peak.</summary>
        private void ReleasePreHeldPulses(PreHeldPulses ahead = null)
        {
            if (ahead == null)
            {
                ahead = _preHeldPulses;
                _preHeldPulses = null;
            }

            if (ahead?.Glows == null)
            {
                return;
            }

            foreach (var glow in ahead.Glows)
            {
                try
                {
                    RarityGlowPulse.ReleaseEffectPulse(glow, ahead.HalfCycleSeconds);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Brings the presented pulse up to its peak over <paramref name="milliseconds"/> and
        /// leaves the glows standing at theirs: the wave's own window then shows the glow as the
        /// thumbnail did, without either having to change at one instant. For the hand-back
        /// before the exit, where the window is about to show the whole card at full opacity.
        /// </summary>
        private void FadeBackPulsesToPeak(int milliseconds)
        {
            var pulse = _activeHoldPulse;
            if (pulse == null)
            {
                return;
            }

            if (_activePresenter?.MainOpacitySource != null)
            {
                _logger?.Info("[Toast] Presenter pulse: " + _activePresenter.RestReport());
            }

            pulse.FadeToPeak(milliseconds);
        }

        private void ReleaseBackPulses(List<FrameworkElement> held, HoldPulse pulse = null)
        {
            pulse = pulse ?? _activeHoldPulse;
            if (_activeHoldPulse != null && _activePresenter?.MainOpacitySource != null)
            {
                _logger.Info("[Toast] Presenter pulse: " + _activePresenter.RestReport());
            }

            _activeHoldPulse = null;
            var cycleSeconds = pulse?.CycleSeconds() ?? 0d;
            foreach (var glow in held)
            {
                try
                {
                    RarityGlowPulse.ReleaseEffectPulse(glow, cycleSeconds);
                }
                catch
                {
                }
            }

            UpdatePresenter();
        }

        /// <summary>
        /// The glow pulse's sweep on a clock of its own: from the floor to the peak and back, sine
        /// eased each way, as RarityGlowPulse animates it, continuing from the opacity shown when
        /// it was taken over.
        /// </summary>
        private sealed class HoldPulse
        {
            private readonly double _floor;
            private readonly double _peak;
            private readonly double _halfCycleSeconds;
            private readonly double _startSeconds;
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public HoldPulse(double shown, double floor, double peak, double halfCycleSeconds)
            {
                _floor = floor;
                _peak = peak;
                _halfCycleSeconds = halfCycleSeconds;
                var span = peak - floor;
                var eased = span > 0 ? Math.Max(0d, Math.Min(1d, (shown - floor) / span)) : 1d;
                // The rising half of the cycle at the opacity shown.
                _startSeconds = Math.Acos(1d - (2d * eased)) / Math.PI * halfCycleSeconds;
            }

            public bool Matches(double floor, double peak, double halfCycleSeconds)
            {
                return Math.Abs(floor - _floor) < 1e-6 && Math.Abs(peak - _peak) < 1e-6 &&
                    Math.Abs(halfCycleSeconds - _halfCycleSeconds) < 1e-6;
            }

            /// <summary>Seconds into the cycle (rise, then fall) now.</summary>
            public double CycleSeconds()
            {
                return (_startSeconds + _clock.Elapsed.TotalSeconds) % (2d * _halfCycleSeconds);
            }

            private double _fadeFrom = -1d;
            private double _fadeMs;
            private readonly Stopwatch _fade = new Stopwatch();

            /// <summary>
            /// Leaves the sweep and rises from the opacity shown now to the peak over
            /// <paramref name="milliseconds"/>, then stays there. Read from other threads.
            /// </summary>
            public void FadeToPeak(double milliseconds)
            {
                var from = Scale();
                _fadeMs = Math.Max(1d, milliseconds);
                _fade.Restart();
                System.Threading.Volatile.Write(ref _fadeFrom, from);
            }

            /// <summary>The pulse's opacity now, as a fraction of its peak.</summary>
            public double Scale()
            {
                var fadeFrom = System.Threading.Volatile.Read(ref _fadeFrom);
                if (fadeFrom >= 0d)
                {
                    var t = Math.Min(1d, _fade.Elapsed.TotalMilliseconds / _fadeMs);
                    return fadeFrom + ((1d - fadeFrom) * ((1d - Math.Cos(Math.PI * t)) / 2d));
                }

                if (_peak <= 0)
                {
                    return 1d;
                }

                var progress = CycleSeconds() / _halfCycleSeconds;
                if (progress > 1d)
                {
                    progress = 2d - progress;
                }

                var eased = (1d - Math.Cos(Math.PI * progress)) / 2d;
                return (_floor + ((_peak - _floor) * eased)) / _peak;
            }
        }

        private sealed class HeldMain : IDisposable
        {
            private Action _release;

            public HeldMain(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                var release = _release;
                _release = null;
                release?.Invoke();
            }
        }

        /// <summary>
        /// Returns a split wave to its own window before the exit, which moves and fades one
        /// window. A held window first takes over the countdown bars where the content layer's
        /// stand, goes live again, and is given <paramref name="handBackMs"/> to present before
        /// the layers over it are removed.
        /// </summary>
        private async Task EndHoldLayersAsync(Window window, ToastHoldLayers layers, int handBackMs)
        {
            RayAnimationDriver.ClearHeldFps();
            try
            {
                StopCountdownBars(layers.Content);
                if (layers.MainHold != null)
                {
                    var shown = FindCountdownBars(layers.Content).ToList();
                    var own = FindCountdownBars(window).ToList();
                    for (var i = 0; i < own.Count && i < shown.Count; i++)
                    {
                        if (own[i].RenderTransform is ScaleTransform target &&
                            shown[i].RenderTransform is ScaleTransform source)
                        {
                            target.ScaleX = source.ScaleX;
                        }
                    }

                    // Live again, so the sampler reads the window's own cards from here on.
                    _activeSampleLayers = null;
                    layers.MainHold.Dispose();
                    layers.MainHold = null;
                    await Task.Delay(handBackMs).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Toast hold layers hand-back failed.");
            }

            try
            {
                _activePresenter?.RemoveLayers();
            }
            catch
            {
            }
        }
    }
}
