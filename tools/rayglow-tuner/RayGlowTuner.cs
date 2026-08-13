using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Controls.RayGlow;

namespace PlayniteAchievements.Tools.RayGlowTuner
{
    /// <summary>
    /// Live tuner for the rays glow.
    ///
    /// It draws through the plugin's own <see cref="RayTrackBuilder"/> and <see cref="RayArrowLayout"/>,
    /// compiled from source with their tuning constants unfrozen (see build.ps1), so what appears here
    /// is what the plugin will draw rather than a lookalike. Only the fill ladder and the draw loop are
    /// restated, because those live in files that drag in the whole plugin.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // "--snapshot <path> [laps]" renders one frame to a PNG and exits, so the tuner can be
            // checked without a person watching it.
            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                var laps = args.Length >= 3
                    ? double.Parse(args[2], CultureInfo.InvariantCulture)
                    : 0.0;
                Snapshot(args[1], laps);
                return;
            }

            var app = new Application();
            app.Run(new TunerWindow());
        }

        private static void Snapshot(string path, double laps)
        {
            // The preview element is rendered on its own rather than the whole window: a Window that has
            // never been shown has no composition of its own to capture, and comes out blank.
            const int width = 1100;
            const int height = 260;

            var preview = new RayPreview { Laps = laps };
            preview.Subjects.AddRange(Subjects.Default());

            preview.Measure(new Size(width, height));
            preview.Arrange(new Rect(0, 0, width, height));
            preview.UpdateLayout();

            // Let the dispatcher settle, or the first render can capture a half-built visual.
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(preview);
            Console.WriteLine(preview.Readout);

            // Build the real window too, so a snapshot run also proves the panel wires up. It is never
            // shown, which is why it cannot be the thing captured above.
            var window = new TunerWindow();
            window.Measure(new Size(1420, 900));
            window.Arrange(new Rect(0, 0, 1420, 900));
            window.UpdateLayout();
            Console.WriteLine("controls built ok");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            Console.WriteLine("wrote " + path);
        }
    }

    /// <summary>Everything the sliders drive that is not already a field on the layout itself.</summary>
    internal sealed class LadderSettings
    {
        // The shipped ladder is lifted out of the plugin at build time (see build.ps1), so the tuner
        // always opens on exactly what the plugin draws. Moving any softness slider switches to the
        // generated curve below, which cannot reproduce hand-tuned values — hence keeping both.
        private static readonly double[] ShippedWidths = ShippedLadder.Widths;
        private static readonly double[] ShippedHeights = ShippedLadder.Heights;
        private static readonly byte[] ShippedAlphas = ShippedLadder.Alphas;

        public bool Generated;

        public int LayerCount = ShippedLadder.Widths.Length;
        public double HaloWidth = ShippedLadder.Widths[0];
        public double CoreWidth = ShippedLadder.Widths[ShippedLadder.Widths.Length - 1];
        public double OuterAlpha = ShippedLadder.Alphas[0] / 255.0;
        public double CoreAlpha = ShippedLadder.Alphas[ShippedLadder.Alphas.Length - 1] / 255.0;
        public double ShortestHeightFraction = ShippedLadder.Heights[ShippedLadder.Heights.Length - 1];
        public double AlphaCurve = 2.1;
        public double WhiteBlend = 0.28;

        public Color OuterColor = Color.FromRgb(0x4F, 0xC3, 0xF7);
        public Color InnerColor = Color.FromRgb(0x4F, 0xC3, 0xF7);

        /// <summary>Copies of one ray, widest and faintest first.</summary>
        public List<Layer> Build()
        {
            var layers = new List<Layer>();

            if (!Generated)
            {
                for (var i = 0; i < ShippedWidths.Length; i++)
                {
                    var shade = Lerp(OuterColor, InnerColor, i / (double)(ShippedWidths.Length - 1));
                    if (i == ShippedWidths.Length - 1)
                    {
                        shade = Color.FromRgb(Toward(shade.R), Toward(shade.G), Toward(shade.B));
                    }

                    var solid = new SolidColorBrush(
                        Color.FromArgb(ShippedAlphas[i], shade.R, shade.G, shade.B));
                    solid.Freeze();
                    layers.Add(new Layer
                    {
                        Brush = solid,
                        Width = ShippedWidths[i],
                        Height = ShippedHeights[i],
                        Alpha = ShippedAlphas[i] / 255.0
                    });
                }

                return layers;
            }

            var count = Math.Max(1, LayerCount);
            var last = count - 1;

            for (var i = 0; i < count; i++)
            {
                var t = last > 0 ? i / (double)last : 1.0;
                var width = HaloWidth + ((CoreWidth - HaloWidth) * t);
                var height = 1.0 + ((ShortestHeightFraction - 1.0) * t);
                var alpha = OuterAlpha + ((CoreAlpha - OuterAlpha) * Math.Pow(t, AlphaCurve));

                var color = Lerp(OuterColor, InnerColor, t);
                if (i == last)
                {
                    color = Color.FromRgb(
                        Toward(color.R), Toward(color.G), Toward(color.B));
                }

                var brush = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(Clamp01(alpha) * 255.0), color.R, color.G, color.B));
                brush.Freeze();
                layers.Add(new Layer { Brush = brush, Width = width, Height = height, Alpha = alpha });
            }

            return layers;
        }

        private byte Toward(byte channel)
        {
            return (byte)(channel + ((255 - channel) * WhiteBlend));
        }

        private static Color Lerp(Color from, Color to, double amount)
        {
            return Color.FromRgb(
                (byte)(from.R + ((to.R - from.R) * amount)),
                (byte)(from.G + ((to.G - from.G) * amount)),
                (byte)(from.B + ((to.B - from.B) * amount)));
        }

        private static double Clamp01(double value)
        {
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }

        internal sealed class Layer
        {
            public SolidColorBrush Brush;
            public double Width;
            public double Height;
            public double Alpha;
        }
    }

    /// <summary>One thing to draw rays around: a silhouette, a slot to draw it in, and a caption.</summary>
    internal sealed class Subject
    {
        public string Name;
        public BitmapSource Bitmap;
        public RayTrack Track;
        public Size Slot;
        public double Inset;
        public double CornerRadiusRatio = 0.22;
        public double BurstScale = 1.55;

        public void Retrace()
        {
            var traced = RayTrackBuilder.Build(Bitmap);
            Track = traced.IsAnalytic
                ? RayTrack.RoundedRect(traced.SourceAspect, CornerRadiusRatio)
                : traced;
        }
    }

    /// <summary>
    /// Draws the rays itself and hosts the artwork as child elements.
    ///
    /// The border is a DropShadowEffect on a copy of the artwork, exactly as the plugin's templates do
    /// it, and an effect can only be applied to an element — DrawingContext.PushEffect has done nothing
    /// since .NET 4. So the artwork lives in child Images here rather than being drawn. A Panel renders
    /// its own content before its children, which puts the rays behind the picture without any extra
    /// arrangement.
    /// </summary>
    internal sealed class RayPreview : Canvas
    {
        private readonly RayArrowLayout.RayArrowSpine[] _spines = new RayArrowLayout.RayArrowSpine[256];
        private readonly RayArrowLayout.RayArrowQuad[] _quads = new RayArrowLayout.RayArrowQuad[256];
        private readonly Point[] _rimOuter = new Point[RayTrack.SampleCount];
        private readonly Point[] _rimInner = new Point[RayTrack.SampleCount];

        public List<Subject> Subjects = new List<Subject>();
        public LadderSettings Ladder = new LadderSettings();
        public double Laps;
        public bool ShowSubject = true;
        public bool ShowTrack;
        public Brush Backdrop = Brushes.Black;

        /// <summary>Blur radius of the edge, matching the ConverterParameter the templates pass.</summary>
        public double BorderBlur = 4.0;

        public bool ShowBorder = true;

        private readonly List<Rect> _artRects = new List<Rect>();

        /// <summary>Rebuilds the hosted artwork. Cheap, and only called when something it depends on
        /// moves — not per frame, unlike the ray drawing.</summary>
        public void Refresh()
        {
            Children.Clear();

            for (var i = 0; i < Subjects.Count && i < _artRects.Count; i++)
            {
                var subject = Subjects[i];
                if (subject.Bitmap == null)
                {
                    continue;
                }

                var rect = _artRects[i];

                if (ShowBorder && BorderBlur > 0)
                {
                    var glow = new DropShadowEffect
                    {
                        BlurRadius = BorderBlur,
                        ShadowDepth = 0,
                        Color = Ladder.InnerColor,
                        Opacity = 1.0
                    };

                    glow.Freeze();
                    Children.Add(Place(subject.Bitmap, rect, glow));
                }

                if (ShowSubject)
                {
                    Children.Add(Place(subject.Bitmap, rect, null));
                }
            }

            InvalidateVisual();
        }

        private static UIElement Place(BitmapSource bitmap, Rect rect, Effect effect)
        {
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Width = rect.Width,
                Height = rect.Height,
                Effect = effect,
                IsHitTestVisible = false
            };

            SetLeft(image, rect.X);
            SetTop(image, rect.Y);
            return image;
        }

        /// <summary>Set after a render so the panel can report what the numbers came out as.</summary>
        public string Readout { get; private set; } = string.Empty;

        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));

            var layers = Ladder.Build();
            var lines = new List<string>();
            var x = 40.0;
            var rectsChanged = false;
            var rectIndex = 0;

            foreach (var subject in Subjects)
            {
                if (subject.Track == null)
                {
                    continue;
                }

                var slot = subject.Slot;
                var top = Math.Max(40.0, (ActualHeight - slot.Height) / 2.0);
                context.PushTransform(new TranslateTransform(x, top));

                var mapped = RayArrowLayout.Map(subject.Track, slot, subject.Inset);
                if (mapped != null)
                {
                    var written = RayArrowLayout.BuildSpines(
                        mapped,
                        RayArrowLayout.ScaleLapsToTrack(Laps, mapped),
                        subject.BurstScale,
                        RayArrowLayout.ArrowCountFor(mapped),
                        _spines);
                    foreach (var layer in layers)
                    {
                        RayArrowLayout.Emit(_spines, written, layer.Width, layer.Height, _quads);

                        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
                        using (var writer = geometry.Open())
                        {
                            for (var i = 0; i < written; i++)
                            {
                                var quad = _quads[i];
                                writer.BeginFigure(quad.BaseLeft, true, true);
                                writer.LineTo(quad.TipLeft, false, false);
                                writer.LineTo(quad.TipRight, false, false);
                                writer.LineTo(quad.BaseRight, false, false);
                            }
                        }

                        geometry.Freeze();
                        context.DrawGeometry(layer.Brush, null, geometry);
                    }

                    // No border here. It is a DropShadowEffect on the artwork in the real templates, and
                    // DrawingContext cannot apply one — PushEffect has done nothing since .NET 4. An
                    // approximation drawn with geometry would only mislead, so the border is judged in
                    // Playnite, where it is the same machinery as the soft glow beside it.

                    if (ShowTrack)
                    {
                        var pen = new Pen(Brushes.Magenta, 1.0);
                        pen.Freeze();
                        var loop = new StreamGeometry();
                        using (var writer = loop.Open())
                        {
                            writer.BeginFigure(mapped.Points[0], false, true);
                            for (var i = 1; i < mapped.Points.Length; i++)
                            {
                                writer.LineTo(mapped.Points[i], true, false);
                            }
                        }

                        loop.Freeze();
                        context.DrawGeometry(null, pen, loop);
                    }

                    lines.Add(Describe(subject, mapped, written, layers));

                    // The artwork is a child element, not something drawn here, so its position has to
                    // be recorded in this element's own coordinates rather than the pushed transform's.
                    var art = new Rect(
                        x + mapped.ArtRect.X, top + mapped.ArtRect.Y,
                        mapped.ArtRect.Width, mapped.ArtRect.Height);

                    if (rectIndex >= _artRects.Count)
                    {
                        _artRects.Add(art);
                        rectsChanged = true;
                    }
                    else if (_artRects[rectIndex] != art)
                    {
                        _artRects[rectIndex] = art;
                        rectsChanged = true;
                    }

                    rectIndex++;
                }

                context.Pop();
                x += slot.Width + 90.0;
            }

            while (_artRects.Count > rectIndex)
            {
                _artRects.RemoveAt(_artRects.Count - 1);
                rectsChanged = true;
            }

            Readout = string.Join("\n", lines);

            if (rectsChanged)
            {
                // Never during this render pass: changing children mid-render is not allowed.
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded, new Action(Refresh));
            }
        }

        private string Describe(
            Subject subject, RayArrowLayout.MappedTrack mapped, int written, List<LadderSettings.Layer> layers)
        {
            double tallest = 0, shortest = double.MaxValue, widest = 0;
            for (var i = 0; i < written; i++)
            {
                tallest = Math.Max(tallest, _spines[i].Height);
                shortest = Math.Min(shortest, _spines[i].Height);
                widest = Math.Max(widest, _spines[i].HalfWidth);
            }

            var gap = mapped.Perimeter / Math.Max(1, written);
            var readable = 0.0;
            var halo = 0.0;
            foreach (var layer in layers)
            {
                halo = Math.Max(halo, 2 * widest * layer.Width);
                if (layer.Alpha >= 0.10)
                {
                    readable = Math.Max(readable, 2 * widest * layer.Width);
                }
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0,-14} reach {1,5:N1}-{2,-5:N1} slender {3,4:N1}:1  gap {4,5:N1}  readable {5,3:N0}%  halo {6,3:N0}%{7}",
                subject.Name, shortest, tallest, tallest / (2 * Math.Max(0.01, widest)), gap,
                100 * readable / gap, 100 * halo / gap,
                readable > gap ? "  RAYS RUN TOGETHER" : string.Empty);
        }
    }
}



