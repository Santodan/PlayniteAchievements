using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>The part of a notification card a template element belongs to.</summary>
    public enum ToastLayerRole
    {
        /// <summary>Content: text, icons and everything else drawn over the background.</summary>
        None,

        /// <summary>Drawn behind the background: the glow or shadow of the card.</summary>
        Back,

        /// <summary>Rays around the card, drawn behind its glow or shadow.</summary>
        Rays,

        /// <summary>The background image as the user sees it.</summary>
        Background,
    }

    /// <summary>
    /// Marks the layers of a notification card template, so the same template can be shown one layer
    /// at a time. While a card rests, an animated background redraws everything drawn over it, and
    /// WPF re-runs every shadow effect in a redrawn region; with the background and the content each
    /// in a window of their own, a background frame redraws only the background
    /// (tools/capture-harness/README.md, "The thumbnail probe").
    ///
    /// A template without a <see cref="ToastLayerRole.Background"/> element is never split.
    /// </summary>
    public static class ToastLayer
    {
        public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
            "Role", typeof(ToastLayerRole), typeof(ToastLayer), new PropertyMetadata(ToastLayerRole.None));

        public static ToastLayerRole GetRole(DependencyObject element)
        {
            return (ToastLayerRole)element.GetValue(RoleProperty);
        }

        public static void SetRole(DependencyObject element, ToastLayerRole value)
        {
            element.SetValue(RoleProperty, value);
        }

        /// <summary>Every element under <paramref name="root"/> with <paramref name="role"/>.</summary>
        public static List<FrameworkElement> Find(DependencyObject root, ToastLayerRole role)
        {
            var found = new List<FrameworkElement>();
            Collect(root, role, found);
            return found;
        }

        private static void Collect(DependencyObject node, ToastLayerRole role, List<FrameworkElement> found)
        {
            if (node == null)
            {
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement element && GetRole(element) == role)
                {
                    found.Add(element);
                }

                Collect(child, role, found);
            }
        }

        /// <summary>
        /// Leaves only the background elements under <paramref name="root"/> drawn.
        /// </summary>
        public static void ShowOnlyBackground(DependencyObject root)
        {
            ShowOnly(root, ToastLayerRole.Background);
        }

        /// <summary>
        /// Leaves only the elements with <paramref name="role"/> under <paramref name="root"/>
        /// drawn: every element that is neither one of them nor an ancestor of one is hidden,
        /// keeping its layout.
        /// </summary>
        public static void ShowOnly(DependencyObject root, ToastLayerRole role)
        {
            var backgrounds = Find(root, role);
            var kept = new HashSet<DependencyObject>();
            foreach (var background in backgrounds)
            {
                for (DependencyObject node = background; node != null && !ReferenceEquals(node, root);
                    node = VisualTreeHelper.GetParent(node))
                {
                    kept.Add(node);
                }
            }

            kept.Add(root);
            var leaves = new HashSet<DependencyObject>(backgrounds);
            foreach (var node in new List<DependencyObject>(kept))
            {
                if (leaves.Contains(node))
                {
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    // An element at zero opacity already draws nothing, and may be there to keep
                    // an image decoding: an animated image stops when its Image is not visible.
                    if (VisualTreeHelper.GetChild(node, i) is UIElement child && !kept.Contains(child) &&
                        child.Opacity > 0)
                    {
                        child.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                    }
                }
            }
        }

        /// <summary>
        /// Leaves only the back elements under <paramref name="root"/> drawn, by hiding their
        /// siblings, and returns what shows those again. For the whole card while its background
        /// and content are shown from layers over it: what is left is redrawn by its own
        /// animation alone, without the content that was drawn over it. Rays stay unless
        /// <paramref name="hideRays"/>, for when a layer under the card shows them instead.
        /// </summary>
        public static Action ShowOnlyBack(DependencyObject root, bool hideRays)
        {
            var hidden = new List<UIElement>();
            var parents = new HashSet<DependencyObject>();
            foreach (var back in Find(root, ToastLayerRole.Back))
            {
                var parent = VisualTreeHelper.GetParent(back);
                if (parent == null || !parents.Add(parent))
                {
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(parent);
                for (var i = 0; i < count; i++)
                {
                    if (VisualTreeHelper.GetChild(parent, i) is UIElement child &&
                        GetRole(child) != ToastLayerRole.Back &&
                        (hideRays || GetRole(child) != ToastLayerRole.Rays) &&
                        child.Opacity > 0 && child.Visibility == Visibility.Visible)
                    {
                        child.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                        hidden.Add(child);
                    }
                }
            }

            return () =>
            {
                foreach (var element in hidden)
                {
                    if (element.Visibility == Visibility.Hidden)
                    {
                        element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                    }
                }
            };
        }

        /// <summary>
        /// Leaves only the content under <paramref name="root"/> drawn: the back and background
        /// elements are hidden, and a background lets go of its image so the frames of that image
        /// no longer reach this window.
        /// </summary>
        public static void ShowOnlyContent(DependencyObject root)
        {
            foreach (var back in Find(root, ToastLayerRole.Back))
            {
                back.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            }

            foreach (var rays in Find(root, ToastLayerRole.Rays))
            {
                rays.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            }

            foreach (var background in Find(root, ToastLayerRole.Background))
            {
                background.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
                if (background is Border border)
                {
                    border.SetCurrentValue(Border.BackgroundProperty, null);
                }
            }
        }

        /// <summary>
        /// Whether the cards under <paramref name="root"/> can rest as separate layers: at least one
        /// shown background is animated, and every shown background is opaque in every pixel, since
        /// the background layer is drawn over the whole card, which carries its own copy.
        /// </summary>
        public static bool CanSplit(DependencyObject root)
        {
            var animated = false;
            foreach (var background in Find(root, ToastLayerRole.Background))
            {
                if (!background.IsVisible)
                {
                    continue;
                }

                var source = ((background as Border)?.Background as ImageBrush)?.ImageSource as BitmapSource;
                if (source == null || !IsOpaque(source))
                {
                    return false;
                }

                animated |= source is WriteableBitmap;
            }

            return animated;
        }

        private static bool IsOpaque(BitmapSource source)
        {
            try
            {
                var format = source.Format;
                if (format == PixelFormats.Bgr32 || format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24)
                {
                    return true;
                }

                BitmapSource pixelsOf = source;
                if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32)
                {
                    pixelsOf = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                }

                var stride = pixelsOf.PixelWidth * 4;
                var pixels = new byte[stride * pixelsOf.PixelHeight];
                pixelsOf.CopyPixels(pixels, stride, 0);
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 255)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
