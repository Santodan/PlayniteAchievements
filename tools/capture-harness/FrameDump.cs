// Decodes a clip and writes consecutive frames as downscaled PNGs plus a per-frame change report, so
// the reported flicker can be seen rather than inferred.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

internal static class FrameDump
{
    private static void Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("usage: FrameDump <clip.mp4> <outDir> <startSeconds> <endSeconds> [maxWidth]");
            return;
        }

        var path = args[0];
        var outDir = args[1];
        var start = double.Parse(args[2]);
        var end = double.Parse(args[3]);
        var maxWidth = args.Length > 4 ? int.Parse(args[4]) : 640;

        System.IO.Directory.CreateDirectory(outDir);
        MediaManager.Startup();
        try
        {
            using (var attributes = new MediaAttributes(1))
            {
                attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                using (var reader = new SourceReader(path, attributes))
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                    using (var request = new MediaType())
                    {
                        request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                    }

                    int w, h, stride;
                    using (var decoded = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream))
                    {
                        var size = decoded.Get(MediaTypeAttributeKeys.FrameSize);
                        w = (int)(size >> 32);
                        h = (int)(size & 0xffffffff);
                        try { stride = decoded.Get(MediaTypeAttributeKeys.DefaultStride); }
                        catch { stride = w * 4; }
                    }

                    Console.WriteLine("frame size " + w + "x" + h + " stride " + stride);
                    Dump(reader, outDir, start, end, w, h, Math.Abs(stride), stride < 0, maxWidth);
                }
            }
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }
    }

    private static void Dump(
        SourceReader reader, string outDir, double start, double end,
        int w, int h, int stride, bool bottomUp, int maxWidth)
    {
        var frame = new byte[stride * h];
        byte[] previous = null;
        var index = 0;
        var saved = 0;

        while (true)
        {
            var sample = reader.ReadSample(
                (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
                out _, out var flags, out _);
            if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
            {
                sample?.Dispose();
                break;
            }

            using (sample)
            {
                var seconds = sample.SampleTime / 10_000_000.0;
                index++;
                if (seconds < start)
                {
                    continue;
                }

                if (seconds > end)
                {
                    break;
                }

                using (var buffer = sample.ConvertToContiguousBuffer())
                {
                    var ptr = buffer.Lock(out _, out var length);
                    try
                    {
                        Marshal.Copy(ptr, frame, 0, Math.Min(length, frame.Length));
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }

                // How much changed since the previous frame, as a rough per-frame activity number.
                long diff = 0;
                if (previous != null)
                {
                    for (var i = 0; i < frame.Length; i += 64)
                    {
                        diff += Math.Abs(frame[i] - previous[i]);
                    }
                }

                previous = (byte[])frame.Clone();
                Save(frame, w, h, stride, bottomUp, maxWidth,
                    System.IO.Path.Combine(outDir, "f" + saved.ToString("000") + "_t" + seconds.ToString("0.000") + ".png"));
                Console.WriteLine("  saved f" + saved.ToString("000") + " at " + seconds.ToString("0.000") + "s  sampleIndex=" + index + "  changeScore=" + diff);
                saved++;
            }
        }

        Console.WriteLine("saved " + saved + " frames");
    }

    private static void Save(
        byte[] frame, int w, int h, int stride, bool bottomUp, int maxWidth, string path)
    {
        using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (var y = 0; y < h; y++)
                {
                    var sourceRow = bottomUp ? (h - 1 - y) * stride : y * stride;
                    Marshal.Copy(frame, sourceRow, IntPtr.Add(data.Scan0, y * data.Stride), Math.Min(stride, data.Stride));
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var scale = Math.Min(1.0, maxWidth / (double)w);
            var tw = Math.Max(1, (int)(w * scale));
            var th = Math.Max(1, (int)(h * scale));
            using (var small = new Bitmap(bitmap, tw, th))
            {
                small.Save(path, ImageFormat.Png);
            }
        }
    }
}
