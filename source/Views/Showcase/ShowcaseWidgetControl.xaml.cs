using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    public partial class ShowcaseWidgetControl : UserControl
    {
        public static readonly DependencyProperty ProjectionProperty =
            DependencyProperty.Register(
                nameof(Projection),
                typeof(ShowcaseWidgetProjection),
                typeof(ShowcaseWidgetControl),
                new PropertyMetadata(null, OnProjectionChanged));

        private ShowcaseWidgetProjection _projection;
        private WidgetViewportState _viewport = WidgetViewportState.Classify(0, 0);
        private ShowcaseWidgetViewModelBase _bodyViewModel;

        // The live, appearance-recolored completion visuals exist at application scope; the
        // statically merged DesignTokens defaults shadow them for DynamicResource lookups inside
        // this tree, so they are mirrored locally the way GameSummariesGridControl does.
        private static readonly string[] MirroredAppearanceResourceKeys =
        {
            "PlayAch.Brush.CompletedGlowBloom",
            "PlayAch.Effect.CompletedGlowEdge"
        };

        // The glow-gating settings the game summaries grid exposes as ancestor DPs; the mosaic
        // tile template gates its completion glow layers on these the same way the grid's cover
        // cell does, so both surfaces honor the soft/ray tier selections and the pulse toggle.
        public static readonly DependencyProperty AnimateRarityGlowsProperty =
            DependencyProperty.Register(
                nameof(AnimateRarityGlows),
                typeof(bool),
                typeof(ShowcaseWidgetControl),
                new PropertyMetadata(false));

        public bool AnimateRarityGlows
        {
            get => (bool)GetValue(AnimateRarityGlowsProperty);
            set => SetValue(AnimateRarityGlowsProperty, value);
        }

        public static readonly DependencyProperty SoftGlowTiersProperty =
            DependencyProperty.Register(
                nameof(SoftGlowTiers),
                typeof(PlayniteAchievements.Models.Achievements.RaritySelection),
                typeof(ShowcaseWidgetControl),
                new PropertyMetadata(PlayniteAchievements.Models.Achievements.RaritySelection.None));

        public PlayniteAchievements.Models.Achievements.RaritySelection SoftGlowTiers
        {
            get => (PlayniteAchievements.Models.Achievements.RaritySelection)GetValue(SoftGlowTiersProperty);
            set => SetValue(SoftGlowTiersProperty, value);
        }

        public static readonly DependencyProperty RayGlowTiersProperty =
            DependencyProperty.Register(
                nameof(RayGlowTiers),
                typeof(PlayniteAchievements.Models.Achievements.RaritySelection),
                typeof(ShowcaseWidgetControl),
                new PropertyMetadata(PlayniteAchievements.Models.Achievements.RaritySelection.None));

        public PlayniteAchievements.Models.Achievements.RaritySelection RayGlowTiers
        {
            get => (PlayniteAchievements.Models.Achievements.RaritySelection)GetValue(RayGlowTiersProperty);
            set => SetValue(RayGlowTiersProperty, value);
        }

        public ShowcaseWidgetControl()
        {
            InitializeComponent();
            // Edit mode makes the widget body inert through IsHitTestVisible (see
            // ShowcaseControl); a hosted slideshow also holds its current image while inert so
            // layout edits do not flip pictures mid-drag.
            IsHitTestVisibleChanged += OnHitTestVisibleChanged;
            PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                .BindAnimateRarityGlows(this, AnimateRarityGlowsProperty);
            PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                .BindSoftGlowTiers(this, SoftGlowTiersProperty);
            PlayniteAchievements.Models.Achievements.RarityAppearanceHelper
                .BindRayGlowTiers(this, RayGlowTiersProperty);
            // Widget hosts are re-parented across dashboard rebuilds and page switches, which
            // re-fires Loaded without a guaranteed intervening Unloaded. Without the hooked
            // guard each such Loaded would stack another handler on the static event and root
            // this control (and its projection/snapshot) permanently. Same convention as
            // OverviewHostControl / RarityRayBurst.
            Loaded += (_, __) =>
            {
                if (!_appearanceHooked)
                {
                    _appearanceHooked = true;
                    PlayniteAchievements.Models.Achievements.RarityAppearanceHelper.AppearanceChanged +=
                        OnAppearanceChanged;
                }

                MirrorAppearanceResources();
            };
            Unloaded += (_, __) =>
            {
                if (_appearanceHooked)
                {
                    _appearanceHooked = false;
                    PlayniteAchievements.Models.Achievements.RarityAppearanceHelper.AppearanceChanged -=
                        OnAppearanceChanged;
                }
            };
        }

        private bool _appearanceHooked;

        private void OnAppearanceChanged(object sender, EventArgs e)
        {
            Dispatcher?.BeginInvoke(new Action(MirrorAppearanceResources));
        }

        private void MirrorAppearanceResources()
        {
            foreach (var key in MirroredAppearanceResourceKeys)
            {
                try
                {
                    var resource = Application.Current?.TryFindResource(key);
                    if (resource != null)
                    {
                        Resources[key] = resource;
                    }
                }
                catch
                {
                    // Keep the static fallback resources if application resources are unavailable.
                }
            }
        }

        private void OnHitTestVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (BodyHost?.Content is ScreenshotSlideshowControl slideshow)
            {
                slideshow.SetEditHold(!(bool)e.NewValue);
            }
        }

        public ShowcaseWidgetProjection Projection
        {
            get => (ShowcaseWidgetProjection)GetValue(ProjectionProperty);
            set => SetValue(ProjectionProperty, value);
        }

        public void Apply(ShowcaseWidgetProjection projection)
        {
            Projection = projection;
        }

        private static void OnProjectionChanged(
            DependencyObject sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is ShowcaseWidgetControl control))
            {
                return;
            }

            control._projection = e.NewValue as ShowcaseWidgetProjection;
            control.UpdateTitle();
            control.RebuildBody();
        }

        // PlayAch.Radius.Section (8) less PlayAch.Thickness.Border (1): the border's inner curve.
        private const double InnerCornerRadius = 7;

        private void OnRootContentSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RootContent.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(e.NewSize),
                InnerCornerRadius,
                InnerCornerRadius);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var next = WidgetViewportState.Classify(e.NewSize.Width, e.NewSize.Height);
            if (next.Density == _viewport.Density &&
                next.Orientation == _viewport.Orientation)
            {
                return;
            }

            _viewport = next;
            RebuildBody();
        }

        private void UpdateTitle()
        {
            // The header only appears when the user gave the widget a custom title;
            // widgets are otherwise chrome-free at every density (large StartPage-hosted
            // widgets used to auto-show the kind name at expanded density).
            var custom = _projection?.Instance?.CustomTitle?.Trim();
            if (string.IsNullOrWhiteSpace(custom))
            {
                TitleText.Text = string.Empty;
                GlyphText.Text = string.Empty;
                HeaderBorder.Visibility = Visibility.Collapsed;
                return;
            }

            TitleText.Text = custom;
            GlyphText.Text = GetWidgetGlyph(_projection.Instance.Kind);
            HeaderBorder.Visibility = Visibility.Visible;
        }

        private void RebuildBody()
        {
            // A full-bleed profile takes the whole card; its view model reapplies the same inset
            // to the foreground so only the background reaches the edge.
            var fullBleed = _projection?.Instance?.Kind == ShowcaseWidgetKind.Profile &&
                            ShowcaseWidgetOptions.GetProfileFullBleed(_projection.Instance);
            BodyHost.Margin = fullBleed
                ? new Thickness(0)
                : new Thickness(ShowcaseWidgetViewModelBase.GetBodyInset(_viewport.Density));
            if (_projection?.Instance == null)
            {
                SetBodyContent(CreateEmptyText());
                return;
            }

            switch (_projection.Instance.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    SetBodyContent(UpdateBodyViewModel<ProfileWidgetViewModel>());
                    break;
                case ShowcaseWidgetKind.Scores:
                    SetBodyContent(UpdateBodyViewModel<ScoresWidgetViewModel>());
                    break;
                case ShowcaseWidgetKind.Pie:
                    SetBodyContent(UpdateBodyViewModel<PieWidgetViewModel>());
                    break;
                case ShowcaseWidgetKind.Timeline:
                    SetBodyContent(UpdateBodyViewModel<TimelineWidgetViewModel>());
                    break;
                case ShowcaseWidgetKind.Statistics:
                    SetBodyContent(UpdateBodyViewModel<StatisticsWidgetViewModel>());
                    break;
                case ShowcaseWidgetKind.NativePoints:
                    SetBodyContent(_projection.ChartEntries?.Count > 0
                        ? (object)UpdateBodyViewModel<NativePointsWidgetViewModel>()
                        : CreateEmptyText());
                    break;
                case ShowcaseWidgetKind.IconMosaic:
                    // The collapsed Mosaic renders achievement icons or game covers per its
                    // Content option; UpdateBodyViewModel swaps the view model type when the
                    // option changes.
                    if (ShowcaseWidgetOptions.GetMosaicContent(_projection.Instance) ==
                        ShowcaseMosaicContent.Games)
                    {
                        SetBodyContent(_projection.Games?.Count > 0
                            ? (object)UpdateBodyViewModel<GameMosaicWidgetViewModel>()
                            : CreateEmptyText());
                    }
                    else
                    {
                        SetBodyContent(_projection.MosaicAchievements?.Count > 0
                            ? (object)UpdateBodyViewModel<IconMosaicWidgetViewModel>()
                            : CreateEmptyText());
                    }

                    break;
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                    // Unlike the view-model widgets above, the slideshow carries playback state
                    // (order, position, timer phase); reuse it across projection re-applies so
                    // edits elsewhere on the dashboard do not reset or reshuffle it.
                    if (BodyHost.Content is ScreenshotSlideshowControl slideshow &&
                        slideshow.IsFor(_projection.Instance))
                    {
                        slideshow.RefreshOptions();
                    }
                    else
                    {
                        slideshow = new ScreenshotSlideshowControl(_projection.Instance);
                        SetBodyContent(slideshow);
                    }

                    // Both branches, so a refresh reaches a reused control too: these rows are what
                    // the info panel resolves captures against.
                    slideshow.SetAchievementRows(_projection.Snapshot?.Achievements, _projection.Snapshot);
                    slideshow.SetEditHold(!IsHitTestVisible);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    // The collapsed Achievements Grid: the pinned source rides the pinned
                    // view model for reorder support and placeholder rows.
                    if (ShowcaseWidgetOptions.GetAchievementGridSource(_projection.Instance) ==
                        ShowcaseAchievementGridSource.Pinned)
                    {
                        SetBodyContent(_projection.AchievementRows?.Count > 0
                            ? (object)UpdateBodyViewModel<PinnedAchievementsWidgetViewModel>()
                            : CreateEmptyText(Localize("LOCPlayAch_Showcase_NoPinnedAchievements")));
                    }
                    else
                    {
                        SetBodyContent(_projection.AchievementRows?.Count > 0
                            ? (object)UpdateBodyViewModel<RecentAchievementsWidgetViewModel>()
                            : CreateEmptyText());
                    }

                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    // The collapsed Game Summaries Grid: pinned/favorites sources ride the
                    // favorites view model for pin reorder support.
                    if (ShowcaseWidgetOptions.GetGameGridSource(_projection.Instance) ==
                        ShowcaseGameGridSource.Library)
                    {
                        SetBodyContent(_projection.Games?.Count > 0
                            ? (object)UpdateBodyViewModel<GameSummariesWidgetViewModel>()
                            : CreateEmptyText());
                    }
                    else
                    {
                        SetBodyContent(_projection.Games?.Count > 0
                            ? (object)UpdateBodyViewModel<FavoriteGamesWidgetViewModel>()
                            : CreateEmptyText(Localize("LOCPlayAch_Showcase_NoFavoriteGames")));
                    }

                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    SetBodyContent(UpdateBodyViewModel<ActivityCalendarWidgetViewModel>());
                    break;
                default:
                    SetBodyContent(CreateEmptyText());
                    break;
            }
        }

        // Swapping BodyHost.Content discards the previous content's template-generated
        // visuals. The grid hosts subscribe the app-lifetime PersistedSettings and only
        // unhook in Dispose (they cannot self-dispose on Unloaded: widget hosts are
        // re-parented across dashboard rebuilds and the inner grids would not fully
        // re-attach), so a discarded body must be disposed here or every empty/non-empty
        // transition permanently roots another grid. The slideshow owns a timer and a
        // CapturesChanged subscription; same rule.
        private void SetBodyContent(object next)
        {
            var current = BodyHost.Content;
            if (ReferenceEquals(current, next))
            {
                return;
            }

            DisposeBodyVisuals(current);
            BodyHost.Content = next;
        }

        private void DisposeBodyVisuals(object current)
        {
            if (current == null)
            {
                return;
            }

            if (current is ScreenshotSlideshowControl slideshow)
            {
                slideshow.Dispose();
                return;
            }

            foreach (var host in VisualTreeHelpers.FindVisualChildren<ShowcaseGridHostBase>(BodyHost))
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// Releases the body's disposable visuals. For widget hosts being discarded for good
        /// (pruned instances, dashboard teardown); re-applying a projection afterwards
        /// rebuilds the body from scratch.
        /// </summary>
        public void DisposeBody()
        {
            DisposeBodyVisuals(BodyHost.Content);
            BodyHost.Content = null;
            (_bodyViewModel as IDisposable)?.Dispose();
            _bodyViewModel = null;
        }

        // Reuses (or lazily creates) the body view model for this control and feeds it the
        // current projection and viewport. Implicit templates in ShowcaseWidgetTemplates.xaml render
        // the returned view model. The type check replaces the view model when a collapsed kind's
        // source option switches it to the other mode's view model.
        private ShowcaseWidgetViewModelBase UpdateBodyViewModel<T>()
            where T : ShowcaseWidgetViewModelBase, new()
        {
            if (!(_bodyViewModel is T typed))
            {
                // The replaced view model may hold something rooted by a process-lifetime event
                // (the pie's chart is), so release it rather than dropping the reference.
                (_bodyViewModel as IDisposable)?.Dispose();
                typed = new T();
                _bodyViewModel = typed;
            }

            typed.Update(_projection, _viewport);
            return typed;
        }

        private TextBlock CreateEmptyText(string text = null)
        {
            return CreateText(
                text ?? Localize("LOCPlayAch_Showcase_NoData"),
                12,
                FontWeights.Normal,
                0.62);
        }

        private static TextBlock CreateText(
            string text,
            double fontSize,
            FontWeight weight,
            double opacity = 1)
        {
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = fontSize,
                FontWeight = weight,
                Opacity = opacity,
                TextWrapping = TextWrapping.Wrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            return block;
        }

        private static string GetWidgetGlyph(ShowcaseWidgetKind kind) =>
            ShowcaseWidgetCatalog.Get(kind).GlyphKey;
    }
}
