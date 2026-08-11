using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Controls.RayGlow;

namespace PlayniteAchievements.Tools.RayGlowTuner
{
    internal sealed class TunerWindow : Window
    {
        private readonly RayPreview _preview = new RayPreview();
        private readonly TextBlock _readout = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(10, 6, 10, 8)
        };

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Action> _resets = new List<Action>();
        private readonly List<Func<string>> _dump = new List<Func<string>>();

        private double _lapSeconds = 38.5;
        private bool _running = true;
        private double _pausedLaps;

        public TunerWindow()
        {
            Title = "Ray glow tuner";
            Width = 1420;
            Height = 900;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            BuildSubjects();

            var root = new DockPanel();
            root.Children.Add(BuildControls());
            DockPanel.SetDock(root.Children[0], Dock.Right);

            var centre = new DockPanel();
            var readoutHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                Child = _readout
            };
            centre.Children.Add(readoutHost);
            DockPanel.SetDock(readoutHost, Dock.Bottom);
            centre.Children.Add(_preview);
            root.Children.Add(centre);

            Content = root;

            CompositionTarget.Rendering += OnFrame;
            Closed += (s, e) => CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>Pins the animation for a snapshot, so a captured frame is reproducible.</summary>
        internal void SetPreviewLaps(double laps)
        {
            _running = false;
            _preview.Laps = laps;
            _preview.InvalidateVisual();
            _preview.UpdateLayout();
            _readout.Text = _preview.Readout;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (_running)
            {
                _preview.Laps = (_clock.Elapsed.TotalSeconds / Math.Max(0.5, _lapSeconds)) + _pausedLaps;
            }

            _preview.InvalidateVisual();
            _readout.Text = _preview.Readout;
        }

        private void BuildSubjects()
        {
            _preview.Subjects.AddRange(Subjects.Default());
        }

        private void RetraceAll()
        {
            foreach (var subject in _preview.Subjects)
            {
                subject.Retrace();
            }
        }

        private UIElement BuildControls()
        {
            var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };

            stack.Children.Add(Header("Motion"));
            stack.Children.Add(Slider("Lap seconds", 3, 90, _lapSeconds, "N1", v => _lapSeconds = v, null));
            stack.Children.Add(Field(
                "Envelope drift", -6, 6, RayArrowLayout.EnvelopeDriftRatio, "N2",
                v => RayArrowLayout.EnvelopeDriftRatio = v,
                () => "EnvelopeDriftRatio = " + F(RayArrowLayout.EnvelopeDriftRatio)));

            stack.Children.Add(Header("Arrows"));
            stack.Children.Add(Field(
                "Count", 4, 48, RayArrowLayout.DefaultArrowCount, "N0",
                v => RayArrowLayout.DefaultArrowCount = (int)Math.Round(v),
                () => "DefaultArrowCount = " + RayArrowLayout.DefaultArrowCount));
            stack.Children.Add(Field(
                "Burst scale", 1.0, 3.0, 1.55, "N2",
                v => { foreach (var s in _preview.Subjects) { s.BurstScale = v; } },
                null));
            stack.Children.Add(Field(
                "Min height", 0.05, 1.0, RayArrowLayout.MinHeightFraction, "N2",
                v => RayArrowLayout.MinHeightFraction = v,
                () => "MinHeightFraction = " + F(RayArrowLayout.MinHeightFraction)));
            stack.Children.Add(Field(
                "Slenderness", 0.03, 0.6, RayArrowLayout.SlendernessRatio, "N3",
                v => RayArrowLayout.SlendernessRatio = v,
                () => "SlendernessRatio = " + F(RayArrowLayout.SlendernessRatio)));
            stack.Children.Add(Field(
                "Max width / gap", 0.1, 1.0, RayArrowLayout.MaxWidthFraction, "N2",
                v => RayArrowLayout.MaxWidthFraction = v,
                () => "MaxWidthFraction = " + F(RayArrowLayout.MaxWidthFraction)));
            stack.Children.Add(Field(
                "Inward depth", 0.0, 0.6, RayArrowLayout.InwardFraction, "N2",
                v => RayArrowLayout.InwardFraction = v,
                () => "InwardFraction = " + F(RayArrowLayout.InwardFraction)));
            stack.Children.Add(Field(
                "Tip width", 0.0, 0.5, RayArrowLayout.TipWidthFraction, "N2",
                v => RayArrowLayout.TipWidthFraction = v,
                () => "TipWidthFraction = " + F(RayArrowLayout.TipWidthFraction)));

