// How much bitrate does a second encode need before it stops visibly softening the picture?
// Re-encodes a real clip at several bitrates using the plugin's own encoder configuration, then
// compares each result against the source frame by frame (PSNR) and reports the file size cost.
//
// PSNR rule of thumb for "is another generation visible": >45 dB effectively invisible,
// 40-45 dB very hard to see, 35-40 dB noticeable on detail, <35 dB obvious.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

internal static class GenerationLoss
{
    private const int MaxFrames = 60; // ~10s at 30fps, enough for a stable average

    private static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: GenerationLoss <source.mp4> <outDir> [bitrateKbps...]");
            return;
        }

        var source = args[0];
        var outDir = args[1];
        var rates = new List<int>();
        for (var i = 2; i < args.Length; i++)
        {
            rates.Add(int.Parse(args[i]));
        }

        if (rates.Count == 0)
        {
            rates.AddRange(new[] { 8000, 10000, 12000, 16000, 24000 });
        }

        Directory.CreateDirectory(outDir);
        MediaManager.Startup();
        try
        {
            var sourceBytes = new FileInfo(source).Length;
            var info = Probe(source);
            Console.WriteLine("source: " + Path.GetFileName(source) + "  " + info.Item1 + "x" + info.Item2 +
                " @" + info.Item3 + "fps  " + (sourceBytes / 1024 / 1024) + " MB");
            Console.WriteLine("        comparing the first " + MaxFrames + " frames of each re-encode against it");
            Console.WriteLine();
            Console.WriteLine("  bitrate     PSNR      size of a 28s clip   verdict");

            foreach (var kbps in rates)
            {
                var target = Path.Combine(outDir, "gen2_" + kbps + ".mp4");
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                Reencode(source, target, info.Item1, info.Item2, info.Item3, kbps * 1000);
                var psnr = ComparePsnr(source, target, info.Item1, info.Item2);
                var perSecond = new FileInfo(target).Length / (MaxFrames / (double)info.Item3);
                var verdict = psnr >= 45 ? "invisible" : psnr >= 40 ? "very hard to see" : psnr >= 35 ? "noticeable" : "obvious";
                Console.WriteLine(
                    "  " + (kbps / 1000.0).ToString("0.0").PadLeft(5) + " Mbps  " +
                    psnr.ToString("0.00").PadLeft(6) + " dB   " +
                    ((perSecond * 28) / 1024 / 1024).ToString("0").PadLeft(6) + " MB            " + verdict);
            }
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }
    }

    private static Tuple<int, int, int> Probe(string path)
    {
        using (var attributes = new MediaAttributes(1))
        {
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
            using (var reader = new SourceReader(path, attributes))
            {
                reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                using (var type = reader.GetNativeMediaType((int)SourceReaderIndex.FirstVideoStream, 0))
                {
                    var size = type.Get(MediaTypeAttributeKeys.FrameSize);
                    var rate = type.Get(MediaTypeAttributeKeys.FrameRate);
                    var num = (int)(rate >> 32);
                    var den = Math.Max(1, (int)(rate & 0xffffffff));
                    return Tuple.Create((int)(size >> 32), (int)(size & 0xffffffff), Math.Max(1, num / den));
                }
            }
        }
    }

    private static double MeasuredSeconds(string path)
    {
        using (var reader = new SourceReader(path))
        {
            reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
            long last = 0;
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

                using (sample) { last = sample.SampleTime + Math.Max(0, sample.SampleDuration); }
            }

            return last / 10_000_000.0;
        }
    }

    // Mirrors the overlay pass: shared device, ARGB32 decode, H.264 out with the plugin's attributes.
    private static void Reencode(string source, string target, int w, int h, int fps, int bitrate)
    {
        using (var device = new D3D11.Device(
            SharpDX.Direct3D.DriverType.Hardware,
            D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport))
        using (var manager = new DXGIDeviceManager())
        {
            manager.ResetDevice(device);
            using (var readerAttributes = new MediaAttributes(2))
            {
                readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
                readerAttributes.Set(SourceReaderAttributeKeys.D3DManager, manager);
                using (var reader = new SourceReader(source, readerAttributes))
                {
                    reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
                    reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
                    using (var request = new MediaType())
                    {
                        request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                        reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
                    }

                    var decodedType = reader.GetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream);
                    SinkWriter sink;
                    using (var sinkAttributes = new MediaAttributes(3))
                    {
                        sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                        sinkAttributes.Set(SinkWriterAttributeKeys.D3DManager, manager);
                        sinkAttributes.Set(SinkWriterAttributeKeys.ReadwriteD3DOptional, true);
                        sink = MediaFactory.CreateSinkWriterFromURL(target, null, sinkAttributes);
                    }

                    using (decodedType)
                    using (sink)
                    {
                        int stream;
                        using (var output = new MediaType())
                        {
                            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                            output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
                            output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, fps);
                            output.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                            output.Set(MediaTypeAttributeKeys.FrameSize, ((long)w << 32) | (uint)h);
                            output.Set(MediaTypeAttributeKeys.FrameRate, ((long)fps << 32) | 1);
                            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, ((long)1 << 32) | 1);
                            output.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range16_235);
                            output.Set(MediaTypeAttributeKeys.YuvMatrix, (int)VideoTransferMatrix.Bt709);
                            output.Set(MediaTypeAttributeKeys.TransferFunction, (int)VideoTransferFunction.Func709);
                            output.Set(MediaTypeAttributeKeys.VideoPrimaries, (int)VideoPrimaries.Bt709);
                            sink.AddStream(output, out stream);
                        }

                        decodedType.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range0_255);
                        sink.SetInputMediaType(stream, decodedType, null);
                        sink.BeginWriting();

                        var written = 0;
                        while (written < MaxFrames)
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
                                sink.WriteSample(stream, sample);
                                written++;
                            }
                        }

                        sink.Finalize();
                    }
                }
            }
        }
    }

    private static double ComparePsnr(string a, string b, int w, int h)
    {
        double totalMse = 0;
        var frames = 0;
        using (var readerA = OpenRgb(a))
        using (var readerB = OpenRgb(b))
        {
            var bufferA = new byte[w * 4 * h];
            var bufferB = new byte[w * 4 * h];
            while (frames < MaxFrames)
            {
                if (!Next(readerA, bufferA) || !Next(readerB, bufferB))
                {
                    break;
                }

                double mse = 0;
                var samples = 0;
                // Every 4th pixel is plenty for a stable average and keeps this quick.
                for (var i = 0; i < bufferA.Length; i += 16)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        var d = bufferA[i + c] - bufferB[i + c];
                        mse += d * d;
                        samples++;
                    }
                }

                totalMse += mse / samples;
                frames++;
            }
        }

        if (frames == 0 || totalMse <= 0)
        {
            return 99;
        }

        var meanMse = totalMse / frames;
        return 10 * Math.Log10(255.0 * 255.0 / meanMse);
    }

    private static SourceReader OpenRgb(string path)
    {
        var attributes = new MediaAttributes(1);
        attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
        var reader = new SourceReader(path, attributes);
        attributes.Dispose();
        reader.SetStreamSelection((int)SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection((int)SourceReaderIndex.FirstVideoStream, true);
        using (var request = new MediaType())
        {
            request.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            request.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType((int)SourceReaderIndex.FirstVideoStream, request);
        }

        return reader;
    }

    private static bool Next(SourceReader reader, byte[] into)
    {
        var sample = reader.ReadSample(
            (int)SourceReaderIndex.FirstVideoStream, SourceReaderControlFlags.None,
            out _, out var flags, out _);
        if (sample == null || (flags & SourceReaderFlags.Endofstream) != 0)
        {
            sample?.Dispose();
            return false;
        }

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
            var ptr = buffer.Lock(out _, out var length);
            try { Marshal.Copy(ptr, into, 0, Math.Min(length, into.Length)); }
            finally { buffer.Unlock(); }
        }

        return true;
    }
}


