using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class RecordingCommandBuilderTests
    {
        private static RecordingCommandBuilder.CaptureOptions DefaultOptions()
        {
            return new RecordingCommandBuilder.CaptureOptions
            {
                Backend = RecordingCaptureBackend.Gdigrab,
                Fps = 30,
                MonitorX = -1920,
                MonitorY = 120,
                MonitorWidth = 1920,
                MonitorHeight = 1080,
                MonitorIndex = 1,
                Resolution = RecordingResolution.Native,
                EncoderArguments = "-c:v libx264 -preset ultrafast -crf 23",
                SegmentSeconds = 5,
                BufferDirectory = @"C:\Data\RecordingBuffer\20260101-120000"
            };
        }

        [TestMethod]
        public void Capture_Gdigrab_IncludesMonitorOffsetsAndSize()
        {
            var args = RecordingCommandBuilder.BuildCaptureArguments(DefaultOptions());

            StringAssert.Contains(args, "-f gdigrab");
            StringAssert.Contains(args, "-framerate 30");
            StringAssert.Contains(args, "-offset_x -1920");
            StringAssert.Contains(args, "-offset_y 120");
            StringAssert.Contains(args, "-video_size 1920x1080");
            StringAssert.Contains(args, "-draw_mouse 1 -i desktop");
        }

        [TestMethod]
        public void Capture_OddMonitorDimensions_RoundDownToEven()
        {
            var options = DefaultOptions();
            options.MonitorWidth = 1921;
            options.MonitorHeight = 1081;

            var args = RecordingCommandBuilder.BuildCaptureArguments(options);

            StringAssert.Contains(args, "-video_size 1920x1080");
        }

        [TestMethod]
        public void Capture_SegmentsWithForcedKeyframesAndStrftimeNames()
        {
            var args = RecordingCommandBuilder.BuildCaptureArguments(DefaultOptions());

            StringAssert.Contains(args, "-pix_fmt yuv420p");
            StringAssert.Contains(args, "-g 30 -keyint_min 30");
            StringAssert.Contains(args, "-force_key_frames \"expr:gte(t,n_forced*1)\"");
            StringAssert.Contains(args, "-f segment -segment_time 5 -reset_timestamps 1 -strftime 1");
            StringAssert.Contains(args, @"C:\Data\RecordingBuffer\20260101-120000\seg_%Y%m%d-%H%M%S.ts");
        }

        [TestMethod]
        public void Capture_NativeResolution_HasNoScaleFilter()
        {
            var args = RecordingCommandBuilder.BuildCaptureArguments(DefaultOptions());

            Assert.IsFalse(args.Contains("-vf"), args);
        }

        [TestMethod]
        public void Capture_FixedResolutions_UseEvenWidthScaleFilter()
        {
            var options = DefaultOptions();

            options.Resolution = RecordingResolution.P1080;
            StringAssert.Contains(RecordingCommandBuilder.BuildCaptureArguments(options), "-vf scale=-2:1080");

            options.Resolution = RecordingResolution.P720;
            StringAssert.Contains(RecordingCommandBuilder.BuildCaptureArguments(options), "-vf scale=-2:720");
        }

        [TestMethod]
        public void Capture_Ddagrab_UsesMonitorIndexScopedFilter()
        {
            var options = DefaultOptions();
            options.Backend = RecordingCaptureBackend.Ddagrab;

            var args = RecordingCommandBuilder.BuildCaptureArguments(options);

            StringAssert.Contains(args, "ddagrab=output_idx=1:framerate=30:draw_mouse=1");
            Assert.IsFalse(args.Contains("gdigrab"), args);
        }

        [TestMethod]
        public void ResolveBackend_AutoPrefersDdagrabWhenSupported()
        {
            Assert.AreEqual(
                RecordingCaptureBackend.Ddagrab,
                RecordingCommandBuilder.ResolveBackend(RecordingCaptureBackend.Auto, supportsDdagrab: true));
            Assert.AreEqual(
                RecordingCaptureBackend.Gdigrab,
                RecordingCommandBuilder.ResolveBackend(RecordingCaptureBackend.Auto, supportsDdagrab: false));
        }

        [TestMethod]
        public void ResolveBackend_ExplicitChoicesPassThrough()
        {
            Assert.AreEqual(
                RecordingCaptureBackend.Gdigrab,
                RecordingCommandBuilder.ResolveBackend(RecordingCaptureBackend.Gdigrab, supportsDdagrab: true));
            Assert.AreEqual(
                RecordingCaptureBackend.Ddagrab,
                RecordingCommandBuilder.ResolveBackend(RecordingCaptureBackend.Ddagrab, supportsDdagrab: false));
        }

        [TestMethod]
        public void Encoder_Auto_PrefersNvencThenQsvThenAmfThenX264()
        {
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(
                    RecordingEncoder.Auto, new[] { "libx264", "h264_amf", "h264_qsv", "h264_nvenc" }),
                "h264_nvenc");
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(
                    RecordingEncoder.Auto, new[] { "libx264", "h264_amf", "h264_qsv" }),
                "h264_qsv");
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(
                    RecordingEncoder.Auto, new[] { "libx264", "h264_amf" }),
                "h264_amf");
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(
                    RecordingEncoder.Auto, new[] { "libx264" }),
                "libx264");
        }

        [TestMethod]
        public void Encoder_Auto_WithoutProbeFallsBackToX264()
        {
            var args = RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Auto, null);

            StringAssert.Contains(args, "libx264");
            StringAssert.Contains(args, "-preset ultrafast -crf 23");
        }

        [TestMethod]
        public void Encoder_ExplicitChoice_IsHonoredWithoutProbe()
        {
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Nvenc, null),
                "h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0");
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Qsv, null),
                "h264_qsv -global_quality 23");
            StringAssert.Contains(
                RecordingCommandBuilder.BuildEncoderArguments(RecordingEncoder.Amf, null),
                "h264_amf -quality speed -rc cqp");
        }

        [TestMethod]
        public void Trim_StreamCopy_SeeksConcatAndFaststarts()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\list.txt", 2.5, 17.25, @"D:\out\clip.mp4");

            StringAssert.Contains(args, "-f concat -safe 0 -ss 2.5 -i \"C:\\buf\\list.txt\"");
            StringAssert.Contains(args, "-t 17.25 -c copy -movflags +faststart \"D:\\out\\clip.mp4\"");
            Assert.IsFalse(args.Contains("libx264"), args);
        }

        [TestMethod]
        public void Trim_Reencode_UsesVeryfastCrf20()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\list.txt", 0, 15, @"D:\out\clip.mp4", reencode: true);

            StringAssert.Contains(args, "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p");
            Assert.IsFalse(args.Contains("-c copy"), args);
        }

        [TestMethod]
        public void Trim_OffsetsAreInvariantCulture()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\list.txt", 1.125, 9.5, @"D:\out\clip.mp4");

            StringAssert.Contains(args, "-ss 1.125");
            StringAssert.Contains(args, "-t 9.5");
        }

        [TestMethod]
        public void Trim_WithAudio_SeeksBothInputsAndMuxesAac()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\video.txt", 2.5, 17.25, @"D:\out\clip.mp4",
                reencode: false,
                audioConcatListPath: @"C:\buf\audio.txt",
                audioStartOffsetSeconds: 1.75);

            StringAssert.Contains(args, "-f concat -safe 0 -ss 2.5 -i \"C:\\buf\\video.txt\"");
            StringAssert.Contains(args, "-f concat -safe 0 -ss 1.75 -i \"C:\\buf\\audio.txt\"");
            StringAssert.Contains(
                args,
                "-t 17.25 -map 0:v -map 1:a? -c:v copy -c:a aac -b:a 160k -movflags +faststart \"D:\\out\\clip.mp4\"");
            Assert.IsFalse(args.Contains("libx264"), args);
        }

        [TestMethod]
        public void Trim_WithAudio_ReencodeKeepsAudioInputsAndAac()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\video.txt", 0, 15, @"D:\out\clip.mp4",
                reencode: true,
                audioConcatListPath: @"C:\buf\audio.txt",
                audioStartOffsetSeconds: 3);

            StringAssert.Contains(args, "-ss 3 -i \"C:\\buf\\audio.txt\"");
            StringAssert.Contains(
                args,
                "-map 0:v -map 1:a? -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 160k");
            Assert.IsFalse(args.Contains("-c copy"), args);
        }

        [TestMethod]
        public void Trim_WithoutAudioList_MatchesVideoOnlyArguments()
        {
            var baseline = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\list.txt", 2.5, 17.25, @"D:\out\clip.mp4");
            var withNullAudio = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\list.txt", 2.5, 17.25, @"D:\out\clip.mp4",
                reencode: false,
                audioConcatListPath: null,
                audioStartOffsetSeconds: 3);

            Assert.AreEqual(baseline, withNullAudio);
            Assert.IsFalse(baseline.Contains("-map"), baseline);
            Assert.IsFalse(baseline.Contains("aac"), baseline);
        }

        [TestMethod]
        public void ConcatList_QuotesAndEscapesApostrophes()
        {
            var content = RecordingCommandBuilder.BuildConcatListContent(new[]
            {
                @"C:\buf\seg_20260101-120000.ts",
                @"C:\buf\it's here\seg_20260101-120005.ts"
            });

            StringAssert.Contains(content, "file 'C:\\buf\\seg_20260101-120000.ts'\n");
            StringAssert.Contains(content, "file 'C:\\buf\\it'\\''s here\\seg_20260101-120005.ts'\n");
        }

        [TestMethod]
        public void ConcatList_SkipsBlankEntries()
        {
            var content = RecordingCommandBuilder.BuildConcatListContent(new[] { null, "", @"C:\a.ts" });

            Assert.AreEqual("file 'C:\\a.ts'\n", content);
        }

        [TestMethod]
        public void SmokeTest_IsOneSecondTinyGrabToNullMuxer()
        {
            var args = RecordingCommandBuilder.BuildSmokeTestArguments(10, 20);

            StringAssert.Contains(args, "-f gdigrab");
            StringAssert.Contains(args, "-offset_x 10 -offset_y 20");
            StringAssert.Contains(args, "-video_size 64x64");
            StringAssert.Contains(args, "-t 1 -f null -");
        }

        [TestMethod]
        public void DdagrabSmokeTest_IsOneSecondCaptureToNullMuxer()
        {
            var args = RecordingCommandBuilder.BuildDdagrabSmokeTestArguments(1);

            StringAssert.Contains(args, "ddagrab=output_idx=1:framerate=10");
            StringAssert.Contains(args, "-t 1 -f null -");
            Assert.IsFalse(args.Contains("gdigrab"), args);
        }

        // === Cropped exports ===

        [TestMethod]
        public void Trim_WithCrop_UsesFilterAndSessionEncoderInsteadOfCopy()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\clip.txt", 3, 15, @"C:\out\clip.mp4",
                reencode: false,
                crop: new System.Drawing.Rectangle(100, 50, 1280, 720),
                cropEncoderArguments: "-c:v h264_nvenc -preset p4");

            StringAssert.Contains(args, "-filter:v \"crop=1280:720:100:50\"");
            StringAssert.Contains(args, "-c:v h264_nvenc -preset p4");
            Assert.IsFalse(args.Contains("-c copy"));
        }

        [TestMethod]
        public void Trim_WithCrop_ReencodeRetryFallsBackToSoftwareEncoder()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\clip.txt", 3, 15, @"C:\out\clip.mp4",
                reencode: true,
                crop: new System.Drawing.Rectangle(100, 50, 1280, 720),
                cropEncoderArguments: "-c:v h264_nvenc -preset p4");

            StringAssert.Contains(args, "-filter:v \"crop=1280:720:100:50\"");
            StringAssert.Contains(args, "-c:v libx264");
            Assert.IsFalse(args.Contains("h264_nvenc"));
        }

        [TestMethod]
        public void Trim_WithCropAndAudio_KeepsAudioMappingAndFilter()
        {
            var args = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\clip.txt", 3, 15, @"C:\out\clip.mp4",
                reencode: false,
                audioConcatListPath: @"C:\buf\clipaud.txt",
                audioStartOffsetSeconds: 2,
                crop: new System.Drawing.Rectangle(0, 0, 640, 480),
                cropEncoderArguments: "-c:v h264_nvenc -preset p4");

            StringAssert.Contains(args, "-filter:v \"crop=640:480:0:0\"");
            StringAssert.Contains(args, "-map 0:v -map 1:a?");
            StringAssert.Contains(args, "-c:a aac");
        }

        [TestMethod]
        public void Trim_WithoutCrop_IsUnchanged()
        {
            var baseline = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\clip.txt", 3, 15, @"C:\out\clip.mp4");
            var explicitNull = RecordingCommandBuilder.BuildTrimArguments(
                @"C:\buf\clip.txt", 3, 15, @"C:\out\clip.mp4",
                reencode: false, audioConcatListPath: null, audioStartOffsetSeconds: 0,
                crop: null, cropEncoderArguments: "-c:v h264_nvenc");

            Assert.AreEqual(baseline, explicitNull);
            StringAssert.Contains(baseline, "-c copy");
        }

        [TestMethod]
        public void ComputeCropRectangle_WindowedGame_MapsToMonitorRelativeEvenRect()
        {
            var crop = RecordingCommandBuilder.ComputeCropRectangle(
                new System.Drawing.Rectangle(-1920, 120, 1920, 1080),
                new System.Drawing.Rectangle(-1820, 221, 1280, 721),
                RecordingResolution.Native);

            Assert.IsTrue(crop.HasValue);
            // The origin rounds UP to even (101 -> 102) so no border pixel above the client
            // area leaks into the crop; the far edges round down.
            Assert.AreEqual(100, crop.Value.X);
            Assert.AreEqual(102, crop.Value.Y);
            Assert.AreEqual(1280, crop.Value.Width);
            Assert.AreEqual(720, crop.Value.Height);
        }

        [TestMethod]
        public void ComputeCropRectangle_FullscreenWindow_ReturnsNullToKeepStreamCopy()
        {
            var monitor = new System.Drawing.Rectangle(0, 0, 2880, 1920);

            Assert.IsNull(RecordingCommandBuilder.ComputeCropRectangle(
                monitor, monitor, RecordingResolution.Native));
            // Borderless with a stray pixel row still counts as fullscreen.
            Assert.IsNull(RecordingCommandBuilder.ComputeCropRectangle(
                monitor, new System.Drawing.Rectangle(0, 1, 2880, 1919), RecordingResolution.Native));
        }

        [TestMethod]
        public void ComputeCropRectangle_DownscaledCapture_ScalesTheRegion()
        {
            // 2160p monitor captured at 1080p: everything halves.
            var crop = RecordingCommandBuilder.ComputeCropRectangle(
                new System.Drawing.Rectangle(0, 0, 3840, 2160),
                new System.Drawing.Rectangle(400, 200, 1920, 1080),
                RecordingResolution.P1080);

            Assert.IsTrue(crop.HasValue);
            Assert.AreEqual(200, crop.Value.X);
            Assert.AreEqual(100, crop.Value.Y);
            Assert.AreEqual(960, crop.Value.Width);
            Assert.AreEqual(540, crop.Value.Height);
        }

        [TestMethod]
        public void ComputeCropRectangle_DegenerateOrOffMonitorWindow_ReturnsNull()
        {
            var monitor = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            Assert.IsNull(RecordingCommandBuilder.ComputeCropRectangle(
                monitor, new System.Drawing.Rectangle(5000, 5000, 800, 600), RecordingResolution.Native));
            Assert.IsNull(RecordingCommandBuilder.ComputeCropRectangle(
                monitor, new System.Drawing.Rectangle(10, 10, 40, 30), RecordingResolution.Native));
        }
    }
}
