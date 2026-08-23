using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Controls.RayGlow;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The layer behind an achievement icon that carries the rays glow style.
    ///
    /// A track is derived from the subject's own alpha silhouette — the smoothed convex hull of it — and
    /// arrow bases ride that track like a conveyor belt. Each arrow spans inward as well as outward, so
    /// the opaque subject covers its base and the subject's own pixels decide where it appears to begin.
    /// Height and width come from a standing wave keyed to position on the track, so arrows swell and
    /// shrink as they pass through fixed regions of the outline.
    ///
    /// Placement is arranged by the call sites: this is the first child of a <c>ClipToBounds="False"</c>
    /// Grid that also holds the soft glow layer and the crisp icon, gated on the same conditions as the
    /// glow. The tier selection reaches it through <see cref="RayGlowTiers"/>, <see cref="IsActive"/>
    /// follows the global animation toggle, and the notification surfaces bind <see cref="PhaseLock"/>
    /// so captures can be made deterministic. <see cref="SubjectUri"/> carries the same image the
    /// sibling icon draws, which is what the track is traced from.
    ///
    /// Three findings from the removed attempts are worth not rediscovering. A layer that moves must not
    /// be bitmap-cached: WPF re-rasterizes a cache whenever the element's transform changes, so caching
    /// a moving layer costs a full re-rasterization per row per frame and was what made a populated grid
    /// lag — hence no CacheMode and no Effect anywhere on this control. An animation must not be
    /// attached to rows that draw nothing, whether because their tier is unselected or because the art
    /// is absent, so subscription is re-evaluated on every gating change and re-checked each tick. And
    /// nothing here accumulates across frames: the phase is read from the shared clock, so a surface
    /// that renders once into a bitmap gets the same picture as one that has been ticking all session.
    /// </summary>
    public class RarityRayBurst : Panel, IRayAnimationTarget
    {
        private readonly DrawingVisual _visual = new DrawingVisual();

        private RayTrack _track;
        private RayArrowLayout.MappedTrack _mapped;
        private Size _mappedSlot;
        private string _subjectKey;
        private CancellationTokenSource _loadCts;
        private RarityAppearanceHelper.RayGlowPalette _palette;
        private int _paletteGeneration = -1;
        private Size _arrangedSize;
        private bool _appearanceHooked;
        private bool _localEpochSet;
        private double _localEpochMs;

        private RayArrowLayout.RayArrowSpine[] _spines;
        private RayArrowLayout.RayArrowQuad[] _quads;

        public RarityRayBurst()
        {
            IsHitTestVisible = false;
            Focusable = false;

            // Both host Grids set these and they inherit. Snapping arbitrary-angle geometry to the pixel
            // grid quantizes the arrow flanks, which shows up as width jitter as the conveyor advances.
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;

            AddVisualChild(_visual);

            RarityAppearanceHelper.BindRayGlowTiers(this, RayGlowTiersProperty);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        /// <summary>Rarity tier whose color the rays take.</summary>
        public static readonly DependencyProperty RarityProperty =
            DependencyProperty.Register(
                nameof(Rarity), typeof(RarityTier), typeof(RarityRayBurst),
                new PropertyMetadata(RarityTier.Common, OnAppearanceAffectingChanged));

        public RarityTier Rarity
        {
            get => (RarityTier)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        /// <summary>
        /// When true the rays take the completed-game gradient colors instead of a rarity tier, for the
        /// completion glow on game and category art. Completed art has no tier of its own, so the call
        /// site gates it on the selection's completion entry rather than on <see cref="RayGlowTiers"/>.
        /// </summary>
        public static readonly DependencyProperty UseCompletedColorsProperty =
            DependencyProperty.Register(
                nameof(UseCompletedColors), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false, OnAppearanceAffectingChanged));

        public bool UseCompletedColors
        {
            get => (bool)GetValue(UseCompletedColorsProperty);
            set => SetValue(UseCompletedColorsProperty, value);
        }

        /// <summary>
        /// Which rarity tiers show the rays. Self-bound to the global setting in the constructor, so the
        /// call sites need no per-tier markup and changing the selection reaches layers already on
        /// screen.
        /// </summary>
        public static readonly DependencyProperty RayGlowTiersProperty =
            DependencyProperty.Register(
                nameof(RayGlowTiers), typeof(RaritySelection), typeof(RarityRayBurst),
                new PropertyMetadata(RaritySelection.None, OnGatingChanged));

        public RaritySelection RayGlowTiers
        {
            get => (RaritySelection)GetValue(RayGlowTiersProperty);
            set => SetValue(RayGlowTiersProperty, value);
        }

        /// <summary>
        /// Whether the effect animates. A style trigger sets this from the global AnimateRarityGlows
        /// toggle; the effect renders either way and only moves while it is set.
        /// </summary>
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false, OnGatingChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        /// <summary>
        /// When true (default) the animation phase-locks to the shared <see cref="GlowAnimationClock"/>
        /// so recreated elements resume mid-cycle. The notification surfaces bind this to IsPreview and
        /// opt out, so every captured wave starts from the same point.
        /// </summary>
        public static readonly DependencyProperty PhaseLockProperty =
            DependencyProperty.Register(
                nameof(PhaseLock), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(true));

        public bool PhaseLock
        {
            get => (bool)GetValue(PhaseLockProperty);
            set => SetValue(PhaseLockProperty, value);
        }

        /// <summary>
        /// How far the effect may reach beyond its layout slot, as a multiple of it. The tallest arrow
        /// reaches half the excess past the subject's short side, so this is what caps the burst's
        /// overall size. Completed game art passes its own value, being much larger than an icon.
        /// </summary>
        public static readonly DependencyProperty BurstScaleProperty =
            DependencyProperty.Register(
                nameof(BurstScale), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(1.55, OnRenderAffectingChanged));

        public double BurstScale
        {
            get => (double)GetValue(BurstScaleProperty);
            set => SetValue(BurstScaleProperty, value);
        }

        /// <summary>
        /// The image the sibling icon draws, whose silhouette the track is traced from. Typed as object
        /// to match AsyncImage's own Uri property, so a path string binds directly and anything else
        /// falls back to a rounded rectangle.
        /// </summary>
        public static readonly DependencyProperty SubjectUriProperty =
            DependencyProperty.Register(
                nameof(SubjectUri), typeof(object), typeof(RarityRayBurst),
                new PropertyMetadata(null, OnSubjectUriChanged));

        public object SubjectUri
        {
            get => GetValue(SubjectUriProperty);
            set => SetValue(SubjectUriProperty, value);
        }

        /// <summary>
        /// Inset between this layer's slot and the artwork inside it, for call sites that give their
        /// icon a margin. Without it the track sits outside the art by that margin.
        /// </summary>
        public static readonly DependencyProperty SubjectInsetProperty =
            DependencyProperty.Register(
                nameof(SubjectInset), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(0.0, OnLayoutAffectingChanged));

        public double SubjectInset
        {
            get => (double)GetValue(SubjectInsetProperty);
            set => SetValue(SubjectInsetProperty, value);
        }

        /// <summary>
        /// Corner rounding of the fallback track, as a fraction of the subject's short side. Only used
        /// where no silhouette was traced — an opaque rectangle, which is most icons and every cover —
        /// so this, not the silhouette smoothing, is what most arrows actually travel around. Generous
        /// on purpose: a base rounding a square corner turns through ninety degrees, and doing that over
        /// a wide arc reads as travel where doing it over a tight one reads as a corner being clipped.
        /// Surfaces whose art is clipped tighter than this pass their own value.
        /// </summary>
        public static readonly DependencyProperty CornerRadiusRatioProperty =
            DependencyProperty.Register(
                nameof(CornerRadiusRatio), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(0.22, OnLayoutAffectingChanged));

        public double CornerRadiusRatio
        {
            get => (double)GetValue(CornerRadiusRatioProperty);
            set => SetValue(CornerRadiusRatioProperty, value);
        }

        // The drawing lives in its own visual so a track arriving late, or a frame advancing, can be
        // re-issued without entering layout at all. It sits first so it stays behind anything a call
        // site might place inside this element.
        protected override int VisualChildrenCount => InternalChildren.Count + 1;

        protected override Visual GetVisualChild(int index)
        {
            return index == 0 ? _visual : InternalChildren[index - 1];
        }

        /// <summary>
        /// Reports no desired size, so this layer never drives layout however large it draws. Keep this:
        /// the subject has to be what establishes the cell. An Image measuring to its source's natural
        /// size was enough to inflate a 28px icon cell — and its whole DataGrid row — to the art's own
        /// dimensions.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var empty = new Size(0, 0);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(empty);
            }

            return empty;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(finalSize);
                child.Arrange(bounds);
            }

            // RenderSize is not published until arrange completes, so the arranged size is what the
            // mapping has to use.
            _arrangedSize = finalSize;
            if (finalSize != _mappedSlot)
            {
                _mapped = null;
            }

            Redraw();
            UpdateSubscription();
            return finalSize;
        }

        bool IRayAnimationTarget.WantsRayFrames => IsActive && IsVisible && ShouldDraw();

        /// <summary>
        /// Whether any part of this burst falls inside the window. IsVisible stays true for an
        /// element scrolled out of its list's viewport - it is merely clipped - so without this a
        /// wall of icons keeps rebuilding arrow geometry for rows nobody can see. Checked per
        /// frame rather than in WantsRayFrames: the driver drops a target whose WantsRayFrames
        /// goes false, and no event fires when an element scrolls back into view, so a burst
        /// unsubscribed for being clipped would stay frozen forever.
        /// </summary>
        private bool IsWithinViewport()
        {
            var root = PresentationSource.FromVisual(this)?.RootVisual as FrameworkElement;
            if (root == null || root.ActualWidth <= 0 || root.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                var bounds = TransformToAncestor(root).TransformBounds(new Rect(RenderSize));
                return bounds.IntersectsWith(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                // Not connected to the ancestor yet; treat as visible and let the next tick decide.
                return true;
            }
        }

        void IRayAnimationTarget.OnRayFrame()
        {
            // Clipped out of the scroll viewport: stay subscribed and skip the frame, so the
            // burst resumes the moment it scrolls back in.
            if (IsWithinViewport())
            {
                Redraw();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Hooked only while loaded, and paired with the generation check in EnsurePalette so a
            // missed unhook costs a stale repaint at worst rather than an element that never dies.
            if (!_appearanceHooked)
            {
                RarityAppearanceHelper.AppearanceChanged += OnAppearanceChanged;
                _appearanceHooked = true;
            }

            BeginTrackLoad();
            UpdateSubscription();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_appearanceHooked)
            {
                RarityAppearanceHelper.AppearanceChanged -= OnAppearanceChanged;
                _appearanceHooked = false;
            }

            // The track itself is kept: it is cached anyway, and dropping it would only make a
            // re-shown row flash its fallback for a frame.
            CancelPendingLoad();
            RayAnimationDriver.Unsubscribe(this);
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                BeginTrackLoad();
            }
            else
            {
                CancelPendingLoad();
            }

            UpdateSubscription();
        }

        private static void OnGatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var burst = (RarityRayBurst)d;
            burst.Redraw();
            burst.UpdateSubscription();
        }

        private static void OnAppearanceAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var burst = (RarityRayBurst)d;
            burst._palette = null;
            burst.Redraw();
            burst.UpdateSubscription();
        }

        private static void OnRenderAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((RarityRayBurst)d).Redraw();
        }

        private static void OnLayoutAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var burst = (RarityRayBurst)d;
            burst._mapped = null;
            burst.Redraw();
        }

        private static void OnSubjectUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((RarityRayBurst)d).ApplySubjectUri(e.NewValue);
        }

        private void OnAppearanceChanged(object sender, EventArgs e)
        {
            _palette = null;
            Redraw();
        }

        private void ApplySubjectUri(object value)
        {
            var key = value as string;

            // A recycled row rebound to the same icon keeps everything it already has.
            if (string.Equals(key, _subjectKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CancelPendingLoad();
            _subjectKey = key;
            _track = null;
            _mapped = null;

            // A fresh subject restarts the wave, so a recycled notification card cannot inherit the
            // epoch of the one before it.
            _localEpochSet = false;

            BeginTrackLoad();
            Redraw();
        }

        private void BeginTrackLoad()
        {
            if (_track != null || string.IsNullOrWhiteSpace(_subjectKey))
            {
                return;
            }

            var service = PlayniteAchievementsPlugin.Instance?.RayTrackService;
            if (service == null)
            {
                return;
            }

            // A cached track resolves with no async hop at all, which spares a recycled row the frame of
            // fallback it would otherwise show — and is the only way an offscreen render, which cannot
            // await anything, gets the real silhouette.
            if (service.TryGet(_subjectKey, out var cached))
            {
                _track = cached;
                _mapped = null;
                return;
            }

            if (!IsLoaded || !IsVisible)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _loadCts = cts;
            var requested = _subjectKey;
            _ = LoadTrackAsync(service, requested, cts);
        }

        private async Task LoadTrackAsync(RayTrackService service, string requested, CancellationTokenSource cts)
        {
            try
            {
                // No ConfigureAwait(false): everything below touches the visual tree.
                var track = await service.GetAsync(requested, cts.Token);

                if (cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
                {
                    return;
                }

                if (!string.Equals(requested, _subjectKey, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _track = track;
                _mapped = null;
                Redraw();
                UpdateSubscription();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    _loadCts = null;
                }

                cts.Dispose();
            }
        }

        private void CancelPendingLoad()
        {
            var existing = _loadCts;
            if (existing == null)
            {
                return;
            }

            _loadCts = null;
            try
            {
                existing.Cancel();
            }
            catch
            {
            }
        }

        // No subscribed flag is kept here: the driver drops targets on its own when their
        // WantsRayFrames goes false, so a local flag can go stale and block resubscription.
        // Subscribe and Unsubscribe are idempotent, so the driver's list is the single record.
        private void UpdateSubscription()
        {
            if (IsActive && IsVisible && IsLoaded && ShouldDraw())
            {
                RayAnimationDriver.Subscribe(this);
            }
            else
            {
                RayAnimationDriver.Unsubscribe(this);
            }
        }

        /// <summary>
        /// Whether this subject's tier is one the user asked to see rays on, and whether there is a
        /// subject at all. Completed art has no tier, so it is gated at the call site instead.
        /// </summary>
        private bool ShouldDraw()
        {
            if (_track != null && _track.IsEmpty)
            {
                return false;
            }

            return UseCompletedColors || RayGlowTiers.Contains(Rarity);
        }

        /// <summary>
        /// Laps completed, left unwrapped: the wave travels at its own rate relative to the arrows, so
        /// wrapping here would jerk it back every time the arrows crossed the start of the loop.
        /// </summary>
        private double CurrentLaps(PersistedSettings persisted)
        {
            var period = RayAnimationDriver.LapPeriodMs(persisted);
            if (!(period > 0))
            {
                return 0.0;
            }

            // Phase-locked: read straight off the shared epoch, so every burst on screen carries the
            // same wave and a recycled row picks it up mid-cycle instead of restarting.
            if (PhaseLock)
            {
                return GlowAnimationClock.ElapsedMilliseconds / period;
            }

            // Opted out: a per-instance epoch stamped at the first paint, so the wave always starts from
            // the beginning. A surface that never animates therefore renders that same first frame every
            // time, which is what makes a capture reproducible.
            if (!_localEpochSet)
            {
                _localEpochMs = GlowAnimationClock.ElapsedMilliseconds;
                _localEpochSet = true;
            }

            return (GlowAnimationClock.ElapsedMilliseconds - _localEpochMs) / period;
        }

        private void Redraw()
        {
            using (var context = _visual.RenderOpen())
            {
                if (!ShouldDraw())
                {
                    return;
                }

                var mapped = EnsureMappedTrack();
                if (mapped == null)
                {
                    return;
                }

                var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                var palette = EnsurePalette(persisted);
                if (palette == null)
                {
                    return;
                }

                // Derived from the track's own length, so an icon and a notification card carry arrows
                // of the same size the same distance apart rather than the same number of them.
                var count = RayArrowLayout.ArrowCountFor(mapped);
                if (_spines == null || _spines.Length < count)
                {
                    _spines = new RayArrowLayout.RayArrowSpine[count];
                    _quads = new RayArrowLayout.RayArrowQuad[count];
                }

                // Scaled by the track's length, so arrows cross the screen at one speed whatever they
                // are going around: a lap of a notification card is several laps of an icon.
                var laps = RayArrowLayout.ScaleLapsToTrack(CurrentLaps(persisted), mapped);

                // Every phase-locked burst of the same size and artwork traces the same arrows at
                // the same phase, so the geometry is identical across all of them - only the tier
                // brush differs. Building it once per frame and sharing the frozen result turns a
                // wall of icons from one tessellation each into one for the whole wall.
                var shared = GetSharedLayerGeometries(mapped, laps, count, palette.Layers.Count);
                if (shared == null)
                {
                    return;
                }

                // Widest and faintest first, so the narrower copies accumulate on top and the ray comes
                // out soft at its edges and bright along its spine.
                for (var i = 0; i < palette.Layers.Count; i++)
                {
                    if (shared[i] != null)
                    {
                        context.DrawGeometry(palette.Layers[i].Brush, null, shared[i]);
                    }
                }
            }
        }

        // Keyed by everything the arrow geometry depends on, so a stale entry can never be reused:
        // the traced silhouette, the slot it was mapped into, the shaping properties, and the phase.
        private struct LayerGeometryKey : IEquatable<LayerGeometryKey>
        {
            public string SubjectKey;
            public bool HasTrack;
            public double SlotWidth;
            public double SlotHeight;
            public double BurstScale;
            public double SubjectInset;
            public double CornerRadiusRatio;
            public int Count;
            public double Laps;

            public bool Equals(LayerGeometryKey other) =>
                string.Equals(SubjectKey, other.SubjectKey, StringComparison.Ordinal) &&
                HasTrack == other.HasTrack &&
                SlotWidth == other.SlotWidth &&
                SlotHeight == other.SlotHeight &&
                BurstScale == other.BurstScale &&
                SubjectInset == other.SubjectInset &&
                CornerRadiusRatio == other.CornerRadiusRatio &&
                Count == other.Count &&
                Laps.Equals(other.Laps);

            public override bool Equals(object obj) => obj is LayerGeometryKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = SubjectKey?.GetHashCode() ?? 0;
                hash = (hash * 397) ^ SlotWidth.GetHashCode();
                hash = (hash * 397) ^ SlotHeight.GetHashCode();
                hash = (hash * 397) ^ Count;
                hash = (hash * 397) ^ Laps.GetHashCode();
                return hash;
            }
        }

        private static readonly Dictionary<LayerGeometryKey, StreamGeometry[]> FrameGeometries =
            new Dictionary<LayerGeometryKey, StreamGeometry[]>();

        /// <summary>
        /// Drops the shared geometry built for the previous frame. Called once per tick by the
        /// driver, so the cache holds at most the distinct burst shapes on screen.
        /// </summary>
        internal static void BeginFrame() => FrameGeometries.Clear();

        private StreamGeometry[] GetSharedLayerGeometries(
            RayArrowLayout.MappedTrack mapped,
            double laps,
            int count,
            int layerCount)
        {
            var key = new LayerGeometryKey
            {
                SubjectKey = _subjectKey ?? string.Empty,
                HasTrack = _track != null,
                SlotWidth = _mappedSlot.Width,
                SlotHeight = _mappedSlot.Height,
                BurstScale = BurstScale,
                SubjectInset = SubjectInset,
                CornerRadiusRatio = CornerRadiusRatio,
                Count = count,
                Laps = laps
            };

            if (FrameGeometries.TryGetValue(key, out var cached) && cached.Length == layerCount)
            {
                return cached;
            }

            var written = RayArrowLayout.BuildSpines(mapped, laps, BurstScale, count, _spines);
            if (written <= 0)
            {
                return null;
            }

            var geometries = new StreamGeometry[layerCount];
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            var palette = EnsurePalette(persisted);
            for (var i = 0; i < layerCount; i++)
            {
                geometries[i] = BuildLayerGeometry(written, palette.Layers[i]);
            }

            FrameGeometries[key] = geometries;
            return geometries;
        }

        private StreamGeometry BuildLayerGeometry(int count, RarityAppearanceHelper.RayGlowLayer layer)
        {
            RayArrowLayout.Emit(_spines, count, layer.WidthMultiplier, layer.HeightFraction, _quads);

            // One geometry for every arrow in the copy, so a copy costs a single draw however many
            // arrows it carries.
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var writer = geometry.Open())
            {
                for (var i = 0; i < count; i++)
                {
                    var quad = _quads[i];
                    writer.BeginFigure(quad.BaseLeft, true, true);
                    writer.LineTo(quad.TipLeft, false, false);
                    writer.LineTo(quad.TipRight, false, false);
                    writer.LineTo(quad.BaseRight, false, false);
                }
            }

            geometry.Freeze();
            return geometry;
        }

        private RayArrowLayout.MappedTrack EnsureMappedTrack()
        {
            // Arrange normally sets this, but an offscreen surface renders without assuming it ran.
            var slot = _arrangedSize.Width > 0 && _arrangedSize.Height > 0 ? _arrangedSize : RenderSize;
            if (_mapped != null && slot == _mappedSlot)
            {
                return _mapped;
            }

            var track = _track;
            if (track == null)
            {
                // With no subject named at all, the slot itself is the subject: the loop traces the
                // shape being decorated rather than a square floating inside it. That is what lets this
                // ring something that is not a picture — a notification card, say — without pretending
                // there is artwork to trace.
                //
                // With a subject named but not yet traced, a square stands in, so the effect appears
                // immediately and sharpens to the real silhouette when it arrives.
                var aspect = string.IsNullOrWhiteSpace(_subjectKey) && slot.Height > 0
                    ? slot.Width / slot.Height
                    : 1.0;

                track = RayTrack.RoundedRect(aspect, CornerRadiusRatio);
            }
            else if (track.IsAnalytic)
            {
                // A traced-away subject (an opaque rectangle) carries no rounding of its own, because the
                // radius belongs to the surface rather than to the image.
                track = RayTrack.RoundedRect(track.SourceAspect, CornerRadiusRatio);
            }

            _mapped = RayArrowLayout.Map(track, slot, SubjectInset);
            _mappedSlot = slot;
            return _mapped;
        }

        private RarityAppearanceHelper.RayGlowPalette EnsurePalette(PersistedSettings persisted)
        {
            var generation = RarityAppearanceHelper.RayGlowPaletteGeneration;
            if (_palette != null && _paletteGeneration == generation)
            {
                return _palette;
            }

            _paletteGeneration = generation;
            _palette = UseCompletedColors
                ? RarityAppearanceHelper.GetCompletedRayGlowPalette(persisted)
                : RarityAppearanceHelper.GetRayGlowPalette(Rarity, persisted);

            return _palette;
        }
    }
}