            stack.Children.Add(Header("Wave"));
            stack.Children.Add(Field(
                "Alternation", 0.0, 0.95, RayArrowLayout.AlternationAmplitude, "N2",
                v => RayArrowLayout.AlternationAmplitude = v,
                () => "AlternationAmplitude = " + F(RayArrowLayout.AlternationAmplitude)));
            stack.Children.Add(Field(
                "Lobes 1", 1, 13, RayArrowLayout.PrimaryLobes, "N0",
                v => RayArrowLayout.PrimaryLobes = (int)Math.Round(v),
                () => "PrimaryLobes = " + RayArrowLayout.PrimaryLobes));
            stack.Children.Add(Field(
                "Lobes 2", 1, 13, RayArrowLayout.SecondaryLobes, "N0",
                v => RayArrowLayout.SecondaryLobes = (int)Math.Round(v),
                () => "SecondaryLobes = " + RayArrowLayout.SecondaryLobes));
            stack.Children.Add(Field(
                "Lobes 3", 1, 13, RayArrowLayout.TertiaryLobes, "N0",
                v => RayArrowLayout.TertiaryLobes = (int)Math.Round(v),
                () => "TertiaryLobes = " + RayArrowLayout.TertiaryLobes));
            stack.Children.Add(Field(
                "Amp 1", 0.0, 1.0, RayArrowLayout.PrimaryAmplitude, "N2",
                v => RayArrowLayout.PrimaryAmplitude = v,
                () => "PrimaryAmplitude = " + F(RayArrowLayout.PrimaryAmplitude)));
            stack.Children.Add(Field(
                "Amp 2", 0.0, 1.0, RayArrowLayout.SecondaryAmplitude, "N2",
                v => RayArrowLayout.SecondaryAmplitude = v,
                () => "SecondaryAmplitude = " + F(RayArrowLayout.SecondaryAmplitude)));
            stack.Children.Add(Field(
                "Amp 3", 0.0, 1.0, RayArrowLayout.TertiaryAmplitude, "N2",
                v => RayArrowLayout.TertiaryAmplitude = v,
                () => "TertiaryAmplitude = " + F(RayArrowLayout.TertiaryAmplitude)));

