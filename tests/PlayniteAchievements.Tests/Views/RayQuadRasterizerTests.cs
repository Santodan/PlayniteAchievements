using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Controls.RayGlow;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Holds the rasterizer to the picture WPF draws from the same quads: each copy one nonzero
    /// geometry of all its quads, filled with a translucent brush over the copies before it.
    /// </summary>
    [TestClass]
    public class RayQuadRasterizerTests
    {
        private static readonly double[] Widths = { 9.00, 7.28, 5.56, 3.84, 2.13, 0.41 };
        private static readonly double[] Heights = { 1.00, 0.82, 0.64, 0.46, 0.28, 0.10 };
        private static readonly byte[] Alphas = { 0x05, 0x09, 0x18, 0x35, 0x61, 0x9F };

        private const int Margin = 60;

        [DataTestMethod]
        [DataRow(550.0, 92.0, 0.0, 1.0)]
        [DataRow(550.0, 92.0, 3.37, 1.0)]
        [DataRow(550.0, 92.0, 0.61, 1.15)]
        [DataRow(550.0, 92.0, 1.9, 2.0)]
        [DataRow(55.0, 55.0, 0.25, 1.5)]
        public void Fill_MatchesTheGeometryWpfDraws(double slotWidth, double slotHeight, double laps, double scale)
        {
            var mapped = RayArrowLayout.Map(
                RayTrack.RoundedRect(slotWidth / slotHeight, 0.12), new Size(slotWidth, slotHeight), 0.0);
            var count = RayArrowLayout.ArrowCountFor(mapped);
            var spines = new RayArrowLayout.RayArrowSpine[count];
            var quads = new RayArrowLayout.RayArrowQuad[count];
            var written = RayArrowLayout.BuildSpines(mapped, laps, 1.55, count, spines);
            Assert.IsTrue(written > 0);

            var width = (int)Math.Ceiling((slotWidth + (2 * Margin)) * scale);
            var height = (int)Math.Ceiling((slotHeight + (2 * Margin)) * scale);

            // A fractional offset, as a burst's place in its window is.
            var offset = (Margin * scale) + 0.37;

            var visual = new DrawingVisual();
            var mine = new int[width * height];
            var rasterizer = new RayQuadRasterizer();
            rasterizer.Begin(width, height);
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new MatrixTransform(scale, 0, 0, scale, offset, offset));
                for (var layer = 0; layer < Widths.Length; layer++)
                {
                    RayArrowLayout.Emit(spines, written, Widths[layer], Heights[layer], quads);
                    var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
                    using (var writer = geometry.Open())
                    {
                        for (var i = 0; i < written; i++)
                        {
                            writer.BeginFigure(quads[i].BaseLeft, true, true);
                            writer.LineTo(quads[i].TipLeft, false, false);
                            writer.LineTo(quads[i].TipRight, false, false);
                            writer.LineTo(quads[i].BaseRight, false, false);
                        }
                    }

                    geometry.Freeze();
                    var brush = new SolidColorBrush(Color.FromArgb(Alphas[layer], 0xFF, 0xB0, 0x30));
                    brush.Freeze();
                    context.DrawGeometry(brush, null, geometry);
                    rasterizer.Fill(
                        quads, written, scale, scale,
                        offset + RayQuadRasterizer.WpfBiasX, offset + RayQuadRasterizer.WpfBiasY,
                        Alphas[layer], 0xFF, 0xB0, 0x30, mine);
                }

                context.Pop();
            }

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var theirs = new int[width * height];
            target.CopyPixels(theirs, width * 4, 0);

            long total = 0;
            var worst = 0;
            var over = 0;
            var lit = 0;
            for (var i = 0; i < mine.Length; i++)
            {
                if (theirs[i] != 0)
                {
                    lit++;
                }

                for (var shift = 0; shift < 32; shift += 8)
                {
                    var d = Math.Abs(((mine[i] >> shift) & 0xFF) - ((theirs[i] >> shift) & 0xFF));
                    total += d;
                    worst = Math.Max(worst, d);
                    if (d > 8)
                    {
                        over++;
                    }
                }
            }

            Console.WriteLine("arrows={0} lit={1} worst={2} over8={3} mean={4:0.0000}", written, lit, worst, over, total / (4.0 * mine.Length));
            Assert.IsTrue(lit > 500, "WPF drew nothing to compare with.");
            Assert.IsTrue(worst <= 32, "A channel differs by " + worst + " levels.");
            Assert.IsTrue(over <= lit / 50, over + " channel values differ by more than 8 levels.");
            Assert.IsTrue(total / (4.0 * mine.Length) < 0.2, "Mean difference too high.");
        }

        [TestMethod]
        public void Fill_DropsWhatFallsOutsideTheBuffer()
        {
            var quads = new[]
            {
                new RayArrowLayout.RayArrowQuad
                {
                    BaseLeft = new Point(-20, -20), TipLeft = new Point(-20, 40),
                    TipRight = new Point(40, 40), BaseRight = new Point(40, -20),
                },
            };
            var pixels = new int[10 * 10];
            var rasterizer = new RayQuadRasterizer();
            rasterizer.Begin(10, 10);
            rasterizer.Fill(quads, 1, 1, 1, 0, 0, 0xFF, 0x10, 0x20, 0x30, pixels);

            foreach (var pixel in pixels)
            {
                Assert.AreEqual(unchecked((int)0xFF102030), pixel);
            }
        }
    }
}
