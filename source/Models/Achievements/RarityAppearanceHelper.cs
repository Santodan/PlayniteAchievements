using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Achievements
{
    public static class RarityAppearanceHelper
    {
        private static readonly Uri BadgeResourcesUri =
            new Uri("pack://application:,,,/PlayniteAchievements;component/Resources/RarityBadges.xaml", UriKind.Absolute);
        private static readonly Uri TrophyResourcesUri =
            new Uri("pack://application:,,,/PlayniteAchievements;component/Resources/TrophyBadges.xaml", UriKind.Absolute);
        private static readonly Lazy<ResourceDictionary> DefaultBadgeResources =
            new Lazy<ResourceDictionary>(CreateDefaultBadgeResources);
        private static readonly Lazy<ResourceDictionary> DefaultTrophyResources =
            new Lazy<ResourceDictionary>(CreateDefaultTrophyResources);

        public static event EventHandler AppearanceChanged;

        private static PersistedSettings _activeSettings;

        // Sunburst geometry for the rotating ray glow style, laid out in a 0-100 square with the
        // burst centered. Ray bases sit inside the icon's footprint so they stay hidden behind it
        // and the rays appear to emerge from around the icon. These are the visual tuning knobs.
        //
        // The radii are calibrated against the soft glow the rays sit behind, so the two read as one
        // effect. Half-angles are kept well under half the 360/RayCount spacing so the rays stay
        // separated rather than merging into a disc.
        //
        // One flat pass per ray, with a linear gradient along it. A second wider pass was tried to
        // soften the edges and is deliberately not here: it doubles the geometry drawn under a
        // continuously rotating layer, for every row on screen, every frame.
        private const int RayCount = 28;
        private const double RayBurstBox = 100.0;
        private const double RayBurstCenter = RayBurstBox / 2.0;

        // Rays begin well inside the subject and are hidden by it, so where each one appears to start is
        // decided by the subject's own silhouette rather than by any radius chosen here. That is the
        // only way the bases follow a square, a rounded icon, a circle and a cut-out alike — and the
        // only way they keep following it while the burst turns. Placing the bases on an outline of our
        // own instead made them correct in one position and wrong in every other, because the outline
        // rotated with the art while the subject stayed put.
        private const double RayOriginRadius = 6.0;
        private const double LongRayRadius = 50.0;
        private const double ShortRayRadius = 44.0;

        // Where the rays emerge for a square subject at the default RarityRayBurst.BurstScale, used to
        // place the fade so the visible part of a ray fades along its length. Rays are hidden inward of
        // this, so the exact value only shifts where the brightest point sits.
        private const double SubjectEdgeRadius = 26.0;

        private const double LongRayHalfAngle = 2.9;
        private const double ShortRayHalfAngle = 2.1;
        private const byte RayBaseAlpha = 0xCC;
        private const byte RayMidAlpha = 0x6E;

        // WPF on this target has no additive blend mode, so the long rays are blended toward white
        // to read as light rather than as paint in the flat tier color.
        private const double RayHighlightBlend = 0.35;


        private static readonly object RayBurstCacheLock = new object();
        private static readonly Dictionary<RarityTier, DrawingImage> RayBurstCache =
            new Dictionary<RarityTier, DrawingImage>();
        private static DrawingImage _completedRayBurstCache;

        public static Color GetBaseColor(RarityTier tier, PersistedSettings settings = null)
        {
            var persisted = settings ?? _activeSettings;
            return ParseTierColor(tier, persisted?.RarityColors);
        }

        public static SolidColorBrush GetBrush(RarityTier tier, PersistedSettings settings = null)
        {
            var brush = new SolidColorBrush(GetBaseColor(tier, settings));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        /// <summary>
        /// Glossy gradient brush in the rarity color for the Hardcore icon border. The corners
        /// stay at the rarity color and a bright highlight sweeps diagonally through the middle,
        /// giving a clean shine without the dark corners of the badge gradient.
        /// </summary>
        public static Brush GetShineBrush(RarityTier tier, PersistedSettings settings = null)
        {
            return CreateShineBrush(GetBaseColor(tier, settings));
        }

        private static Brush CreateShineBrush(Color baseColor)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            brush.GradientStops.Add(new GradientStop(baseColor, 0.00));
            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.White, 0.30), 0.35));
            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.White, 0.70), 0.50));
            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.White, 0.30), 0.65));
            brush.GradientStops.Add(new GradientStop(baseColor, 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        public static Color GetPieColor(RarityTier tier, PersistedSettings settings = null)
        {
            return GetBaseColor(tier, settings);
        }

        public static Color GetCompletedColor(PersistedSettings settings = null)
        {
            var persisted = settings ?? _activeSettings;
            if (UsesDefaultCompletedColors(persisted))
            {
                return GetCompletedEndColor(persisted);
            }

            return GetCompletedStartColor(persisted);
        }

        public static SolidColorBrush GetCompletedBrush(PersistedSettings settings = null)
        {
            return CreateSolidBrush(GetCompletedColor(settings));
        }

        public static void ApplyCompletedGameBrushResource(ResourceDictionary resources, PersistedSettings settings = null)
        {
            if (resources == null)
            {
                return;
            }

            resources["PlayAch.Brush.CompletedGame"] = GetCompletedBrush(settings);
        }

        /// <summary>
        /// Soft glow effect in the completed-game gradient start or end color for the
        /// game/category art completion glow. The two effects are offset toward opposite
        /// corners so stacked copies read as a two-tone bloom matching the diagonal of the
        /// completed badge gradient.
        /// </summary>
        public static DropShadowEffect GetCompletedGlow(bool useEndColor, PersistedSettings settings = null)
        {
            var effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = useEndColor ? 315 : 135,
                Color = useEndColor ? GetCompletedEndColor(settings) : GetCompletedStartColor(settings),
                Opacity = 1.0
            };

            if (effect.CanFreeze)
            {
                effect.Freeze();
            }

            return effect;
        }

        public static void ApplyCompletedGlowEffectResources(ResourceDictionary resources, PersistedSettings settings = null)
        {
            if (resources == null)
            {
                return;
            }

            resources["PlayAch.Effect.CompletedGlowStart"] = GetCompletedGlow(useEndColor: false, settings);
            resources["PlayAch.Effect.CompletedGlowEnd"] = GetCompletedGlow(useEndColor: true, settings);
        }

        /// <summary>
        /// Publishes the completed progress-bar fill so the progress-bar style can switch to
        /// the completed gradient via DynamicResource. The fill is always a plain
        /// CompletedStart -> CompletedEnd sweep (the badge's rainbow default and highlight
        /// band stay badge-only).
        /// </summary>
        public static void ApplyCompletedProgressFillResource(ResourceDictionary resources, PersistedSettings settings = null)
        {
            if (resources == null)
            {
                return;
            }

            var persisted = settings ?? _activeSettings;
            if (persisted?.ShowCompletedProgressColoring == false)
            {
                var normalFill = resources.Contains("PlayAch.Brush.Progress.Fill")
                    ? resources["PlayAch.Brush.Progress.Fill"]
                    : Application.Current?.TryFindResource("PlayAch.Brush.Progress.Fill");
                if (normalFill != null)
                {
                    resources["PlayAch.Brush.Progress.CompletedFill"] = normalFill;
                    return;
                }
            }

            resources["PlayAch.Brush.Progress.CompletedFill"] = CreateCompletedProgressFillBrush(settings);
        }

        public static Color GetCompletedStartColor(PersistedSettings settings = null)
        {
            var persisted = settings ?? _activeSettings;
            return ParseColor(
                persisted?.RarityColors?.CompletedStart,
                RarityColorSettings.DefaultCompletedStart);
        }

        public static Color GetCompletedEndColor(PersistedSettings settings = null)
        {
            var persisted = settings ?? _activeSettings;
            return ParseColor(
                persisted?.RarityColors?.CompletedEnd,
                RarityColorSettings.DefaultCompletedEnd);
        }

        public static Color GetTrophyColor(string trophyKey, PersistedSettings settings = null)
        {
            var persisted = settings ?? _activeSettings;
            var colors = persisted?.RarityColors;
            switch (trophyKey)
            {
                case "TrophyPlatinum":
                    return ParseColor(
                        colors?.TrophyPlatinum,
                        RarityColorSettings.DefaultTrophyPlatinum);
                case "TrophyGold":
                    return ParseColor(
                        colors?.TrophyGold,
                        RarityColorSettings.DefaultTrophyGold);
                case "TrophySilver":
                    return ParseColor(
                        colors?.TrophySilver,
                        RarityColorSettings.DefaultTrophySilver);
                default:
                    return ParseColor(
                        colors?.TrophyBronze,
                        RarityColorSettings.DefaultTrophyBronze);
            }
        }

        public static Color GetTrophyPieColor(string trophyKey, PersistedSettings settings = null)
        {
            return GetTrophyColor(trophyKey, settings);
        }

        /// <summary>
        /// Binds a control's AnimateRarityGlows dependency property to the single global setting so
        /// the rarity-glow pulse toggle reaches every glow surface without per-usage-site plumbing.
        /// The live PersistedSettings instance is mutated in place on save (and raises
        /// PropertyChanged), so this one-way binding tracks the toggle. No-op when the plugin
        /// instance is unavailable (design time, tests), leaving the DP at its default (true).
        /// </summary>
        public static void BindAnimateRarityGlows(FrameworkElement element, DependencyProperty property)
        {
            BindPersistedSetting(element, property, nameof(PersistedSettings.AnimateRarityGlows));
        }

        /// <summary>
        /// Binds a control's soft-glow tier selection to the global setting, so the per-tier choice
        /// reaches every glow surface the same way the pulse toggle does — and so changing it updates
        /// glows already on screen.
        /// </summary>
        public static void BindSoftGlowTiers(FrameworkElement element, DependencyProperty property)
        {
            BindPersistedSetting(element, property, nameof(PersistedSettings.RarityGlowSoftTiers));
        }

        /// <summary>Ray counterpart to <see cref="BindSoftGlowTiers"/>.</summary>
        public static void BindRayGlowTiers(FrameworkElement element, DependencyProperty property)
        {
            BindPersistedSetting(element, property, nameof(PersistedSettings.RarityGlowRayTiers));
        }

        /// <summary>
        /// Binds a control's ShowHardcoreBorder dependency property to the global setting, so turning
        /// the Hardcore border off hands those unlocks back to the normal glow everywhere at once.
        /// </summary>
        public static void BindShowHardcoreBorder(FrameworkElement element, DependencyProperty property)
        {
            BindPersistedSetting(element, property, nameof(PersistedSettings.ShowHardcoreBorder));
        }

        private static void BindPersistedSetting(
            FrameworkElement element,
            DependencyProperty property,
            string settingName)
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (element == null || property == null || persisted == null)
            {
                return;
            }

            element.SetBinding(property, new Binding(settingName)
            {
                Source = persisted,
                Mode = BindingMode.OneWay
            });
        }

        public static DropShadowEffect GetGlow(RarityTier tier, double blurRadius, PersistedSettings settings = null)
        {
            if (tier == RarityTier.Common)
            {
                return null;
            }

            var color = GetBaseColor(tier, settings);
            var effect = new DropShadowEffect
            {
                BlurRadius = blurRadius,
                ShadowDepth = 0,
                Color = color,
                Opacity = 1.0
            };

            if (effect.CanFreeze)
            {
                effect.Freeze();
            }

            return effect;
        }

        /// <summary>
        /// Frozen sunburst art for the rotating ray glow style, in the tier color. The rays are the
        /// same radial graphic for every tier; only the color changes, so the result is cached per
        /// tier (the cache is bypassed when an explicit settings instance is supplied, as the
        /// settings mockups do, and cleared whenever appearance settings are applied).
        ///
        /// Common returns null, matching <see cref="GetGlow"/>, so the lowest tier stays glowless.
        /// </summary>
        public static DrawingImage GetRayBurstImage(RarityTier tier, PersistedSettings settings = null)
        {
            if (tier == RarityTier.Common)
            {
                return null;
            }

            if (settings == null)
            {
                lock (RayBurstCacheLock)
                {
                    if (RayBurstCache.TryGetValue(tier, out var cached))
                    {
                        return cached;
                    }
                }
            }

            var baseColor = GetBaseColor(tier, settings);
            var image = CreateRayBurstImage(Blend(baseColor, Colors.White, RayHighlightBlend), baseColor);

            if (settings == null)
            {
                lock (RayBurstCacheLock)
                {
                    RayBurstCache[tier] = image;
                }
            }

            return image;
        }

        /// <summary>
        /// Sunburst art for the completion glow, alternating the completed gradient's end and start
        /// colors between long and short rays so the burst reads as the same two-tone bloom as the
        /// stacked <see cref="GetCompletedGlow"/> pair.
        /// </summary>
        public static DrawingImage GetCompletedRayBurstImage(PersistedSettings settings = null)
        {
            if (settings == null)
            {
                lock (RayBurstCacheLock)
                {
                    if (_completedRayBurstCache != null)
                    {
                        return _completedRayBurstCache;
                    }
                }
            }

            var image = CreateRayBurstImage(
                Blend(GetCompletedEndColor(settings), Colors.White, RayHighlightBlend),
                GetCompletedStartColor(settings));

            if (settings == null)
            {
                lock (RayBurstCacheLock)
                {
                    _completedRayBurstCache = image;
                }
            }

            return image;
        }

        /// <summary>
        /// Builds the sunburst as vector wedges rather than a blurred bitmap: each ray is a triangle
        /// filled with a gradient fading to fully transparent at its tip, so the art freezes once and
        /// the rotating layer costs a transform instead of re-rasterizing a blur every frame.
        /// Alternating long and short rays give the burst its cadence, a central bloom seats the icon
        /// in light rather than on top of bare spokes, and a radial opacity mask fades the whole
        /// burst out so it never ends on a hard circular edge.
        /// </summary>
        private static DrawingImage CreateRayBurstImage(Color longRayColor, Color shortRayColor)
        {
            var group = new DrawingGroup();

            // Transparent bounds rectangle pins the drawing's extent to the full square, so the
            // relative-mapped opacity mask below and the consuming Stretch stay predictable
            // regardless of how far individual rays reach.
            group.Children.Add(new GeometryDrawing
            {
                Geometry = new RectangleGeometry(new Rect(0, 0, RayBurstBox, RayBurstBox)),
                Brush = Brushes.Transparent
            });

            group.Children.Add(new GeometryDrawing
            {
                Geometry = new EllipseGeometry(
                    new Point(RayBurstCenter, RayBurstCenter),
                    RayBurstCenter,
                    RayBurstCenter),
                Brush = CreateRayBloomBrush(longRayColor)
            });

            // One merged geometry per length, filled by one brush: every ray radiates from the same
            // center, so a single center-anchored radial gradient fades them all along their length
            // exactly as a gradient per ray would, without a drawing per ray.
            AddRayPass(group, longRayColor, LongRayRadius, LongRayHalfAngle, longRays: true);
            AddRayPass(group, shortRayColor, ShortRayRadius, ShortRayHalfAngle, longRays: false);

            group.OpacityMask = CreateRayFalloffBrush();

            var image = new DrawingImage(group);
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        /// <summary>
        /// Adds one length of ray — long or short — as exactly two drawings: a wide faint pass and a
        /// narrower bright core over it, which together read as a soft-edged ray. The pair stands in
        /// for lateral blur, which is not an option on a rotating layer.
        ///
        /// Every ray of a given length shares one merged geometry and one brush. Drawing them
        /// individually meant a separate gradient-filled path per ray, and since a rotating layer is
        /// re-tessellated every frame for every row on screen, that cost showed up as real lag in a
        /// full grid. Because all rays radiate from the same center, one center-anchored radial
        /// gradient fades them all along their length exactly as per-ray linear gradients did.
        /// </summary>
        private static void AddRayPass(
            DrawingGroup group,
            Color color,
            double outerRadius,
            double halfAngleDegrees,
            bool longRays)
        {
            group.Children.Add(new GeometryDrawing
            {
                Geometry = BuildRayGeometry(outerRadius, halfAngleDegrees, longRays),
                Brush = CreateRayFadeBrush(color, outerRadius)
            });
        }

        private static Geometry BuildRayGeometry(double outerRadius, double halfAngleDegrees, bool longRays)
        {
            // Nonzero, because every ray reaches in toward the center and they all overlap there. Under
            // the default EvenOdd those overlaps would cancel out into holes; Nonzero unions them, and
            // it is the overlapping region that the subject covers anyway.
            var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
            for (var i = 0; i < RayCount; i++)
            {
                // Alternating long and short rays give the burst its cadence.
                if ((i % 2 == 0) != longRays)
                {
                    continue;
                }

                var angle = i * (360.0 / RayCount);
                var figure = new PathFigure
                {
                    StartPoint = PolarPoint(angle - halfAngleDegrees, RayOriginRadius),
                    IsClosed = true,
                    IsFilled = true
                };
                figure.Segments.Add(new LineSegment(PolarPoint(angle, outerRadius), true));
                figure.Segments.Add(new LineSegment(PolarPoint(angle + halfAngleDegrees, RayOriginRadius), true));
                geometry.Figures.Add(figure);
            }

            if (geometry.CanFreeze)
            {
                geometry.Freeze();
            }

            return geometry;
        }

        /// <summary>
        /// One brush that fades every ray of a pass from its base to its tip. Anchored at the burst's
        /// center with an absolute radius matching that pass's ray length, so the fade lands on each
        /// ray's own tip; a pass whose rays are shorter gets its own brush rather than being cut off
        /// part-way through a longer pass's gradient.
        /// </summary>
        private static Brush CreateRayFadeBrush(Color color, double outerRadius)
        {
            var center = new Point(RayBurstCenter, RayBurstCenter);
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = outerRadius,
                RadiusY = outerRadius
            };

            // The inner half of every ray is hidden by the subject, so the fade has to be measured over
            // what is left rather than over the ray's whole length: full strength is held out past the
            // silhouette and only the last stretch fades. Fading from the silhouette outward instead
            // spent most of the visible ray at low alpha, which read as barely-there stubs.
            var edgeOffset = Math.Min(SubjectEdgeRadius / outerRadius, 0.9);
            var fadeStart = edgeOffset + ((1.0 - edgeOffset) * 0.35);
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, RayBaseAlpha), 0.0));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, RayBaseAlpha), fadeStart));
            brush.GradientStops.Add(new GradientStop(
                WithAlpha(color, RayMidAlpha),
                fadeStart + ((1.0 - fadeStart) * 0.5)));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x00), 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Brush CreateRayBloomBrush(Color color)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };

            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x3A), 0.00));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x1C), 0.42));
            brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x00), 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Brush CreateRayFalloffBrush()
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };

            // The opaque region reaches almost to the edge: each ray already fades to transparent at
            // its own tip, so this mask exists only to kill any hard edge at the burst boundary.
            // Bringing the falloff in closer double-fades the rays and visibly truncates them.
            brush.GradientStops.Add(new GradientStop(Colors.White, 0.00));
            brush.GradientStops.Add(new GradientStop(Colors.White, 0.96));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Point PolarPoint(double angleDegrees, double radius)
        {
            var radians = angleDegrees * Math.PI / 180.0;
            return new Point(
                RayBurstCenter + (radius * Math.Cos(radians)),
                RayBurstCenter + (radius * Math.Sin(radians)));
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        public static void ApplyBadgeApplicationResources(PersistedSettings settings)
        {
            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            void apply()
            {
                ApplyBadgeApplicationResources(app.Resources, settings);
            }

            var dispatcher = app.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(apply), DispatcherPriority.Normal);
                return;
            }

            apply();
        }

        public static void ApplyBadgeApplicationResources(ResourceDictionary resources, PersistedSettings settings)
        {
            if (resources == null)
            {
                return;
            }

            _activeSettings = settings;

            // Drop the cached sunbursts so the next resolve picks up recolored tiers. Consumers
            // re-resolve their art on AppearanceChanged below.
            lock (RayBurstCacheLock)
            {
                RayBurstCache.Clear();
                _completedRayBurstCache = null;
            }

            ApplyBadgeResources(resources, settings);

            AppearanceChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void ApplyBadgeResources(ResourceDictionary resources, PersistedSettings settings)
        {
            if (resources == null)
            {
                return;
            }

            ApplyGeneratedBadgeResources(resources, settings);
            ApplyBadgeAlias(resources, RarityTier.Common, settings?.UseUniformRarityBadges ?? false);
            ApplyBadgeAlias(resources, RarityTier.Uncommon, settings?.UseUniformRarityBadges ?? false);
            ApplyBadgeAlias(resources, RarityTier.Rare, settings?.UseUniformRarityBadges ?? false);
            ApplyBadgeAlias(resources, RarityTier.UltraRare, settings?.UseUniformRarityBadges ?? false);
        }

        public static ImageSource CreateBadgePreview(RarityTier tier, PersistedSettings settings)
        {
            var sourceKey = GetIconKey(tier, settings?.UseUniformRarityBadges ?? false);
            return CreateBadgeImage(tier, GetGeometryKeyForBadge(sourceKey), settings);
        }

        public static ImageSource CreateCompletedBadgePreview(PersistedSettings settings)
        {
            return CreateCompletedBadgeImage(settings);
        }

        public static ImageSource CreateTrophyPreview(string trophyKey, PersistedSettings settings)
        {
            return CreateTrophyImage(trophyKey, settings);
        }

        public static bool IsAppearanceSettingPropertyName(string propertyName)
        {
            return string.Equals(propertyName, nameof(PersistedSettings.RarityColors), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(PersistedSettings.UseUniformRarityBadges), StringComparison.Ordinal) ||
                   string.Equals(propertyName, nameof(PersistedSettings.UseTrophiesForRarity), StringComparison.Ordinal);
        }

        private static void ApplyGeneratedBadgeResources(ResourceDictionary resources, PersistedSettings settings)
        {
            SetGeneratedBadge(resources, RarityTier.Common, "BadgeBronzeTriangle");
            SetGeneratedBadge(resources, RarityTier.Common, "BadgeBronzeHexagon");
            SetGeneratedBadge(resources, RarityTier.Uncommon, "BadgeSilverSquare");
            SetGeneratedBadge(resources, RarityTier.Uncommon, "BadgeSilverHexagon");
            SetGeneratedBadge(resources, RarityTier.Rare, "BadgeGoldPentagon");
            SetGeneratedBadge(resources, RarityTier.Rare, "BadgeGoldHexagon");
            SetGeneratedBadge(resources, RarityTier.UltraRare, "BadgePlatinumHexagon");
            ApplyCompletedGameBrushResource(resources, settings);
            ApplyCompletedProgressFillResource(resources, settings);
            var completedBadge = CreateCompletedBadgeImage(settings);
            resources["BadgeCompletedGame"] = completedBadge;
            // Runtime-only alias with no static definition in RarityBadges.xaml, mirroring the
            // BadgeRarity* aliases. Controls that merge the default RarityBadges dictionary would
            // shadow "BadgeCompletedGame" with its static default; consuming this alias via
            // DynamicResource instead resolves the user-customized image at the application scope.
            resources["BadgeRarityCompleted"] = completedBadge;
            resources["TrophyBronze"] = CreateTrophyImage("TrophyBronze", settings);
            resources["TrophySilver"] = CreateTrophyImage("TrophySilver", settings);
            resources["TrophyGold"] = CreateTrophyImage("TrophyGold", settings);
            resources["TrophyPlatinum"] = CreateTrophyImage("TrophyPlatinum", settings);
            // Solid text brushes in the configured tier colors for rarity/trophy count labels
            // (e.g. the game summary progress-footer badges), consumed via DynamicResource.
            resources["PlayAch.Brush.Rarity.UltraRare"] = GetBrush(RarityTier.UltraRare, settings);
            resources["PlayAch.Brush.Rarity.Rare"] = GetBrush(RarityTier.Rare, settings);
            resources["PlayAch.Brush.Rarity.Uncommon"] = GetBrush(RarityTier.Uncommon, settings);
            resources["PlayAch.Brush.Rarity.Common"] = GetBrush(RarityTier.Common, settings);
            resources["PlayAch.Brush.Trophy.Platinum"] = CreateSolidBrush(GetTrophyColor("TrophyPlatinum", settings));
            resources["PlayAch.Brush.Trophy.Gold"] = CreateSolidBrush(GetTrophyColor("TrophyGold", settings));
            resources["PlayAch.Brush.Trophy.Silver"] = CreateSolidBrush(GetTrophyColor("TrophySilver", settings));
            resources["PlayAch.Brush.Trophy.Bronze"] = CreateSolidBrush(GetTrophyColor("TrophyBronze", settings));
            SetStaticScoreBadge(resources, "ScoreBadgeBronzeTriangle", "BadgeBronzeTriangle");
            SetStaticScoreBadge(resources, "ScoreBadgeBronzeHexagon", "BadgeBronzeHexagon");
            SetStaticScoreBadge(resources, "ScoreBadgeSilverSquare", "BadgeSilverSquare");
            SetStaticScoreBadge(resources, "ScoreBadgeSilverHexagon", "BadgeSilverHexagon");
            SetStaticScoreBadge(resources, "ScoreBadgeGoldPentagon", "BadgeGoldPentagon");
            SetStaticScoreBadge(resources, "ScoreBadgeGoldHexagon", "BadgeGoldHexagon");
            SetStaticScoreBadge(resources, "ScoreBadgePlatinumHexagon", "BadgePlatinumHexagon");
            SetStaticScoreBadge(resources, "ScoreBadgeCompletedGame", "BadgeCompletedGame");

            void SetGeneratedBadge(ResourceDictionary target, RarityTier tier, string badgeKey)
            {
                target[badgeKey] = CreateBadgeImage(tier, GetGeometryKeyForBadge(badgeKey), settings);
            }

            void SetStaticScoreBadge(ResourceDictionary target, string scoreBadgeKey, string defaultBadgeKey)
            {
                var image = TryGetDefaultImage(defaultBadgeKey);
                if (image != null)
                {
                    target[scoreBadgeKey] = image;
                }
            }
        }

        private static void ApplyDefaultBadgeResources(ResourceDictionary resources)
        {
            foreach (var key in new[]
            {
                "BadgeBronzeTriangle",
                "BadgeBronzeHexagon",
                "BadgeSilverSquare",
                "BadgeSilverHexagon",
                "BadgeGoldPentagon",
                "BadgeGoldHexagon",
                "BadgePlatinumHexagon",
                "BadgeCompletedGame",
                "ScoreBadgeBronzeTriangle",
                "ScoreBadgeBronzeHexagon",
                "ScoreBadgeSilverSquare",
                "ScoreBadgeSilverHexagon",
                "ScoreBadgeGoldPentagon",
                "ScoreBadgeGoldHexagon",
                "ScoreBadgePlatinumHexagon",
                "ScoreBadgeCompletedGame",
                "TrophyBronze",
                "TrophySilver",
                "TrophyGold",
                "TrophyPlatinum"
            })
            {
                var defaultKey = key.StartsWith("ScoreBadge", StringComparison.Ordinal)
                    ? key.Substring("Score".Length)
                    : key;
                var image = defaultKey.StartsWith("Trophy", StringComparison.Ordinal)
                    ? TryGetDefaultTrophyImage(key)
                    : TryGetDefaultImage(defaultKey);
                if (image != null)
                {
                    resources[key] = image;
                }
            }
        }

        private static DrawingImage CreateBadgeImage(RarityTier tier, string geometryKey, PersistedSettings settings)
        {
            var geometry = settings?.UseTrophiesForRarity == true
                ? (TryGetDefaultTrophyGeometry("GeoTrophy") ?? TryGetDefaultGeometry(geometryKey))
                : TryGetDefaultGeometry(geometryKey);
            if (geometry == null)
            {
                return TryGetDefaultImage(GetIconKey(tier, settings?.UseUniformRarityBadges ?? false)) as DrawingImage;
            }

            var drawingGroup = new DrawingGroup();
            var shapeDrawing = new GeometryDrawing
            {
                Geometry = geometry,
                Brush = CreateGradientBrush(GetBaseColor(tier, settings)),
                Pen = new Pen(CreateRimBrush(GetBaseColor(tier, settings)), 3)
                {
                    LineJoin = PenLineJoin.Round
                }
            };

            drawingGroup.Children.Add(shapeDrawing);
            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = TryGetDefaultBrush("ShineOverlay") ?? CreateShineOverlay()
            });

            var image = new DrawingImage(drawingGroup);
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        private static DrawingImage CreateCompletedBadgeImage(PersistedSettings settings)
        {
            var useTrophy = settings?.UseTrophiesForRarity == true;
            var geometry = useTrophy
                ? (TryGetDefaultTrophyGeometry("GeoTrophy") ?? TryGetDefaultGeometry("GeoHexagon"))
                : TryGetDefaultGeometry("GeoHexagon");
            if (geometry == null)
            {
                return TryGetDefaultImage("BadgeCompletedGame") as DrawingImage;
            }

            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = CreateCompletedGradientBrush(settings),
                Pen = new Pen(CreateCompletedRimBrush(settings), 3)
                {
                    LineJoin = PenLineJoin.Round
                }
            });

            // The inset hexagon accent only reads correctly inside the hexagon badge; skip it
            // for the trophy silhouette so the completed badge matches the rarity trophies.
            if (!useTrophy)
            {
                var innerGeometry = Geometry.Parse("M 64,30 L 90,47 90,83 64,100 38,83 38,47 Z");
                if (innerGeometry.CanFreeze)
                {
                    innerGeometry.Freeze();
                }

                drawingGroup.Children.Add(new GeometryDrawing
                {
                    Geometry = innerGeometry,
                    Brush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                });
            }

            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = TryGetDefaultBrush("ShineOverlay") ?? CreateShineOverlay()
            });

            var image = new DrawingImage(drawingGroup);
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        private static DrawingImage CreateTrophyImage(string trophyKey, PersistedSettings settings)
        {
            var geometry = TryGetDefaultTrophyGeometry("GeoTrophy");
            if (geometry == null)
            {
                return TryGetDefaultTrophyImage(trophyKey) as DrawingImage;
            }

            var baseColor = GetTrophyColor(trophyKey, settings);
            var drawingGroup = new DrawingGroup();
            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = CreateGradientBrush(baseColor),
                Pen = new Pen(CreateRimBrush(baseColor), 3)
                {
                    LineJoin = PenLineJoin.Round
                }
            });

            if (string.Equals(trophyKey, "TrophyPlatinum", StringComparison.Ordinal))
            {
                drawingGroup.Children.Add(new GeometryDrawing
                {
                    Geometry = geometry,
                    Brush = CreateInnerGlowBrush(baseColor)
                });
            }

            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = CreateShineOverlay()
            });

            if (string.Equals(trophyKey, "TrophyPlatinum", StringComparison.Ordinal))
            {
                AddTrophySparkle(drawingGroup, "GeoTrophySparkle1", 0xBB);
                AddTrophySparkle(drawingGroup, "GeoTrophySparkle2", 0x99);
                AddTrophySparkle(drawingGroup, "GeoTrophySparkle3", 0x88);
                AddTrophySparkle(drawingGroup, "GeoTrophySparkle4", 0x77);
            }

            var image = new DrawingImage(drawingGroup);
            if (image.CanFreeze)
            {
                image.Freeze();
            }

            return image;
        }

        private static Brush CreateCompletedGradientBrush(PersistedSettings settings)
        {
            if (UsesDefaultCompletedColors(settings))
            {
                var original = TryGetDefaultBrush("FillRainbow");
                if (original != null)
                {
                    return original;
                }
            }

            var startColor = GetCompletedStartColor(settings);
            var endColor = GetCompletedEndColor(settings);

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            // Mirror CreateGradientBrush's bright highlight band at 0.55 so the
            // diagonal shine reads as strongly as the metal rarity badges, while
            // keeping the user's chosen start/end colors at the edges.
            brush.GradientStops.Add(new GradientStop(startColor, 0.00));
            brush.GradientStops.Add(new GradientStop(Blend(startColor, endColor, 0.35), 0.35));
            brush.GradientStops.Add(new GradientStop(Blend(Blend(startColor, endColor, 0.55), Colors.White, 0.72), 0.55));
            brush.GradientStops.Add(new GradientStop(endColor, 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        /// <summary>
        /// Plain horizontal CompletedStart -> CompletedEnd sweep for the progress-bar fill and
        /// border. Unlike the badge gradient there is no white highlight band, which reads as a
        /// stray bright patch on a thin bar.
        /// </summary>
        private static Brush CreateCompletedProgressFillBrush(PersistedSettings settings)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            brush.GradientStops.Add(new GradientStop(GetCompletedStartColor(settings), 0.0));
            brush.GradientStops.Add(new GradientStop(GetCompletedEndColor(settings), 1.0));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Brush CreateCompletedRimBrush(PersistedSettings settings)
        {
            if (UsesDefaultCompletedColors(settings))
            {
                var original = TryGetDefaultBrush("RimRainbow");
                if (original != null)
                {
                    return original;
                }
            }

            return CreateRimBrush(GetCompletedStartColor(settings));
        }

        private static bool UsesDefaultCompletedColors(PersistedSettings settings)
        {
            if (settings?.RarityColors == null)
            {
                return true;
            }

            return string.Equals(
                       NormalizeColorText(settings?.RarityColors?.CompletedStart),
                       NormalizeColorText(RarityColorSettings.DefaultCompletedStart),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       NormalizeColorText(settings?.RarityColors?.CompletedEnd),
                       NormalizeColorText(RarityColorSettings.DefaultCompletedEnd),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static LinearGradientBrush CreateGradientBrush(Color baseColor)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.Black, 0.62), 0.00));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.35));
            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.White, 0.72), 0.55));
            brush.GradientStops.Add(new GradientStop(Blend(baseColor, Colors.Black, 0.38), 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static SolidColorBrush CreateRimBrush(Color baseColor)
        {
            var color = Blend(baseColor, Colors.White, 0.78);
            color.A = 0xF2;
            return CreateSolidBrush(color);
        }

        private static SolidColorBrush CreateSolidBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Brush CreateShineOverlay()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };

            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.00));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static Brush CreateInnerGlowBrush(Color baseColor)
        {
            var glow = Blend(baseColor, Colors.White, 0.70);
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.4, 0.35),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, glow.R, glow.G, glow.B), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            return brush;
        }

        private static void AddTrophySparkle(DrawingGroup drawingGroup, string geometryKey, byte alpha)
        {
            var geometry = TryGetDefaultTrophyGeometry(geometryKey);
            if (geometry == null)
            {
                return;
            }

            drawingGroup.Children.Add(new GeometryDrawing
            {
                Geometry = geometry,
                Brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF))
            });
        }

        private static Color ParseTierColor(RarityTier tier, RarityColorSettings colors)
        {
            var value = tier switch
            {
                RarityTier.UltraRare => colors?.UltraRare,
                RarityTier.Rare => colors?.Rare,
                RarityTier.Uncommon => colors?.Uncommon,
                _ => colors?.Common
            };

            var fallback = tier switch
            {
                RarityTier.UltraRare => RarityColorSettings.DefaultUltraRare,
                RarityTier.Rare => RarityColorSettings.DefaultRare,
                RarityTier.Uncommon => RarityColorSettings.DefaultUncommon,
                _ => RarityColorSettings.DefaultCommon
            };

            return TryParseColor(value, out var color) ? color : (Color)ColorConverter.ConvertFromString(fallback);
        }

        private static Color ParseColor(string value, string fallback)
        {
            return TryParseColor(value, out var color)
                ? color
                : (Color)ColorConverter.ConvertFromString(fallback);
        }

        private static bool TryParseColor(string value, out Color color)
        {
            try
            {
                color = (Color)ColorConverter.ConvertFromString(value);
                return true;
            }
            catch
            {
                color = Colors.Transparent;
                return false;
            }
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                from.A,
                (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
                (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
                (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
        }

        private static string NormalizeColorText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static void ApplyBadgeAlias(ResourceDictionary resources, RarityTier tier, bool useUniformRarityBadges)
        {
            var source = TryGetResource(resources, GetIconKey(tier, useUniformRarityBadges)) as ImageSource;
            if (source != null)
            {
                resources[GetDynamicIconKey(tier)] = source;
            }
        }

        private static string GetIconKey(RarityTier tier, bool useUniformRarityBadges)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return "BadgePlatinumHexagon";
                case RarityTier.Rare:
                    return useUniformRarityBadges ? "BadgeGoldHexagon" : "BadgeGoldPentagon";
                case RarityTier.Uncommon:
                    return useUniformRarityBadges ? "BadgeSilverHexagon" : "BadgeSilverSquare";
                default:
                    return useUniformRarityBadges ? "BadgeBronzeHexagon" : "BadgeBronzeTriangle";
            }
        }

        private static string GetDynamicIconKey(RarityTier tier)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return "BadgeRarityUltraRare";
                case RarityTier.Rare:
                    return "BadgeRarityRare";
                case RarityTier.Uncommon:
                    return "BadgeRarityUncommon";
                default:
                    return "BadgeRarityCommon";
            }
        }

        private static string GetGeometryKeyForBadge(string badgeKey)
        {
            switch (badgeKey)
            {
                case "BadgeBronzeTriangle":
                    return "GeoTriangle";
                case "BadgeSilverSquare":
                    return "GeoSquareDiamond";
                case "BadgeGoldPentagon":
                    return "GeoPentagon";
                default:
                    return "GeoHexagon";
            }
        }

        private static Geometry TryGetDefaultGeometry(string resourceKey)
        {
            return TryGetDefaultResource(resourceKey) as Geometry;
        }

        private static Brush TryGetDefaultBrush(string resourceKey)
        {
            return TryGetDefaultResource(resourceKey) as Brush;
        }

        private static ImageSource TryGetDefaultImage(string resourceKey)
        {
            return TryGetDefaultResource(resourceKey) as ImageSource;
        }

        private static Geometry TryGetDefaultTrophyGeometry(string resourceKey)
        {
            return TryGetDefaultTrophyResource(resourceKey) as Geometry;
        }

        private static ImageSource TryGetDefaultTrophyImage(string resourceKey)
        {
            return TryGetDefaultTrophyResource(resourceKey) as ImageSource;
        }

        private static object TryGetDefaultResource(string resourceKey)
        {
            return TryGetResource(DefaultBadgeResources.Value, resourceKey);
        }

        private static object TryGetDefaultTrophyResource(string resourceKey)
        {
            return TryGetResource(DefaultTrophyResources.Value, resourceKey);
        }

        private static object TryGetResource(ResourceDictionary resources, string resourceKey)
        {
            if (resources == null || string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            try
            {
                return resources[resourceKey];
            }
            catch
            {
                return null;
            }
        }

        private static ResourceDictionary CreateDefaultBadgeResources()
        {
            try
            {
                return new ResourceDictionary
                {
                    Source = BadgeResourcesUri
                };
            }
            catch
            {
                return new ResourceDictionary();
            }
        }

        private static ResourceDictionary CreateDefaultTrophyResources()
        {
            try
            {
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = BadgeResourcesUri
                });
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = TrophyResourcesUri
                });
                return resources;
            }
            catch
            {
                return new ResourceDictionary();
            }
        }
    }
}
