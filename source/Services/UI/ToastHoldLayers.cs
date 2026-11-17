using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Cloaked windows that each show one layer of a resting wave, for a thumbnail presenter to
    /// draw with the wave's own window: the cards' backgrounds and the cards' content over it and,
    /// when a card shows rays, those rays under it, all rendered in software.
    ///
    /// Measured against a GPU-bound game with an animated background (tools/capture-harness
    /// README.md, "The thumbnail probe"): at normal GPU priority the whole card reached the screen
    /// about 10 times a second and a card without effects, rendered in software, at the video's
    /// full rate, at no cost to the game; at realtime priority the whole card cost the game about
    /// 9 fps of 102, of which about 3 remained without the card's effects. A software window issues
    /// no GPU work from this process, and a window that holds only the content is not redrawn by a
    /// background frame.
    ///
    /// Every window builds the wave's cards from the same view models and template as the wave's
    /// own window, with its content scale and margins, so every element lands on the same pixels.
    /// UI thread only.
    /// </summary>
    internal sealed class ToastHoldLayers : IDisposable
    {
        private const double SizeToleranceDip = 0.01;

        private readonly System.Diagnostics.Stopwatch _created = System.Diagnostics.Stopwatch.StartNew();

        private ToastHoldLayers(Window rays, Window background, Window content)
        {
            Rays = rays;
            Background = background;
            Content = content;
        }

        /// <summary>
        /// The rays around the cards alone, to be drawn under the wave's own window, or null when
        /// no card shows rays or they were left in that window.
        /// </summary>
        public Window Rays { get; }

        /// <summary>
        /// Whether the wave's own window draws nothing while the layers are shown, so no window of
        /// the wave issues GPU work.
        /// </summary>
        public bool MainIsStill { get; set; }

        /// <summary>
        /// The container of card <paramref name="index"/> in <paramref name="layer"/>, one of
        /// this object's windows, or null. It has the layout of the same card in the wave's own
        /// window.
        /// </summary>
        public FrameworkElement CardContainer(Window layer, int index)
        {
            if (layer == null)
            {
                return null;
            }

            if (!_surfaces.TryGetValue(layer, out var surface))
            {
                surface = FindSurface(layer.Content as DependencyObject);
                _surfaces[layer] = surface;
            }

            return surface?.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        }

        private readonly Dictionary<Window, ItemsControl> _surfaces = new Dictionary<Window, ItemsControl>();

        private static ItemsControl FindSurface(DependencyObject node)
        {
            if (node == null || node is ItemsControl)
            {
                return node as ItemsControl;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var found = FindSurface(VisualTreeHelper.GetChild(node, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Milliseconds since the windows were built.</summary>
        public long AgeMs => _created.ElapsedMilliseconds;

        /// <summary>
        /// Whether every window still has <paramref name="main"/>'s exact size, which the cards
        /// can change after the windows were built.
        /// </summary>
        public bool HasSizeOf(Window main)
        {
            return ToastWindowPlacer.TryGetPhysicalRect(main, out var mainRect) &&
                (Rays == null || HasSizeOf(Rays, main, mainRect)) &&
                HasSizeOf(Background, main, mainRect) && HasSizeOf(Content, main, mainRect);
        }

        /// <summary>The cards' backgrounds alone, rendered in software.</summary>
        public Window Background { get; }

        /// <summary>The cards' content alone, over nothing.</summary>
        public Window Content { get; }

        /// <summary>Whatever undoes the stand-downs applied to the wave's own window for the split.</summary>
        public IDisposable MainHold { get; set; }

        /// <summary>
        /// Builds and shows the windows, cloaked, or returns null when they cannot be built to the
        /// main window's exact size.
        /// </summary>
        public static ToastHoldLayers TryCreate(
            IPlayniteAPI api, Window main, ItemsControl mainSurface,
            IReadOnlyList<AchievementToastViewModel> cards, DataTemplate template, bool perMonitorWindow)
        {
            Window rays = null;
            Window background = null;
            Window content = null;
            try
            {
                if (main == null || mainSurface == null || cards == null || template == null ||
                    !ToastWindowPlacer.TryGetPhysicalRect(main, out var mainRect))
                {
                    return null;
                }

                background = Build(api, main, mainSurface, cards, template, perMonitorWindow, mainRect);
                content = Build(api, main, mainSurface, cards, template, perMonitorWindow, mainRect);
                var mainRays = ToastLayer.Find(mainSurface, ToastLayerRole.Rays);
                var needsRays = mainRays.Exists(burst => burst.IsVisible);
                if (needsRays)
                {
                    rays = Build(api, main, mainSurface, cards, template, perMonitorWindow, mainRect);
                }

                if (background == null || content == null || (needsRays && rays == null))
                {
                    Close(rays);
                    Close(background);
                    Close(content);
                    return null;
                }

                // A burst shown with the background continues the wave of the one it stands in for.
                var mainUnder = ToastLayer.Find(mainSurface, ToastLayerRole.Underlay);
                var layerUnder = ToastLayer.Find((DependencyObject)background.Content, ToastLayerRole.Underlay);
                for (var i = 0; i < layerUnder.Count && i < mainUnder.Count; i++)
                {
                    (layerUnder[i] as Views.Controls.RarityRayBurst)?.CopyEpochFrom(
                        mainUnder[i] as Views.Controls.RarityRayBurst);
                }

                ToastLayer.ShowOnlyBackground((DependencyObject)background.Content);
                ToastLayer.ShowOnlyContent((DependencyObject)content.Content);
                if (rays != null)
                {
                    // Each burst continues the wave of the one it stands in for.
                    var layerRays = ToastLayer.Find((DependencyObject)rays.Content, ToastLayerRole.Rays);
                    for (var i = 0; i < layerRays.Count && i < mainRays.Count; i++)
                    {
                        (layerRays[i] as Views.Controls.RarityRayBurst)?.CopyEpochFrom(
                            mainRays[i] as Views.Controls.RarityRayBurst);
                    }

                    ToastLayer.ShowOnly((DependencyObject)rays.Content, ToastLayerRole.Rays);
                }

                return new ToastHoldLayers(rays, background, content);
            }
            catch
            {
                Close(rays);
                Close(background);
                Close(content);
                return null;
            }
        }

        /// <summary>
        /// Whether both windows have every image <paramref name="mainSurface"/>, the wave's own
        /// cards, has.
        /// </summary>
        public bool IsLoadedLike(DependencyObject mainSurface)
        {
            var wanted = AsyncImage.LoadedCountUnder(mainSurface);
            return AsyncImage.LoadedCountUnder(Background) >= wanted &&
                AsyncImage.LoadedCountUnder(Content) >= wanted;
        }

        public void Dispose()
        {
            try
            {
                MainHold?.Dispose();
            }
            catch
            {
            }

            MainHold = null;
            Close(Rays);
            Close(Background);
            Close(Content);
        }

        private static Window Build(
            IPlayniteAPI api, Window main, ItemsControl mainSurface,
            IReadOnlyList<AchievementToastViewModel> cards, DataTemplate template,
            bool perMonitorWindow, System.Drawing.Rectangle mainRect)
        {
            var window = PlayniteUiProvider.CreateBorderlessTopmostWindow(api, main.Title);
            GlowAnimationGate.SetIsExempt(window, true);
            ToastWindowSurface.UseDwmComposition(window);
            window.Topmost = false;

            var surface = ToastSurfaceFactory.BuildToastSurface(cards, template);
            if (mainSurface.LayoutTransform is ScaleTransform scale)
            {
                surface.LayoutTransform = new ScaleTransform(scale.ScaleX, scale.ScaleY);
            }

            surface.Margin = mainSurface.Margin;
            window.Content = ToastSurfaceFactory.BuildSlideHost(surface, out _);

            using (perMonitorWindow ? Common.DpiAwarenessScope.PerMonitorV2() : null)
            {
                new WindowInteropHelper(window).EnsureHandle();
            }

            // Cloaked before and after Show, as the wave's own window is: DWM can compose a newly
            // shown window before a cloak set afterwards takes hold.
            ToastWindowSurface.SetCloaked(window, true);
            ToastWindowPlacer.MovePhysical(window, mainRect.X, mainRect.Y);
            window.Show();
            ToastWindowSurface.SetCloaked(window, true);
            // Software: WPF presents every window of the process from one render thread, so a
            // hardware frame that waits in the GPU queue holds back a software one beside it.
            if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget != null)
            {
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }

            window.UpdateLayout();
            ToastSurfaceFactory.ApplyMeasuredCardGaps(surface);
            window.UpdateLayout();

            // A layer of another size would be resampled onto the wave's rectangle.
            if (!HasSizeOf(window, main, mainRect))
            {
                Close(window);
                return null;
            }

            return window;
        }

        private static bool HasSizeOf(Window window, Window main, System.Drawing.Rectangle mainRect)
        {
            return Math.Abs(window.ActualWidth - main.ActualWidth) <= SizeToleranceDip &&
                Math.Abs(window.ActualHeight - main.ActualHeight) <= SizeToleranceDip &&
                ToastWindowPlacer.TryGetPhysicalRect(window, out var rect) &&
                rect.Width == mainRect.Width && rect.Height == mainRect.Height;
        }

        private static void Close(Window window)
        {
            try
            {
                window?.Close();
            }
            catch
            {
            }
        }
    }
}
