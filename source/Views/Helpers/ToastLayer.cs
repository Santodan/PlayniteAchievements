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

        /// <summary>
        /// The background as the user sees it: an image, or a live image with a tint over it.
        /// </summary>
        Background,

        /// <summary>
        /// The card's own surface, drawn inside a <see cref="Back"/> element as what casts its
        /// glow or shadow. It is the card's background where no image covers it.
        /// </summary>
        Surface,

        /// <summary>
        /// Content that animates and is drawn directly over the background, with nothing else of
        /// the content under it: an icon's rays and pulsing halo. While the card rests it is
        /// shown with the background, so its redraws do not reach the content's effects.
        /// </summary>
        Underlay,
    }

    /// <summary>
    /// Marks the layers of a notification card template, so the same template can be shown one layer
    /// at a time. While a card rests, an animated background redraws everything drawn over it, and
    /// WPF re-runs every shadow effect in a redrawn region; with the background and the content each
    /// in a window of their own, a background frame redraws only the background
    /// (tools/capture-harness/README.md, "The thumbnail probe"). A card with a still background
    /// is split as well: its rays, glow pulse and countdown then reach the screen without a
    /// frame from the card's own window.
    ///
    /// A card is split when the lowest thing its <see cref="ToastLayerRole.Surface"/> and
    /// <see cref="ToastLayerRole.Background"/> elements draw is opaque; a template that marks
    /// neither is never split.
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
            ShowOnly(root, ToastLayerRole.Background, ToastLayerRole.Surface, ToastLayerRole.Underlay);

            // The surface sits inside the back element, which stays shown as its ancestor. Its
            // glow or shadow belongs to the card's own window.
            foreach (var back in Find(root, ToastLayerRole.Back))
            {
                if (back.Visibility == Visibility.Visible)
                {
                    RarityGlowPulse.SetIsActive(back, false);
                    back.SetCurrentValue(UIElement.EffectProperty, null);
                }
            }
        }

        /// <summary>
        /// Leaves only the elements with one of <paramref name="roles"/> under
        /// <paramref name="root"/> drawn: every element that is neither one of them nor an
        /// ancestor of one is hidden, keeping its layout.
        /// </summary>
        public static void ShowOnly(DependencyObject root, params ToastLayerRole[] roles)
        {
            var backgrounds = new List<FrameworkElement>();
            foreach (var role in roles)
            {
                backgrounds.AddRange(Find(root, role));
            }
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

            QuietHidden(root, false);
        }

        /// <summary>
        /// Takes what still animates off every hidden element under <paramref name="root"/>: its
        /// glow pulse, and an image brush it paints a border or panel with. Either one on a
        /// hidden element still had its window redrawn, at every frame for the pulse and at each
        /// of the image's frames for the brush, and WPF ran every effect in the window again each
        /// time. In the content layer that was what kept a resting card below the display's rate
        /// (tools/capture-harness README.md, "The rays' cost").
        /// </summary>
        /// <summary>
        /// Takes the image brush off every hidden border and panel under <paramref name="root"/>
        /// and returns what puts them back. For the card's own window while its layers are shown:
        /// a hidden element painted with an image that changes, a live blurred background, would
        /// have the window redrawn at each change.
        /// </summary>
        public static Action ReleaseHiddenImages(DependencyObject root)
        {
            var taken = new List<KeyValuePair<FrameworkElement, Brush>>();
            CollectHiddenImages(root, false, taken);
            foreach (var pair in taken)
            {
                SetPaint(pair.Key, null);
            }

            return () =>
            {
                foreach (var pair in taken)
                {
                    SetPaint(pair.Key, pair.Value);
                }

                taken.Clear();
            };
        }

        private static void CollectHiddenImages(
            DependencyObject node, bool hidden, List<KeyValuePair<FrameworkElement, Brush>> found)
        {
            if (node == null)
            {
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var childHidden = hidden || (child is UIElement element && element.Visibility != Visibility.Visible);
                if (childHidden && child is FrameworkElement painted)
                {
                    var paint = (painted as Border)?.Background ?? (painted as Panel)?.Background;
                    if (paint is ImageBrush)
                    {
                        found.Add(new KeyValuePair<FrameworkElement, Brush>(painted, paint));
                    }
                }

                CollectHiddenImages(child, childHidden, found);
            }
        }

        private static void SetPaint(FrameworkElement element, Brush paint)
        {
            if (element is Border border)
            {
                border.SetCurrentValue(Border.BackgroundProperty, paint);
            }
            else if (element is Panel panel)
            {
                panel.SetCurrentValue(Panel.BackgroundProperty, paint);
            }
        }

        private static void QuietHidden(DependencyObject node, bool hidden)
        {
            if (node == null)
            {
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var childHidden = hidden || (child is UIElement element && element.Visibility != Visibility.Visible);
                if (childHidden)
                {
                    if (RarityGlowPulse.GetIsActive(child))
                    {
                        RarityGlowPulse.SetIsActive(child, false);
                    }

                    if (child is Border border && border.Background is ImageBrush)
                    {
                        border.SetCurrentValue(Border.BackgroundProperty, null);
                    }
                    else if (child is Panel panel && panel.Background is ImageBrush)
                    {
                        panel.SetCurrentValue(Panel.BackgroundProperty, null);
                    }
                }

                QuietHidden(child, childHidden);
            }
        }

        /// <summary>
        /// Leaves only the back elements under <paramref name="root"/> drawn, by hiding their
        /// siblings, and returns what shows those again. For the whole card while its background
        /// and content are shown from layers over it: what is left is redrawn by its own
        /// animation alone, without the content that was drawn over it. Rays are hidden too: a
        /// layer under the card shows them.
        /// </summary>
        public static Action ShowOnlyBack(DependencyObject root)
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
            foreach (var underlay in Find(root, ToastLayerRole.Underlay))
            {
                underlay.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Hidden);
            }

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

            QuietHidden(root, false);
        }

        /// <summary>
        /// Whether the cards under <paramref name="root"/> can rest as separate layers: in every
        /// card, the lowest of its shown surface and background elements that draws anything is
        /// opaque, a solid surface or an image that fills its element. While the layers take over
        /// from the card's own window and hand back to it, the background layer is drawn over that
        /// window's own copy, and the window's glow is dimmed as a whole under it; a translucent
        /// base would show both through.
        /// </summary>
        public static bool CanSplit(DependencyObject root)
        {
            return WhyNotSplit(root) == null;
        }

        /// <summary>
        /// Why the cards under <paramref name="root"/> cannot rest as separate layers, for a log
        /// line a template's author can act on, or null when they can (<see cref="CanSplit"/>).
        /// </summary>
        public static string WhyNotSplit(DependencyObject root)
        {
            var bases = new List<FrameworkElement>();
            CollectBases(root, bases);
            if (bases.Count == 0)
            {
                return "the template marks no element with ToastLayer.Role Surface or Background " +
                    "(controls:ToastCard carries the marks)";
            }

            var seen = new HashSet<DependencyObject>();
            var settled = new HashSet<DependencyObject>();
            foreach (var element in bases)
            {
                if (!element.IsVisible)
                {
                    continue;
                }

                var card = CardOf(element, root);
                seen.Add(card);
                if (settled.Contains(card))
                {
                    continue;
                }

                var paint = (element as Border)?.Background ?? (element as Panel)?.Background;
                if (DrawsNothing(paint))
                {
                    continue;
                }

                if (element.Opacity < 1d || !IsOpaque(paint))
                {
                    return "the lowest surface or background of a card is not opaque (" +
                        (string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name) +
                        ", " + GetRole(element) + ")";
                }

                settled.Add(card);
            }

            if (seen.Count == 0)
            {
                return "no marked surface or background is shown";
            }

            return settled.Count == seen.Count
                ? null
                : "the surface and backgrounds of a card draw nothing (a background image may not have loaded)";
        }

        // Surface and background elements in the order they are drawn.
        private static void CollectBases(DependencyObject node, List<FrameworkElement> found)
        {
            if (node == null)
            {
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement element &&
                    (GetRole(element) == ToastLayerRole.Surface || GetRole(element) == ToastLayerRole.Background))
                {
                    found.Add(element);
                }

                CollectBases(child, found);
            }
        }

        // The card an element belongs to: its outermost content presenter under the root.
        private static DependencyObject CardOf(DependencyObject element, DependencyObject root)
        {
            DependencyObject card = root;
            for (var node = VisualTreeHelper.GetParent(element); node != null && !ReferenceEquals(node, root);
                node = VisualTreeHelper.GetParent(node))
            {
                if (node is ContentPresenter)
                {
                    card = node;
                }
            }

            return card;
        }

        private static bool DrawsNothing(Brush paint)
        {
            return paint == null || paint.Opacity <= 0d ||
                (paint is SolidColorBrush solid && solid.Color.A == 0) ||
                (paint is ImageBrush image && image.ImageSource == null);
        }

        private static bool IsOpaque(Brush paint)
        {
            if (paint.Opacity < 1d)
            {
                return false;
            }

            if (paint is SolidColorBrush solid)
            {
                return solid.Color.A == 255;
            }

            return paint is ImageBrush image && image.ImageSource is BitmapSource source &&
                (image.Stretch == Stretch.UniformToFill || image.Stretch == Stretch.Fill) &&
                IsOpaque(source);
        }

        // Per bitmap: a still background is the same cached bitmap from one notification to the
        // next, and reading every pixel of it takes milliseconds.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, object> OpaqueByBitmap =
            new System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, object>();

        private static bool IsOpaque(BitmapSource source)
        {
            // A format without an alpha channel, as the live blurred capture's is.
            if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgr24 ||
                source.Format == PixelFormats.Rgb24)
            {
                return true;
            }

            if (OpaqueByBitmap.TryGetValue(source, out var known))
            {
                return (bool)known;
            }

            var opaque = ReadIsOpaque(source);
            try
            {
                OpaqueByBitmap.Add(source, opaque);
            }
            catch (ArgumentException)
            {
            }

            return opaque;
        }

        private static bool ReadIsOpaque(BitmapSource source)
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
