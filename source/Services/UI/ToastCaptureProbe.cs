using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Diagnostic comparison of the two ways a toast card can be rasterized: through a VisualBrush
    /// into a DrawingVisual (the original path) versus straight into the RenderTargetBitmap. The
    /// direct path is ~2.3x cheaper, and this exists to prove it produces the same pixels on the
    /// real card — with the real template, theme, DPI, effects and fade — which no synthetic
    /// harness can settle.
    ///
    /// Flip <see cref="Enabled"/> and rebuild to turn it on, following
    /// <see cref="Common.PerfScope.PerfTracingEnabled"/>'s convention. While it is on, every sampled
    /// card is rendered BOTH ways, so the capture costs roughly three times its usual budget and the
    /// live toast will stutter — that is expected, and is why this ships off.
    ///
    /// Results accumulate per case (single / stacked / fade / screenshot) and are reported once per
    /// wave. Any case that is not byte-identical also dumps the two renders and an amplified
    /// difference image, so a mismatch can be looked at rather than only counted.
    /// </summary>
    internal static class ToastCaptureProbe
    {
        /// <summary>Diagnostic toggle. Runtime-evaluated so neither branch folds away.</summary>
        internal static readonly bool Enabled = false;

        /// <summary>Difference images written per session, so a systematic mismatch cannot flood the disk.</summary>
        private const int MaxDumps = 4;

        private sealed class CaseStats
        {
            public int Renders;
            public int Identical;
            public long DifferingPixels;
            public int MaxChannelDelta;
            public double ChannelDeltaSum;
            public long ChannelsCompared;
            public double BrushMs;
            public double DirectMs;
        }

        private static readonly Dictionary<string, CaseStats> Stats =
            new Dictionary<string, CaseStats>(StringComparer.Ordinal);

        private static int _dumps;

        /// <summary>
        /// Records one comparison. Both buffers are premultiplied BGRA at <paramref name="width"/> x
        /// <paramref name="height"/>. UI thread only, like the renders that produced them.
        /// </summary>
        public static void Record(
            ILogger logger, string caseTag, byte[] brushPixels, byte[] directPixels,
            int width, int height, double brushMs, double directMs, string dumpDirectory)
        {
            if (!Enabled || brushPixels == null || directPixels == null ||
                brushPixels.Length != directPixels.Length)
            {
                return;
            }

            if (!Stats.TryGetValue(caseTag, out var stats))
            {
                stats = new CaseStats();
                Stats[caseTag] = stats;
            }

            stats.Renders++;
            stats.BrushMs += brushMs;
            stats.DirectMs += directMs;
            stats.ChannelsCompared += brushPixels.Length;

            var differingPixels = 0;
            var maxDelta = 0;
            double deltaSum = 0;
            for (var i = 0; i < brushPixels.Length; i += 4)
            {
                var pixelDiffers = false;
                for (var channel = 0; channel < 4; channel++)
                {
                    var delta = Math.Abs(brushPixels[i + channel] - directPixels[i + channel]);
                    if (delta == 0)
                    {
                        continue;
                    }

                    pixelDiffers = true;
                    deltaSum += delta;
                    if (delta > maxDelta)
                    {
                        maxDelta = delta;
                    }
                }

                if (pixelDiffers)
                {
                    differingPixels++;
                }
            }

            stats.DifferingPixels += differingPixels;
            stats.ChannelDeltaSum += deltaSum;
            if (maxDelta > stats.MaxChannelDelta)
            {
                stats.MaxChannelDelta = maxDelta;
            }

            if (differingPixels == 0)
            {
                stats.Identical++;
                return;
            }

            TryDump(logger, caseTag, brushPixels, directPixels, width, height, dumpDirectory);
        }

        /// <summary>Emits one line per case and resets, at the end of a wave.</summary>
        public static void ReportWave(ILogger logger)
        {
            if (!Enabled || Stats.Count == 0)
            {
                return;
            }

            foreach (var pair in Stats)
            {
                var s = pair.Value;
                if (s.Renders == 0)
                {
                    continue;
                }

                logger?.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "[ToastProbe] {0}: identical={1}/{2}, differingPixels={3}, maxChannelDelta={4}, " +
                    "meanChannelDelta={5:0.####}, brush={6:0.00} ms, direct={7:0.00} ms",
                    pair.Key,
                    s.Identical,
                    s.Renders,
                    s.DifferingPixels,
                    s.MaxChannelDelta,
                    s.ChannelsCompared > 0 ? s.ChannelDeltaSum / s.ChannelsCompared : 0,
                    s.BrushMs / s.Renders,
                    s.DirectMs / s.Renders));
            }

            Stats.Clear();
        }

        /// <summary>
        /// Writes the two renders and an amplified difference next to each other. The difference is
        /// scaled by 32 and made opaque, because a one-level edge difference is invisible otherwise.
        /// </summary>
        private static void TryDump(
            ILogger logger, string caseTag, byte[] brushPixels, byte[] directPixels,
            int width, int height, string dumpDirectory)
        {
            if (_dumps >= MaxDumps || string.IsNullOrWhiteSpace(dumpDirectory))
            {
                return;
            }

            _dumps++;
            try
            {
                var folder = Path.Combine(dumpDirectory, "ToastProbe");
                Directory.CreateDirectory(folder);

                var diff = new byte[brushPixels.Length];
                for (var i = 0; i < brushPixels.Length; i += 4)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var delta = Math.Abs(brushPixels[i + channel] - directPixels[i + channel]) * 32;
                        diff[i + channel] = (byte)Math.Min(255, delta);
                    }

                    diff[i + 3] = 255;
                }

                var stamp = caseTag + "_" + _dumps.ToString(CultureInfo.InvariantCulture);
                WritePng(Path.Combine(folder, stamp + "_brush.png"), brushPixels, width, height);
                WritePng(Path.Combine(folder, stamp + "_direct.png"), directPixels, width, height);
                WritePng(Path.Combine(folder, stamp + "_diff.png"), diff, width, height);
                logger?.Info($"[ToastProbe] Wrote {stamp} render comparison to {folder}.");
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Toast probe dump failed.");
            }
        }

        private static void WritePng(string path, byte[] premulBgra, int width, int height)
        {
            var source = BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null,
                premulBgra, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }
        }
    }
}
