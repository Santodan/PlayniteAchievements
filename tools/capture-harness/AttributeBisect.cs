// Which output-type attribute makes the H.264 encoder flatten per-frame durations onto a fixed grid?
// Same sink/encoder setup as MediaFoundationH264Encoder, one attribute varied at a time, fed uneven
// durations. Reports the resulting stts entry count: >1 means the real timing survived.
using System;
using System.Collections.Generic;
using System.IO;
using SharpDX.MediaFoundation;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

internal static class AttributeBisect
{
    private const int W = 640;
    private const int H = 360;
    private const int Fps = 30;
    private const int Frames = 60;

    private static void Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : ".";
        MediaManager.Startup();
        try
        {
            Case(dir, "output FrameRate only", false, false, false, true, false);
            Case(dir, "shipped, no input range", true, true, true, true, false);
            Case(dir, "shipped + INPUT range", true, true, true, true, true);
            Case(dir, "INPUT range only", false, false, true, true, true);
        }
        finally
        {
            try { MediaManager.Shutdown(); } catch { }
        }
    }

    private static void Case(
        string dir, string label, bool keyframeSpacing, bool colour, bool inputRate, bool outputRate,
        bool inputRange)
    {
        var path = Path.Combine(dir, "bisect_" + Math.Abs(label.GetHashCode()) + ".mp4");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        try
        {
            using (var device = new D3D11.Device(
                SharpDX.Direct3D.DriverType.Hardware,
                D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport))
            using (var manager = new DXGIDeviceManager())
            {
                manager.ResetDevice(device);
                SinkWriter sink;
                using (var attributes = new MediaAttributes(2))
                {
                    attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                    attributes.Set(SinkWriterAttributeKeys.D3DManager, manager);
                    sink = MediaFactory.CreateSinkWriterFromURL(path, null, attributes);
                }

                using (sink)
                {
                    int stream;
                    using (var output = new MediaType())
                    {
                        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                        output.Set(MediaTypeAttributeKeys.AvgBitrate, 4_000_000);
                        output.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                        output.Set(MediaTypeAttributeKeys.FrameSize, Pack(W, H));
                        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                        if (keyframeSpacing)
                        {
                            output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, Fps);
                        }

                        if (outputRate)
                        {
                            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
                        }

                        if (colour)
                        {
                            // Exactly MediaFoundationColor.ApplyBt709LimitedOutput.
                            output.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range16_235);
                            output.Set(MediaTypeAttributeKeys.YuvMatrix, (int)VideoTransferMatrix.Bt709);
                            output.Set(MediaTypeAttributeKeys.TransferFunction, (int)VideoTransferFunction.Func709);
                            output.Set(MediaTypeAttributeKeys.VideoPrimaries, (int)VideoPrimaries.Bt709);
                        }

                        sink.AddStream(output, out stream);
                    }

                    using (var input = new MediaType())
                    {
                        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                        input.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                        input.Set(MediaTypeAttributeKeys.FrameSize, Pack(W, H));
                        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                        if (inputRate)
                        {
                            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
                        }

                        if (inputRange)
                        {
                            // Exactly MediaFoundationColor.ApplyFullRangeRgbInput.
                            input.Set(MediaTypeAttributeKeys.VideoNominalRange, (int)NominalRange.Range0_255);
                        }

                        sink.SetInputMediaType(stream, input, null);
                    }

                    sink.BeginWriting();
                    var description = Describe();
                    var time = 0L;
                    for (var i = 0; i < Frames; i++)
                    {
                        var duration = i % 2 == 0 ? 100_000L : 566_666L;
                        using (var texture = new D3D11.Texture2D(device, description))
                        {
                            var iid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
                            MediaFactory.CreateDXGISurfaceBuffer(iid, texture, 0, false, out var buffer);
                            using (buffer)
                            {
                                using (var buffer2D = buffer.QueryInterface<Buffer2D>())
                                {
                                    buffer.CurrentLength = buffer2D.ContiguousLength;
                                }

                                using (var sample = MediaFactory.CreateSample())
                                {
                                    sample.AddBuffer(buffer);
                                    sample.SampleTime = time;
                                    sample.SampleDuration = duration;
                                    sink.WriteSample(stream, sample);
                                }
                            }
                        }

                        time += duration;
                    }

                    sink.Finalize();
                }
            }

            var entries = SttsEntries(path);
            Console.WriteLine(
                "  " + label.PadRight(26) + " stts entries = " + entries +
                (entries > 1 ? "   PRESERVED" : "   FLATTENED"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + label.PadRight(26) + " failed: " + ex.Message);
        }
    }

    private static D3D11.Texture2DDescription Describe()
    {
        return new D3D11.Texture2DDescription
        {
            Width = W,
            Height = H,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new DXGI.SampleDescription(1, 0),
            Usage = D3D11.ResourceUsage.Default,
            BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
            CpuAccessFlags = D3D11.CpuAccessFlags.None,
            OptionFlags = D3D11.ResourceOptionFlags.None,
        };
    }

    private static long Pack(int high, int low)
    {
        return ((long)high << 32) | (uint)low;
    }

    private static int SttsEntries(string path)
    {
        var b = File.ReadAllBytes(path);
        var moov = Find(b, 0, b.Length, "moov");
        foreach (var trak in All(b, moov.Item1, moov.Item2, "trak"))
        {
            var mdia = Find(b, trak.Item1, trak.Item2, "mdia");
            var hdlr = Find(b, mdia.Item1, mdia.Item2, "hdlr");
            if (Type(b, hdlr.Item1 + 8) != "vide")
            {
                continue;
            }

            var minf = Find(b, mdia.Item1, mdia.Item2, "minf");
            var stbl = Find(b, minf.Item1, minf.Item2, "stbl");
            var stts = Find(b, stbl.Item1, stbl.Item2, "stts");
            return (int)U32(b, stts.Item1 + 4);
        }

        return 0;
    }

    private static long U32(byte[] b, long offset)
    {
        var o = (int)offset;
        return ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
    }

    private static string Type(byte[] b, long o)
    {
        return System.Text.Encoding.ASCII.GetString(b, (int)o, 4);
    }

    private static Tuple<long, long> Find(byte[] b, long start, long end, string type)
    {
        var o = start;
        while (o + 8 <= end)
        {
            var size = U32(b, o);
            var t = Type(b, o + 4);
            long header = 8;
            if (size == 0) { size = end - o; }
            if (size == 1)
            {
                size = 0;
                for (var i = 0; i < 8; i++) { size = (size << 8) | b[(int)o + 8 + i]; }
                header = 16;
            }

            if (t == type) { return Tuple.Create(o + header, o + size); }
            o += size;
        }

        throw new InvalidDataException(type + " not found");
    }

    private static IEnumerable<Tuple<long, long>> All(byte[] b, long start, long end, string type)
    {
        var o = start;
        while (o + 8 <= end)
        {
            var size = U32(b, o);
            if (size <= 0) { break; }
            if (Type(b, o + 4) == type) { yield return Tuple.Create(o + 8, o + size); }
            o += size;
        }
    }
}
