using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    /// <summary>
    /// Holds the decoded-sample lag scorer against a byte-walking mirror of the implementation it
    /// replaced. The cancellation thresholds are field-tuned, so this optimization is only safe if
    /// it is bit-identical — any score drift, however small, is a behavior change. The mirror below
    /// is a verbatim copy of the pre-optimization <c>ScoreCorrelationAtLag</c> and its byte
    /// readers; every field of every score must match it exactly.
    /// </summary>
    [TestClass]
    public class PcmAudioLagScanEquivalenceTests
    {
        private const int BlockAlign = 4;

        // === Verbatim mirror of the pre-optimization scorer ===

        private struct MirrorScore
        {
            public int LagFrames;
            public double ExactLagFrames;
            public int AnalysisStart;
            public double Dot;
            public double ReferenceEnergy;
            public long Count;
            public double Value;
        }

        private static short MirrorReadInt16(byte[] bytes, long offset)
        {
            return (short)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static double MirrorReadInterpolatedSample(
            byte[] bytes, double framePosition, int channel)
        {
            var frames = bytes.Length / BlockAlign;
            if (framePosition < 0 || framePosition > frames - 1 || channel < 0 || channel >= 2)
            {
                return 0;
            }

            var lower = (int)Math.Floor(framePosition);
            var fraction = framePosition - lower;
            var first = (double)MirrorReadInt16(bytes, lower * BlockAlign + channel * 2);
            if (fraction <= 0 || lower + 1 >= frames)
            {
                return first;
            }

            var second = (double)MirrorReadInt16(bytes, (lower + 1) * BlockAlign + channel * 2);
            return first + (second - first) * fraction;
        }

        private static MirrorScore MirrorScoreCorrelationAtLag(
            byte[] mixture,
            byte[] reference,
            double lagFrames,
            int analysisStart)
        {
            var mixtureFrames = mixture.Length / BlockAlign;
            var referenceFrames = reference.Length / BlockAlign;
            var referenceEnd = Math.Min(
                referenceFrames, analysisStart + PcmAudio.CorrelationWindowFrames);
            var mixtureStart = Math.Max(0, (int)Math.Ceiling(analysisStart - lagFrames));
            var mixtureEnd = Math.Min(
                mixtureFrames,
                (int)Math.Ceiling(referenceEnd - lagFrames));
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            for (var mixtureFrame = mixtureStart;
                 mixtureFrame < mixtureEnd;
                 mixtureFrame += PcmAudio.CorrelationStrideFrames)
            {
                var mixtureByte = mixtureFrame * BlockAlign;
                var referenceFrame = mixtureFrame + lagFrames;
                for (var channel = 0; channel < 2; channel++)
                {
                    var mixed = MirrorReadInt16(mixture, mixtureByte + channel * 2);
                    var source = MirrorReadInterpolatedSample(reference, referenceFrame, channel);
                    dot += mixed * source;
                    mixtureEnergy += mixed * (double)mixed;
                    referenceEnergy += source * source;
                    count++;
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            var value = denominator > 0 ? dot / denominator : 0;

            var windowFrames = Math.Max(1, referenceEnd - analysisStart);
            if (count / 2 * PcmAudio.CorrelationStrideFrames < windowFrames * 3L / 4)
            {
                value = 0;
            }

            return new MirrorScore
            {
                LagFrames = (int)Math.Round(lagFrames),
                ExactLagFrames = lagFrames,
                AnalysisStart = analysisStart,
                Dot = dot,
                ReferenceEnergy = referenceEnergy,
                Count = count,
                Value = value,
            };
        }

        // === Deterministic PCM material ===

        /// <summary>Band-limited stereo noise as raw 16-bit PCM bytes.</summary>
        private static byte[] NoisePcm(int frames, int seed, int amplitude, int extraBytes = 0)
        {
            var random = new Random(seed);
            var raw = new double[frames + 16];
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = random.Next(-amplitude, amplitude + 1);
            }

            var bytes = new byte[frames * BlockAlign + extraBytes];
            for (var frame = 0; frame < frames; frame++)
            {
                double left = 0;
                double right = 0;
                for (var k = 0; k < 8; k++)
                {
                    left += raw[frame + k];
                    right += raw[frame + k + 4];
                }

                WriteSample(bytes, frame, 0, (short)(left / 8));
                WriteSample(bytes, frame, 1, (short)(right / 8));
            }

            return bytes;
        }

        /// <summary>Noise plus a gain-scaled copy of the reference shifted by a whole-frame lag.</summary>
        private static byte[] MixturePcm(int frames, byte[] reference, int lagFrames, double gain, int seed)
        {
            var bytes = NoisePcm(frames, seed, 900);
            var referenceFrames = reference.Length / BlockAlign;
            for (var frame = 0; frame < frames; frame++)
            {
                var referenceFrame = frame + lagFrames;
                if (referenceFrame < 0 || referenceFrame >= referenceFrames)
                {
                    continue;
                }

                for (var channel = 0; channel < 2; channel++)
                {
                    var existing = MirrorReadInt16(bytes, frame * BlockAlign + channel * 2);
                    var copied = MirrorReadInt16(
                        reference, referenceFrame * BlockAlign + channel * 2);
                    var mixed = existing + gain * copied;
                    WriteSample(bytes, frame, channel, (short)Math.Max(
                        short.MinValue, Math.Min(short.MaxValue, mixed)));
                }
            }

            return bytes;
        }

        private static void WriteSample(byte[] bytes, int frame, int channel, short value)
        {
            var offset = frame * BlockAlign + channel * 2;
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        // Integer lags cover in-range, boundary, and fully out-of-range; fractional lags cover the
        // refinement grid (k/32 offsets) plus arbitrary fractions and out-of-range fractions.
        private static readonly double[] Lags =
        {
            -30000, -13000, -12000, -2400, -777, -8, -1, 0, 1, 7, 353, 2400, 11999, 12000, 30000,
            -12000.5, -0.5, -0.46875, -0.03125, 0.25, 0.5, 353.03125, 11999.96875,
        };

        [TestMethod]
        public void ScoreCorrelationAtLag_MatchesByteWalkingMirror()
        {
            var reference = NoisePcm(50000, seed: 11, amplitude: 1200);
            var pairs = new (string Label, byte[] Mixture, byte[] Reference)[]
            {
                ("noise-vs-planted", MixturePcm(60000, reference, 353, 0.8, seed: 23), reference),
                ("self", reference, reference),
                ("tiny", NoisePcm(40, 5, 800), NoisePcm(37, 6, 800)),
                ("one-frame", NoisePcm(1, 7, 800), NoisePcm(1, 8, 800)),
                ("odd-length-tail", NoisePcm(500, 9, 800, extraBytes: 2),
                    NoisePcm(400, 10, 800, extraBytes: 3)),
                ("silence", new byte[2000 * BlockAlign], NoisePcm(2000, 12, 800)),
            };

            foreach (var pair in pairs)
            {
                var mixtureView = PcmAudio.SampleView.From(pair.Mixture);
                var referenceView = ReferenceEquals(pair.Mixture, pair.Reference)
                    ? mixtureView
                    : PcmAudio.SampleView.From(pair.Reference);
                var referenceFrames = pair.Reference.Length / BlockAlign;
                var starts = new[] { 0, 12345, Math.Max(0, referenceFrames - 3), referenceFrames + 10 };
                foreach (var start in starts)
                {
                    foreach (var lag in Lags)
                    {
                        var expected = MirrorScoreCorrelationAtLag(
                            pair.Mixture, pair.Reference, lag, start);
                        var actual = PcmAudio.ScoreCorrelationAtLag(
                            mixtureView, referenceView, lag, start);
                        var context = $"{pair.Label} lag={lag} start={start}";
                        Assert.AreEqual(expected.LagFrames, actual.LagFrames, $"LagFrames {context}");
                        Assert.AreEqual(
                            expected.ExactLagFrames, actual.ExactLagFrames, $"ExactLagFrames {context}");
                        Assert.AreEqual(
                            expected.AnalysisStart, actual.AnalysisStart, $"AnalysisStart {context}");
                        Assert.AreEqual(expected.Dot, actual.Dot, $"Dot {context}");
                        Assert.AreEqual(
                            expected.ReferenceEnergy, actual.ReferenceEnergy, $"ReferenceEnergy {context}");
                        Assert.AreEqual(expected.Count, actual.Count, $"Count {context}");
                        Assert.AreEqual(expected.Value, actual.Value, $"Value {context}");
                    }
                }
            }
        }

        [TestMethod]
        public void ScanLagRange_ParallelGridMatchesSequentialFold()
        {
            var reference = NoisePcm(50000, seed: 31, amplitude: 1200);
            var mixture = MixturePcm(60000, reference, -777, 0.9, seed: 37);
            var mixtureView = PcmAudio.SampleView.From(mixture);
            var referenceView = PcmAudio.SampleView.From(reference);

            var ranges = new (int From, int To, int Step, bool PreferSmallLag)[]
            {
                (-2400, 2400, 1, false),
                (-12000, 12000, 2, true),
                (-12000, 12000, 2, false),
                (-779, -775, 1, true), // below the parallel threshold: sequential either way
            };

            foreach (var range in ranges)
            {
                foreach (var start in new[] { 0, 20000 })
                {
                    var sequential = PcmAudio.ScanLagRange(
                        mixtureView, referenceView, start,
                        range.From, range.To, range.Step, range.PreferSmallLag,
                        forceSequential: true);
                    var parallel = PcmAudio.ScanLagRange(
                        mixtureView, referenceView, start,
                        range.From, range.To, range.Step, range.PreferSmallLag,
                        forceSequential: false);
                    var context = $"range=[{range.From}..{range.To}]x{range.Step} " +
                        $"small={range.PreferSmallLag} start={start}";
                    Assert.AreEqual(sequential.LagFrames, parallel.LagFrames, $"LagFrames {context}");
                    Assert.AreEqual(sequential.ExactLagFrames, parallel.ExactLagFrames, $"ExactLagFrames {context}");
                    Assert.AreEqual(sequential.Dot, parallel.Dot, $"Dot {context}");
                    Assert.AreEqual(sequential.ReferenceEnergy, parallel.ReferenceEnergy, $"ReferenceEnergy {context}");
                    Assert.AreEqual(sequential.Count, parallel.Count, $"Count {context}");
                    Assert.AreEqual(sequential.Value, parallel.Value, $"Value {context}");
                }
            }
        }

        [TestMethod]
        public void SampleView_DecodesExactlyWhatTheByteReaderAssembles()
        {
            var pcm = NoisePcm(1000, seed: 41, amplitude: 30000, extraBytes: 3);
            var view = PcmAudio.SampleView.From(pcm);
            Assert.AreEqual(1000, view.Frames);
            Assert.AreEqual(2000, view.Samples.Length);
            for (var frame = 0; frame < view.Frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    Assert.AreEqual(
                        MirrorReadInt16(pcm, frame * BlockAlign + channel * 2),
                        view.Samples[frame * 2 + channel],
                        $"frame={frame} channel={channel}");
                }
            }
        }
    }
}
