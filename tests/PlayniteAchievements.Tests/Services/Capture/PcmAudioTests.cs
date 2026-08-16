using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Capture;

namespace PlayniteAchievements.Services.Tests.Capture
{
    [TestClass]
    public class PcmAudioTests
    {
        private static byte[] Samples(params short[] values)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static short[] ToShorts(byte[] bytes)
        {
            var values = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 2);
            return values;
        }

        /// <summary>
        /// Deterministic band-limited stereo noise (8-tap moving average of white noise), shaped
        /// like real game audio so a sub-frame misalignment leaves only a small residual.
        /// </summary>
        private static short[] BandLimitedNoise(int frames, int seed, int amplitude)
        {
            var random = new Random(seed);
            var raw = new double[frames + 16];
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = random.Next(-amplitude, amplitude + 1);
            }

            var samples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                double left = 0;
                double right = 0;
                for (var k = 0; k < 8; k++)
                {
                    left += raw[frame + k];
                    right += raw[frame + k + 4];
                }

                samples[frame * 2] = (short)(left / 8);
                samples[frame * 2 + 1] = (short)(right / 8);
            }

            return samples;
        }

        private static double SampleAt(short[] samples, double framePosition, int channel)
        {
            var frames = samples.Length / 2;
            var lower = (int)Math.Floor(framePosition);
            var fraction = framePosition - lower;
            double first = lower >= 0 && lower < frames ? samples[lower * 2 + channel] : 0;
            var upper = lower + 1;
            double second = upper >= 0 && upper < frames ? samples[upper * 2 + channel] : 0;
            return first + (second - first) * fraction;
        }

        private static double Energy(short[] samples, int startFrame, int endFrame)
        {
            double energy = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                energy += samples[frame * 2] * (double)samples[frame * 2];
                energy += samples[frame * 2 + 1] * (double)samples[frame * 2 + 1];
            }

            return energy;
        }

        [TestMethod]
        public void MixInto_AddsSamples()
        {
            var dest = Samples(100, -200, 300, 0);
            var source = Samples(50, -50, -300, 1000);

            PcmAudio.MixInto(dest, 0, source, 0, dest.Length);

            CollectionAssert.AreEqual(new short[] { 150, -250, 0, 1000 }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_SaturatesInsteadOfWrapping()
        {
            var dest = Samples(short.MaxValue, short.MinValue);
            var source = Samples(1000, -1000);

            PcmAudio.MixInto(dest, 0, source, 0, dest.Length);

            CollectionAssert.AreEqual(new[] { short.MaxValue, short.MinValue }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_RespectsOffsetsAndClampsToBothBuffers()
        {
            var dest = Samples(1, 2, 3, 4);
            var source = Samples(10, 20);

            // Source offset skips its first sample; dest offset starts at sample index 2; only
            // one source sample remains, so sample 3 of dest is untouched.
            PcmAudio.MixInto(dest, 4, source, 2, long.MaxValue);

            CollectionAssert.AreEqual(new short[] { 1, 2, 23, 4 }, ToShorts(dest));
        }

        [TestMethod]
        public void MixInto_InvalidInputs_NoOp()
        {
            var dest = Samples(5);

            PcmAudio.MixInto(dest, -1, Samples(1), 0, 2);
            PcmAudio.MixInto(dest, 0, null, 0, 2);
            PcmAudio.MixInto(dest, 0, Samples(1), 99, 2);

            CollectionAssert.AreEqual(new short[] { 5 }, ToShorts(dest));
        }

        [TestMethod]
        public void FadeOutTail_RampsToSilenceWithoutTouchingTheHead()
        {
            // 1 second of constant full-scale samples; fade the last half second.
            var pcm = new byte[PcmAudio.BytesPerSecond];
            for (var i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = 0xff;
                pcm[i + 1] = 0x3f; // 16383
            }

            PcmAudio.FadeOutTail(pcm, 0.5);

            short At(int byteOffset) => (short)(pcm[byteOffset] | (pcm[byteOffset + 1] << 8));
            // Head untouched.
            Assert.AreEqual(16383, At(0));
            Assert.AreEqual(16383, At(PcmAudio.BytesPerSecond / 2 - 4));
            // Mid-fade roughly half amplitude; final sample near silence.
            var mid = At(PcmAudio.BytesPerSecond * 3 / 4);
            Assert.IsTrue(mid > 6000 && mid < 10500, $"mid-fade was {mid}");
            Assert.IsTrue(Math.Abs(At(pcm.Length - 2)) < 50);
        }

        [TestMethod]
        public void FadeOutTail_ShortBufferOrInvalid_NoThrow()
        {
            PcmAudio.FadeOutTail(null, 1);
            PcmAudio.FadeOutTail(new byte[2], 1);
            var pcm = Samples(1000, 1000);
            PcmAudio.FadeOutTail(pcm, 0);
            CollectionAssert.AreEqual(new short[] { 1000, 1000 }, ToShorts(pcm));
        }

        [TestMethod]
        public void TicksToAlignedBytes_AlignsToBlockBoundary()
        {
            // 1 second = 192000 bytes; already aligned.
            Assert.AreEqual(192000L, PcmAudio.TicksToAlignedBytes(10_000_000));
            // A fraction that lands mid-frame rounds down to a 4-byte boundary.
            var bytes = PcmAudio.TicksToAlignedBytes(12_345);
            Assert.AreEqual(0, bytes % PcmAudio.BlockAlign);
        }

        [TestMethod]
        public void CancelCorrelated_AlignsAndRemovesGameReference()
        {
            const int frames = 36000;
            const int lag = 17;
            var referenceSamples = new short[frames * 2];
            var mixtureSamples = new short[frames * 2];
            var expectedChime = new short[frames * 2];
            var random = new Random(1234);
            // The sound request precedes the audible chime. Leave the first half-second silent to
            // verify alignment chooses an audible portion instead of rejecting a valid reference.
            for (var frame = 26000; frame < frames; frame++)
            {
                var left = (short)random.Next(-6000, 6001);
                var right = (short)random.Next(-6000, 6001);
                referenceSamples[frame * 2] = left;
                referenceSamples[frame * 2 + 1] = right;
            }

            for (var frame = 0; frame + lag < frames; frame++)
            {
                mixtureSamples[frame * 2] = referenceSamples[(frame + lag) * 2];
                mixtureSamples[frame * 2 + 1] = referenceSamples[(frame + lag) * 2 + 1];
            }

            for (var frame = 30000; frame < 30300; frame++)
            {
                expectedChime[frame * 2] = 1200;
                expectedChime[frame * 2 + 1] = -900;
                mixtureSamples[frame * 2] += 1200;
                mixtureSamples[frame * 2 + 1] -= 900;
            }

            var mixture = Samples(mixtureSamples);
            var reference = Samples(referenceSamples);

            var outcome = PcmAudio.CancelCorrelated(mixture, reference, out var diagnostics);
            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"lag={diagnostics.StartLagMs}ms, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.AreEqual(lag * 1000.0 / 48000.0, diagnostics.StartLagMs, 0.03);
            Assert.IsTrue(diagnostics.Correlation > 0.99, $"correlation was {diagnostics.Correlation}");

            // The fitted gain and fractional lag carry noise-sized epsilons, so the residual is
            // not sample-exact; require it to be far below audibility instead (the game is
            // +/-6000, so an RMS of 16 is about -51 dB relative to it).
            var cancelled = ToShorts(mixture);
            var worst = 0;
            double errorEnergy = 0;
            long count = 0;
            for (var frame = 100; frame < frames - lag; frame++)
            {
                var left = expectedChime[frame * 2] - cancelled[frame * 2];
                var right = expectedChime[frame * 2 + 1] - cancelled[frame * 2 + 1];
                worst = Math.Max(worst, Math.Max(Math.Abs(left), Math.Abs(right)));
                errorEnergy += (double)left * left + (double)right * right;
                count += 2;
            }

            var errorRms = Math.Sqrt(errorEnergy / count);
            Assert.IsTrue(worst <= 64, $"worst residual was {worst}");
            Assert.IsTrue(errorRms <= 16, $"residual rms was {errorRms:0.00}");
        }

        [TestMethod]
        public void CancelCorrelated_ScalesSubtractionToTheMeasuredGain()
        {
            // Field logs showed unity subtraction of a 0.85-gain leak leaving audible residual.
            const int frames = 96000; // 2 s
            const int lag = 960; // 20 ms
            var referenceSamples = BandLimitedNoise(frames, 42, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame + lag < frames; frame++)
            {
                mixtureSamples[frame * 2] = (short)Math.Round(0.85 * referenceSamples[(frame + lag) * 2]);
                mixtureSamples[frame * 2 + 1] = (short)Math.Round(0.85 * referenceSamples[(frame + lag) * 2 + 1]);
            }

            for (var frame = 4800; frame < 5100; frame++)
            {
                mixtureSamples[frame * 2] += 1500;
                mixtureSamples[frame * 2 + 1] -= 1500;
            }

            var mixture = Samples(mixtureSamples);
            var originalEnergy = Energy(mixtureSamples, 10000, frames - lag);

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.AreEqual(0.85, diagnostics.Gain, 0.02);
            // Outside the chime, at least 20 dB of the game must be gone.
            var residualEnergy = Energy(ToShorts(mixture), 10000, frames - lag);
            Assert.IsTrue(
                residualEnergy < originalEnergy * 0.01,
                $"residual energy ratio was {residualEnergy / originalEnergy:0.0000}");
        }

        [TestMethod]
        public void CancelCorrelated_TracksDriftingLagAcrossTheSlice()
        {
            // Field logs showed the inter-stream lag drifting ~33 ppm (18.188 -> 18.354 ms over
            // ~5 s); a single global lag comb-filters the slice tail. The block tracker with
            // fractional-delay subtraction must follow it.
            const int frames = 288000; // 6 s
            var referenceSamples = BandLimitedNoise(frames, 7, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var delay = 873.0 + 10.0 * frame / frames; // ~18.2 ms drifting up ~0.2 ms
                for (var channel = 0; channel < 2; channel++)
                {
                    mixtureSamples[frame * 2 + channel] =
                        (short)Math.Round(0.9 * SampleAt(referenceSamples, frame + delay, channel));
                }
            }

            // A 1 kHz square-ish chime early in the slice, like the real sidecar layout.
            for (var frame = 4800; frame < 14400; frame++)
            {
                var tone = (short)((frame / 24) % 2 == 0 ? 2000 : -2000);
                mixtureSamples[frame * 2] += tone;
                mixtureSamples[frame * 2 + 1] += tone;
            }

            var mixture = Samples(mixtureSamples);
            var originalEnergy = Energy(mixtureSamples, 20000, frames - 1000);

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.IsTrue(
                diagnostics.EndLagMs > diagnostics.StartLagMs,
                $"lag did not track upward: {diagnostics.StartLagMs} -> {diagnostics.EndLagMs}");
            // Outside the chime, at least 10 dB of the game must be gone across the WHOLE slice,
            // tail included.
            var residualEnergy = Energy(ToShorts(mixture), 20000, frames - 1000);
            Assert.IsTrue(
                residualEnergy < originalEnergy * 0.1,
                $"residual energy ratio was {residualEnergy / originalEnergy:0.0000}");
        }

        [TestMethod]
        public void CancelCorrelated_UnrelatedReferenceIsCleanAndUntouched()
        {
            // A loud reference that does not appear in the mixture means the game is not leaking
            // into the sidecar: keep the mixture (it is the chime) rather than dropping it.
            var random = new Random(17);
            var mixtureSamples = Enumerable.Range(0, 96000)
                .Select(_ => (short)random.Next(-8000, 8001))
                .ToArray();
            var referenceSamples = Enumerable.Range(0, 96000)
                .Select(_ => (short)random.Next(-8000, 8001))
                .ToArray();
            var mixture = Samples(mixtureSamples);
            var before = (byte[])mixture.Clone();

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.CleanNoGameDetected,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}");
            CollectionAssert.AreEqual(before, mixture);
        }

        [TestMethod]
        public void CancelCorrelated_ImplausibleGainIsUnseparable()
        {
            // Correlates perfectly but at double amplitude: not the same signal chain, so the
            // mixture must be left alone and the chime omitted.
            const int frames = 48000;
            var referenceSamples = BandLimitedNoise(frames, 5, 6000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                mixtureSamples[frame * 2] = (short)(2 * referenceSamples[frame * 2]);
                mixtureSamples[frame * 2 + 1] = (short)(2 * referenceSamples[frame * 2 + 1]);
            }

            var mixture = Samples(mixtureSamples);
            var before = (byte[])mixture.Clone();

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out _);

            Assert.AreEqual(PcmCancellationOutcome.Unseparable, outcome);
            CollectionAssert.AreEqual(before, mixture);
        }

        [TestMethod]
        public void CancelCorrelated_UnverifiableResidualIsUnseparableAndUntouched()
        {
            // The game is present, but so is loud unidentified audio the reference cannot
            // explain; subtraction cannot verifiably clean the slice, so nothing may ship.
            const int frames = 96000;
            var referenceSamples = BandLimitedNoise(frames, 11, 8000);
            var otherSamples = BandLimitedNoise(frames, 99, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var value = (int)Math.Round(0.9 * referenceSamples[frame * 2 + channel])
                        + otherSamples[frame * 2 + channel];
                    mixtureSamples[frame * 2 + channel] =
                        (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
                }
            }

            var mixture = Samples(mixtureSamples);
            var before = (byte[])mixture.Clone();

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.Unseparable,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            CollectionAssert.AreEqual(before, mixture);
        }

        [TestMethod]
        public void CancelCorrelated_SilentGameReferenceKeepsChime()
        {
            var chime = Samples(1000, -1000, 500, -500);
            var before = (byte[])chime.Clone();

            var outcome = PcmAudio.CancelCorrelated(chime, new byte[chime.Length], out var diagnostics);

            Assert.AreEqual(PcmCancellationOutcome.CleanNoGameDetected, outcome);
            Assert.AreEqual(1, diagnostics.Correlation);
            CollectionAssert.AreEqual(before, chime);
        }
    }
}