            stack.Children.Add(Header("Softness"));
            stack.Children.Add(Field(
                "Copies", 2, 16, _preview.Ladder.LayerCount, "N0",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.LayerCount = (int)Math.Round(v); }, null));
            stack.Children.Add(Field(
                "Halo width", 0.5, 9.0, _preview.Ladder.HaloWidth, "N2",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.HaloWidth = v; }, null));
            stack.Children.Add(Field(
                "Core width", 0.05, 2.0, _preview.Ladder.CoreWidth, "N2",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.CoreWidth = v; }, null));
            stack.Children.Add(Field(
                "Outer alpha", 0.0, 0.4, _preview.Ladder.OuterAlpha, "N3",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.OuterAlpha = v; }, null));
            stack.Children.Add(Field(
                "Core alpha", 0.05, 1.0, _preview.Ladder.CoreAlpha, "N3",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.CoreAlpha = v; }, null));
            stack.Children.Add(Field(
                "Alpha curve", 0.3, 4.0, _preview.Ladder.AlphaCurve, "N2",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.AlphaCurve = v; }, null));
            stack.Children.Add(Field(
                "Shortest copy", 0.1, 1.0, _preview.Ladder.ShortestHeightFraction, "N2",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.ShortestHeightFraction = v; }, null));
            stack.Children.Add(Field(
                "White blend", 0.0, 1.0, _preview.Ladder.WhiteBlend, "N2",
                v => { _preview.Ladder.Generated = true; _preview.Ladder.WhiteBlend = v; }, null));

            stack.Children.Add(Header("Track"));
            stack.Children.Add(Field(
                "Smoothing passes", 0, 200, RayTrackBuilder.SmoothingPasses, "N0",
                v => { RayTrackBuilder.SmoothingPasses = (int)Math.Round(v); RetraceAll(); },
                () => "SmoothingPasses = " + RayTrackBuilder.SmoothingPasses));
            stack.Children.Add(Field(
                "Chaikin passes", 0, 6, RayTrackBuilder.ChaikinIterations, "N0",
                v => { RayTrackBuilder.ChaikinIterations = (int)Math.Round(v); RetraceAll(); },
                () => "ChaikinIterations = " + RayTrackBuilder.ChaikinIterations));
            stack.Children.Add(Field(
                "Corner radius", 0.0, 0.5, 0.22, "N2",
                v => { foreach (var s in _preview.Subjects) { s.CornerRadiusRatio = v; } RetraceAll(); },
                null));

            stack.Children.Add(Header("View"));
            stack.Children.Add(Toggle("Animate", true, v =>
            {
                if (!v)
                {
                    _pausedLaps = _preview.Laps;
                }
                else
                {
                    _pausedLaps = _preview.Laps;
                    _clock.Restart();
                }

                _running = v;
            }));
            stack.Children.Add(Toggle("Show artwork", true, v => _preview.ShowSubject = v));
            stack.Children.Add(Toggle("Show track", false, v => _preview.ShowTrack = v));
            stack.Children.Add(Toggle("Light backdrop", false,
                v => _preview.Backdrop = v ? Brushes.WhiteSmoke : Brushes.Black));

            stack.Children.Add(Button("Load artwork...", LoadArtwork));
            stack.Children.Add(Button("Copy values to clipboard", CopyValues));
            stack.Children.Add(Button("Reset to shipped values", () =>
            {
                foreach (var reset in _resets)
                {
                    reset();
                }

                RetraceAll();
            }));

            return new ScrollViewer
            {
                Width = 330,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Content = stack
            };
        }

        private void LoadArtwork()
        {
            // WinForms, because the WPF pickers still render pre-Vista on this framework version.
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Pick artwork to trace",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All files|*.*"
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 64;
                bitmap.UriSource = new Uri(dialog.FileName);
                bitmap.EndInit();
                bitmap.Freeze();

                var aspect = bitmap.PixelWidth / (double)bitmap.PixelHeight;
                var subject = new Subject
                {
                    Name = Path.GetFileNameWithoutExtension(dialog.FileName),
                    Bitmap = bitmap,
                    Slot = aspect >= 1
                        ? new Size(110, 110 / aspect)
                        : new Size(110 * aspect, 110),
                    CornerRadiusRatio = 0.18
                };

                subject.Retrace();
                _preview.Subjects.Add(subject);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read that image: " + ex.Message);
            }
        }

        private void CopyValues()
        {
            var text = new StringBuilder();
            text.AppendLine("// RayArrowLayout / RayTrackBuilder");
            foreach (var dump in _dump)
            {
                var line = dump();
                if (!string.IsNullOrEmpty(line))
                {
                    text.AppendLine("    " + line + ";");
                }
            }

            var ladder = _preview.Ladder.Build();
            text.AppendLine();
            text.AppendLine("// RarityAppearanceHelper.RayGlow ladder");
            text.Append("    RayLayerWidths  = { ");
            foreach (var layer in ladder)
            {
                text.Append(layer.Width.ToString("N2", CultureInfo.InvariantCulture) + ", ");
            }

            text.AppendLine("};");
            text.Append("    RayLayerHeights = { ");
            foreach (var layer in ladder)
            {
                text.Append(layer.Height.ToString("N2", CultureInfo.InvariantCulture) + ", ");
            }

            text.AppendLine("};");
            text.Append("    RayLayerAlphas  = { ");
            foreach (var layer in ladder)
            {
                text.Append("0x" + layer.Brush.Color.A.ToString("X2") + ", ");
            }

            text.AppendLine("};");

            try
            {
                Clipboard.SetText(text.ToString());
            }
            catch
            {
            }
        }

        private static string F(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private UIElement Header(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xC8, 0xFF)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Margin = new Thickness(0, 14, 0, 4)
            };
        }

        /// <summary>A slider that also records how to reset it and how to print it back out as code.</summary>
        private UIElement Field(
            string label, double min, double max, double initial, string format,
            Action<double> apply, Func<string> dump)
        {
            _resets.Add(() => apply(initial));
            if (dump != null)
            {
                _dump.Add(dump);
            }

            return Slider(label, min, max, initial, format, apply, dump);
        }

        private UIElement Slider(
            string label, double min, double max, double initial, string format,
            Action<double> apply, Func<string> dump)
        {
            var caption = new TextBlock
            {
                Foreground = Brushes.Gainsboro,
                FontSize = 11,
                Text = label + "  " + initial.ToString(format, CultureInfo.InvariantCulture)
            };

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = initial,
                SmallChange = (max - min) / 200.0,
                LargeChange = (max - min) / 20.0,
                Margin = new Thickness(0, 0, 0, 6)
            };

            if (format == "N0")
            {
                slider.IsSnapToTickEnabled = true;
                slider.TickFrequency = 1;
            }

            slider.ValueChanged += (s, e) =>
            {
                apply(e.NewValue);
                caption.Text = label + "  " + e.NewValue.ToString(format, CultureInfo.InvariantCulture);
            };

            var panel = new StackPanel();
            panel.Children.Add(caption);
            panel.Children.Add(slider);
            return panel;
        }

        private UIElement Toggle(string label, bool initial, Action<bool> apply)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = initial,
                Foreground = Brushes.Gainsboro,
                Margin = new Thickness(0, 2, 0, 2)
            };

            box.Checked += (s, e) => apply(true);
            box.Unchecked += (s, e) => apply(false);
            return box;
        }

        private UIElement Button(string label, Action click)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6) };
            button.Click += (s, e) => click();
            return button;
        }
    }

    /// <summary>The subjects the tuner starts with, shared with the snapshot path.</summary>
    internal static class Subjects
    {
        public static List<Subject> Default()
        {
            var subjects = new List<Subject>
            {
                new Subject
                {
                    Name = "icon 68px",
                    Bitmap = Shapes.RoundedSquare(64, 64),
                    Slot = new Size(72, 88),
                    Inset = 2,
                    CornerRadiusRatio = 0.22
                },
                new Subject
                {
                    Name = "cover 2:3",
                    Bitmap = Shapes.RoundedSquare(80, 120),
                    Slot = new Size(80, 120),
                    CornerRadiusRatio = 0.14
                },
                // Category summaries share the cover cell: a 96 wide column less its 8,6 margin. The
                // column resizes from 32 to 600, so the widened case is worth seeing next to it —
                // that is where a fixed arrow count has the most perimeter to spread over.
                new Subject
                {
                    Name = "category 80px",
                    Bitmap = Shapes.RoundedSquare(80, 76),
                    Slot = new Size(80, 76),
                    CornerRadiusRatio = 0.14
                },
                new Subject
                {
                    Name = "category wide",
                    Bitmap = Shapes.RoundedSquare(160, 90),
                    Slot = new Size(160, 90),
                    CornerRadiusRatio = 0.14
                },
                new Subject
                {
                    Name = "cutout",
                    Bitmap = Shapes.Cutout(64, 64),
                    Slot = new Size(72, 88),
                    Inset = 2,
                    CornerRadiusRatio = 0.22
                },
                new Subject
                {
                    Name = "compact 48px",
                    Bitmap = Shapes.RoundedSquare(48, 48),
                    Slot = new Size(48, 48),
                    CornerRadiusRatio = 0.22
                }
            };

            foreach (var subject in subjects)
            {
                subject.Retrace();
            }

            return subjects;
        }
    }

    /// <summary>Stand-in silhouettes, so the tuner is useful before any real artwork is loaded.</summary>
    internal static class Shapes
    {
        public static BitmapSource RoundedSquare(int width, int height)
        {
            var radius = Math.Min(width, height) * 0.18;
            return Create(width, height, (x, y) =>
            {
                var dx = Math.Max(0, Math.Max(radius - x, x - (width - 1 - radius)));
                var dy = Math.Max(0, Math.Max(radius - y, y - (height - 1 - radius)));
                return Math.Sqrt((dx * dx) + (dy * dy)) <= radius ? (byte)255 : (byte)0;
            });
        }

        public static BitmapSource Cutout(int width, int height)
        {
            var cx = (width - 1) * 0.5;
            var cy = (height - 1) * 0.5;
            return Create(width, height, (x, y) =>
            {
                var angle = Math.Atan2(y - cy, x - cx);
                var radius = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var limit = (width * 0.22) + (width * 0.19 * Math.Abs(Math.Cos(2.5 * angle)));
                return radius <= limit ? (byte)255 : (byte)0;
            });
        }

        private static BitmapSource Create(int width, int height, Func<int, int, byte> alphaAt)
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = (y * stride) + (x * 4);
                    var alpha = alphaAt(x, y);
                    pixels[index + 0] = (byte)(0x30 * alpha / 255);
                    pixels[index + 1] = (byte)(0x30 * alpha / 255);
                    pixels[index + 2] = (byte)(0x38 * alpha / 255);
                    pixels[index + 3] = alpha;
                }
            }

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}

