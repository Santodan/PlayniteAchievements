using System;
using System.IO;
using System.Linq;
using System.Text;
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

        private static double DifferenceEnergy(
            short[] actual, short[] expected, int startFrame, int endFrame)
        {
            double energy = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var index = frame * 2 + channel;
                    var difference = actual[index] - expected[index];
                    energy += difference * (double)difference;
                }
            }

            return energy;
        }

        private static double ProjectionEnergy(
            short[] signal, short[] reference, int startFrame, int endFrame)
        {
            double dot = 0;
            double referenceEnergy = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var source = reference[frame * 2 + channel];
                    dot += signal[frame * 2 + channel] * (double)source;
                    referenceEnergy += source * (double)source;
                }
            }

            return referenceEnergy > 0 ? dot * dot / referenceEnergy : 0;
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
            // One frame is 208.333 ticks. The nearest representable tick must still map back to
            // frame one rather than being truncated to three bytes and aligned onto frame zero.
            Assert.AreEqual(PcmAudio.BlockAlign, PcmAudio.TicksToAlignedBytes(208));
            Assert.AreEqual(PcmAudio.BlockAlign, PcmAudio.TicksToAlignedBytes(209));
            Assert.AreEqual(0, PcmAudio.TicksToAlignedBytes(104));
            // Every result is a whole stereo sample frame.
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

            // The fitted gain carries a noise-sized epsilon, so the residual is not sample-exact;
            // require it to be far below audibility instead (the game is
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
        public void CancelCorrelated_HapticStereoFitDoesNotHideOppositeChannelResiduals()
        {
            // DualSense left/right actuator audio can be scaled differently by the endpoint graph.
            // One shared gain averages the two. Its positive residual in one channel and negative
            // residual in the other cancel in a combined projection, falsely looking perfectly
            // clean while both channels still carry an audible copy.
            const int frames = 96000;
            var reference = BandLimitedNoise(frames, 601, 7000);
            var kept = BandLimitedNoise(frames, 887, 1200);
            var mixture = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                mixture[frame * 2] = (short)Math.Round(
                    kept[frame * 2] + 0.95 * reference[frame * 2]);
                mixture[frame * 2 + 1] = (short)Math.Round(
                    kept[frame * 2 + 1] + 0.12 * reference[frame * 2 + 1]);
            }

            var shared = Samples(mixture);
            var sharedOutcome = PcmAudio.CancelCorrelated(
                shared, Samples(reference), out _);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, sharedOutcome);

            var stereo = Samples(mixture);
            var stereoOutcome = PcmAudio.CancelCorrelated(
                stereo, Samples(reference), out var diagnostics,
                independentChannelGains: true,
                gainCrossfadeFrames: 0);
            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                stereoOutcome,
                $"suppression={diagnostics.SuppressionDb:0.0}dB");

            var from = 2400;
            var to = frames - 2400;
            var sharedError = DifferenceEnergy(ToShorts(shared), kept, from, to);
            var stereoError = DifferenceEnergy(ToShorts(stereo), kept, from, to);
            Assert.IsTrue(
                stereoError < sharedError * 0.01,
                $"per-channel residual was {stereoError / sharedError:P2} of shared-gain residual");
        }

        [TestMethod]
        public void CancelCorrelated_ZeroCrossfadeRemovesTheWholeHapticOnset()
        {
            // A 5 ms gain ramp is useful between ordinary audio blocks, but it intentionally leaves
            // the leading edge of a short controller burst behind. Stamped haptics need direct
            // subtraction from their first active frame.
            const int frames = 48000;
            const int burstStart = 2400;
            const int burstEnd = 4800;
            var reference = new short[frames * 2];
            var burst = BandLimitedNoise(burstEnd - burstStart, 733, 8000);
            for (var frame = burstStart; frame < burstEnd; frame++)
            {
                var sourceFrame = frame - burstStart;
                reference[frame * 2] = burst[sourceFrame * 2];
                reference[frame * 2 + 1] = burst[sourceFrame * 2 + 1];
            }

            var mixture = reference
                .Select(sample => (short)Math.Round(sample * 0.8))
                .ToArray();

            var faded = Samples(mixture);
            var fadedOutcome = PcmAudio.CancelCorrelated(
                faded, Samples(reference), out _,
                cancellationBlockFrames: 2400,
                independentChannelGains: true);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, fadedOutcome);

            var direct = Samples(mixture);
            var directOutcome = PcmAudio.CancelCorrelated(
                direct, Samples(reference), out _,
                cancellationBlockFrames: 2400,
                independentChannelGains: true,
                gainCrossfadeFrames: 0);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, directOutcome);

            var fadedOnset = Energy(ToShorts(faded), burstStart, burstStart + 240);
            var directOnset = Energy(ToShorts(direct), burstStart, burstStart + 240);
            Assert.IsTrue(fadedOnset > 0, "the fixture did not exercise the default onset ramp");
            Assert.IsTrue(
                directOnset < fadedOnset * 0.001,
                $"direct onset residual was {directOnset / fadedOnset:P2} of faded residual");
        }

        [TestMethod]
        public void CancelCorrelated_OneFractionalLagRemovesNearestFrameResidual()
        {
            // Packet timestamps place both tracks on the same frame grid, but independent audio
            // engines can present the waveform between frame centres. Nearest-frame subtraction
            // leaves a quiet phasey copy; one fixed fractional calibration removes it without any
            // block-by-block lag changes.
            const int frames = 96000;
            const double lag = 347.375;
            var reference = BandLimitedNoise(frames, 811, 9000);
            var mixture = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    mixture[frame * 2 + channel] =
                        (short)Math.Round(0.9 * SampleAt(reference, frame + lag, channel));
                }
            }

            var nearest = Samples(mixture);
            var nearestOutcome = PcmAudio.CancelCorrelated(
                nearest, Samples(reference), out _,
                independentChannelGains: true,
                gainCrossfadeFrames: 0);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, nearestOutcome);

            var fractional = Samples(mixture);
            var fractionalOutcome = PcmAudio.CancelCorrelated(
                fractional, Samples(reference), out var diagnostics,
                independentChannelGains: true,
                gainCrossfadeFrames: 0,
                fractionalLagSteps: 32);
            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                fractionalOutcome,
                $"lag={diagnostics.StartLagMs:0.0000}ms supp={diagnostics.SuppressionDb:0.0}dB");
            Assert.AreEqual(lag * 1000.0 / 48000.0, diagnostics.StartLagMs, 0.0004);
            Assert.AreEqual(diagnostics.StartLagMs, diagnostics.EndLagMs, 0.000001);

            var from = 2400;
            var to = frames - 2400;
            var nearestResidual = Energy(ToShorts(nearest), from, to);
            var fractionalResidual = Energy(ToShorts(fractional), from, to);
            Assert.IsTrue(
                fractionalResidual < nearestResidual * 0.01,
                $"fractional residual was {fractionalResidual / nearestResidual:P2} of nearest-frame residual");
        }

        [TestMethod]
        public void CancelCorrelated_UsesOneCalibratedLagAcrossTheSlice()
        {
            // A small device-clock drift must not bring back per-block lag chasing. The calibrated
            // timestamp offset stays fixed; blocks that cannot verify at it use the existing
            // restore/mute policy instead of moving every later sample on weak local evidence.
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
            Assert.AreEqual(diagnostics.StartLagMs, diagnostics.EndLagMs, 0.0001);
            // This fixture deliberately violates the stamped-timeline contract. The fixed pass
            // must still make a bounded best effort without changing its alignment mid-slice.
            var residualEnergy = Energy(ToShorts(mixture), 20000, frames - 1000);
            Assert.IsTrue(
                residualEnergy < originalEnergy * 0.6,
                $"residual energy ratio was {residualEnergy / originalEnergy:0.0000}");
            Assert.IsTrue(diagnostics.FixedFitBlocks > 0);
        }

        [TestMethod]
        public void CancelCorrelated_ChimeCalibrationPrefersTheEarlyGraphState()
        {
            // Starting a chime render stream can change the process-tree capture graph's game
            // latency. Calibrate inside the early chime passage rather than selecting the later
            // game-only region merely because it has perfect correlation.
            const int frames = 216000; // 4.5 s, the production slice length
            const int lagBefore = 250; // ~5.2 ms
            const int lagAfter = 340; // ~7.1 ms: a +1.9 ms step at half-way
            var referenceSamples = BandLimitedNoise(frames, 23, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var lag = frame < frames / 2 ? lagBefore : lagAfter;
                for (var channel = 0; channel < 2; channel++)
                {
                    var referenceFrame = frame + lag;
                    if (referenceFrame < frames)
                    {
                        mixtureSamples[frame * 2 + channel] =
                            (short)Math.Round(0.9 * referenceSamples[referenceFrame * 2 + channel]);
                    }
                }
            }

            for (var frame = 4800; frame < 14400; frame++)
            {
                var tone = (short)((frame / 24) % 2 == 0 ? 1500 : -1500);
                mixtureSamples[frame * 2] += tone;
                mixtureSamples[frame * 2 + 1] += tone;
            }

            var mixture = Samples(mixtureSamples);
            var earlyOriginal = Energy(mixtureSamples, 20000, frames / 2 - 1000);

            var outcome = PcmAudio.CancelCorrelated(
                mixture,
                Samples(referenceSamples),
                out var diagnostics,
                preferEarlyAlignmentWindow: true,
                verificationLagRadiusFrames: 480);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.AreEqual(lagBefore * 1000.0 / 48000.0, diagnostics.StartLagMs, 0.03);
            Assert.AreEqual(diagnostics.StartLagMs, diagnostics.EndLagMs, 0.0001);
            var earlyResidual = Energy(ToShorts(mixture), 20000, frames / 2 - 1000);
            Assert.IsTrue(
                earlyResidual < earlyOriginal * 0.1,
                $"early residual ratio was {earlyResidual / earlyOriginal:0.0000}");

            // The unrelated early chime must survive; a wrong later calibration would classify
            // these blocks as failed and mute the very sound the sidecar exists to preserve.
            var cleaned = ToShorts(mixture);
            var chimeEnergy = Energy(cleaned, 4800, 14400);
            Assert.IsTrue(chimeEnergy > 20_000_000_000d, $"chime energy was {chimeEnergy:0}");
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
            Assert.IsTrue(diagnostics.ReferenceHasSignal);
            Assert.IsTrue(diagnostics.ReferenceRms > 16);
            CollectionAssert.AreEqual(before, mixture);
        }

        [TestMethod]
        public void HapticSafety_RequiresVerifiedCancellationForAnActiveReference()
        {
            var silent = new PcmCancellationDiagnostics { ReferenceHasSignal = false };
            var active = new PcmCancellationDiagnostics { ReferenceHasSignal = true };

            Assert.IsTrue(PcmAudio.IsReferenceSafelyAbsentOrRemoved(
                PcmCancellationOutcome.CleanNoGameDetected, silent));
            Assert.IsFalse(PcmAudio.IsReferenceSafelyAbsentOrRemoved(
                PcmCancellationOutcome.CleanNoGameDetected, active));
            Assert.IsFalse(PcmAudio.IsReferenceSafelyAbsentOrRemoved(
                PcmCancellationOutcome.Unseparable, active));
            Assert.IsTrue(PcmAudio.IsReferenceSafelyAbsentOrRemoved(
                PcmCancellationOutcome.CancelledVerified, active));
        }

        [TestMethod]
        public void CancelCorrelated_ResidualCeilingRejectsWithoutChangingTheRecording()
        {
            const int frames = 96000;
            var reference = BandLimitedNoise(frames, 741, 8000);
            var unrelated = BandLimitedNoise(frames, 912, 5000);
            var samples = new short[frames * 2];
            for (var sample = 0; sample < samples.Length; sample++)
            {
                samples[sample] = (short)Math.Round(0.9 * reference[sample] + unrelated[sample]);
            }

            var permissive = Samples(samples);
            var permissiveOutcome = PcmAudio.CancelCorrelated(
                permissive, Samples(reference), out var permissiveDiagnostics);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, permissiveOutcome);
            Assert.IsTrue(
                permissiveDiagnostics.ResidualCorrelation > 0,
                "the deterministic unrelated component should leave a measurable projection");

            var guarded = Samples(samples);
            var original = (byte[])guarded.Clone();
            var guardedOutcome = PcmAudio.CancelCorrelated(
                guarded,
                Samples(reference),
                out var guardedDiagnostics,
                maximumResidualCorrelation: permissiveDiagnostics.ResidualCorrelation / 2);

            Assert.AreEqual(PcmCancellationOutcome.Unseparable, guardedOutcome);
            Assert.IsTrue(
                guardedDiagnostics.ResidualCorrelation >
                    permissiveDiagnostics.ResidualCorrelation / 2);
            CollectionAssert.AreEqual(original, guarded);
        }

        [TestMethod]
        public void CancelCorrelated_FailedSecondReferencePreservesFirstVerifiedCleanup()
        {
            // Two DualSense endpoints were active in the field log. The first pass removed its
            // reference by 35 dB, while the second could not be identified. Each pass is a
            // transaction: rejecting the second must not undo the first or damage the game audio.
            const int frames = 192000;
            var firstReference = BandLimitedNoise(frames, 741, 7000);
            var secondReference = BandLimitedNoise(frames, 333, 7000);
            var game = BandLimitedNoise(frames, 912, 4000);
            var samples = new short[frames * 2];
            for (var sample = 0; sample < samples.Length; sample++)
            {
                samples[sample] = (short)Math.Round(
                    0.9 * firstReference[sample] + 0.4 * game[sample]);
            }

            var working = Samples(samples);
            var firstOutcome = PcmAudio.CancelCorrelated(
                working,
                Samples(firstReference),
                out var firstDiagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000,
                minimumGain: 0.005,
                maximumGain: 20,
                blockGainFloor: 0.005,
                keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400,
                maximumResidualCorrelation: 0.35);
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, firstOutcome);
            Assert.IsTrue(firstDiagnostics.SubtractedBlocks > 0);

            var afterFirst = (byte[])working.Clone();
            var secondOutcome = PcmAudio.CancelCorrelated(
                working,
                Samples(secondReference),
                out var secondDiagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000,
                minimumGain: 0.005,
                maximumGain: 20,
                blockGainFloor: 0.005,
                keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400,
                maximumResidualCorrelation: 0.35);

            Assert.IsFalse(PcmAudio.IsReferenceSafelyAbsentOrRemoved(
                secondOutcome, secondDiagnostics));
            CollectionAssert.AreEqual(
                afterFirst,
                working,
                "A rejected reference pass must leave all earlier verified cleanup intact.");

            var beforeProjection = ProjectionEnergy(samples, firstReference, 2400, frames - 2400);
            var afterProjection = ProjectionEnergy(
                ToShorts(working), firstReference, 2400, frames - 2400);
            Assert.IsTrue(
                afterProjection < beforeProjection * 0.02,
                $"first-reference residual ratio was {afterProjection / beforeProjection:0.0000}");

            var keptEnergy = Energy(ToShorts(working), 2400, frames - 2400);
            var expectedGameEnergy = Energy(game, 2400, frames - 2400) * 0.16;
            Assert.IsTrue(
                keptEnergy > expectedGameEnergy * 0.5,
                "Verified cleanup removed too much of the unrelated game audio.");
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
        public void CancelCorrelated_MutesTheBlockWhereRemovalCannotBeVerified()
        {
            // A pump tear can leave one block whose audio the reference no longer explains at any
            // findable lag. The pass must still verify overall, but that block must ship as
            // silence, never as wrong-time game audio inside the chime.
            const int frames = 216000; // 4.5 s slice, nine 0.5 s blocks
            const int lag = 480;
            const int tornStart = 96000; // block 4
            const int tornEnd = 120000;
            var referenceSamples = BandLimitedNoise(frames, 31, 8000);
            var tornSamples = BandLimitedNoise(frames, 77, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var torn = frame >= tornStart && frame < tornEnd;
                for (var channel = 0; channel < 2; channel++)
                {
                    mixtureSamples[frame * 2 + channel] = torn
                        ? tornSamples[frame * 2 + channel]
                        : frame + lag < frames
                            ? (short)Math.Round(0.9 * referenceSamples[(frame + lag) * 2 + channel])
                            : (short)0;
                }
            }

            var mixture = Samples(mixtureSamples);
            var tornOriginal = Energy(mixtureSamples, tornStart + 2400, tornEnd - 2400);

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.IsTrue(diagnostics.MutedBlocks >= 1, $"muted {diagnostics.MutedBlocks} blocks");
            // Inside the torn block (edges excluded for the ramps) the output is silence.
            var tornResidual = Energy(ToShorts(mixture), tornStart + 2400, tornEnd - 2400);
            Assert.IsTrue(
                tornResidual < tornOriginal * 0.01,
                $"torn block residual ratio was {tornResidual / tornOriginal:0.0000}");
        }

        [TestMethod]
        public void CancelCorrelated_KeepsUnverifiedBlocksWhenMutingIsOff()
        {
            // Same tear as above, but on the track that IS the clip's audio (the haptic pass). A
            // hole punched in the game's own sound is worse than the residual it would remove, so
            // an unverifiable block has to survive the pass intact.
            const int frames = 216000;
            const int lag = 480;
            const int tornStart = 96000;
            const int tornEnd = 120000;
            var referenceSamples = BandLimitedNoise(frames, 31, 8000);
            var tornSamples = BandLimitedNoise(frames, 77, 8000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var torn = frame >= tornStart && frame < tornEnd;
                for (var channel = 0; channel < 2; channel++)
                {
                    mixtureSamples[frame * 2 + channel] = torn
                        ? tornSamples[frame * 2 + channel]
                        : frame + lag < frames
                            ? (short)Math.Round(0.9 * referenceSamples[(frame + lag) * 2 + channel])
                            : (short)0;
                }
            }

            var mixture = Samples(mixtureSamples);
            var tornOriginal = Energy(mixtureSamples, tornStart + 2400, tornEnd - 2400);

            var outcome = PcmAudio.CancelCorrelated(
                mixture, Samples(referenceSamples), out var diagnostics, muteUnverifiedBlocks: false);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            Assert.AreEqual(0, diagnostics.MutedBlocks);
            var tornResidual = Energy(ToShorts(mixture), tornStart + 2400, tornEnd - 2400);
            Assert.IsTrue(
                tornResidual > tornOriginal * 0.5,
                $"unverified block kept {tornResidual / tornOriginal:0.0000} of its energy");
        }

        [TestMethod]
        public void CancelCorrelated_FindsAReferenceBeyondTheDefaultSearchWhenAllowed()
        {
            // A controller endpoint's capture client can sit far further from the main capture than
            // the chime's two process clients do — field logs showed 47 ms against a 50 ms search.
            // Past that edge the reference is simply not found and the buzz ships untouched, so the
            // haptic pass asks for a wider search; this pins that the width is what decides it.
            const int frames = 192000; // 4 s
            const int lag = 5760; // 120 ms
            var referenceSamples = BandLimitedNoise(frames, 31, 8000);
            var otherSamples = BandLimitedNoise(frames, 77, 3000);
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var value = (int)otherSamples[frame * 2 + channel];
                    if (frame + lag < frames)
                    {
                        value += (int)Math.Round(0.9 * referenceSamples[(frame + lag) * 2 + channel]);
                    }

                    mixtureSamples[frame * 2 + channel] =
                        (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
                }
            }

            var reference = Samples(referenceSamples);

            var narrow = Samples(mixtureSamples);
            var narrowOutcome = PcmAudio.CancelCorrelated(narrow, reference, out _);
            Assert.AreNotEqual(
                PcmCancellationOutcome.CancelledVerified,
                narrowOutcome,
                "the default search must not reach a 120 ms lag");

            var wide = Samples(mixtureSamples);
            var outcome = PcmAudio.CancelCorrelated(
                wide, reference, out var diagnostics, muteUnverifiedBlocks: false, maxLagFrames: 12000);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"lag={diagnostics.StartLagMs}ms correlation={diagnostics.Correlation}");
            Assert.AreEqual(lag * 1000.0 / 48000.0, diagnostics.StartLagMs, 1.0);
            Assert.IsTrue(
                diagnostics.ResidualCorrelation < 0.15,
                $"residual correlation was {diagnostics.ResidualCorrelation:0.000}");
            Assert.AreEqual(0, diagnostics.MutedBlocks);
            Assert.IsTrue(diagnostics.SubtractedBlocks > 0, "no block was subtracted");

            // The audio that was not the reference has to come through untouched.
            var keptBefore = Energy(otherSamples, 2000, frames - 8000);
            var keptAfter = Energy(ToShorts(wide), 2000, frames - 8000);
            Assert.IsTrue(
                keptAfter > keptBefore * 0.7 && keptAfter < keptBefore * 1.3,
                $"kept-audio energy ratio was {keptAfter / keptBefore:0.000}");
        }

        [TestMethod]
        public void CancelCorrelated_BlockGainFloorDecidesWhetherAFaintCopyIsRemoved()
        {
            // The clip's copy of a reference is loud in some stretches and faint in others. The
            // chime's floor exists so a block with nothing to remove is left alone, but for haptics
            // it left the quiet stretches in — the field log showed 27 of 48 blocks subtracted, which
            // is what "soft haptics in the background" was.
            const int frames = 480000; // 10 s, twenty blocks
            var referenceSamples = BandLimitedNoise(frames, 31, 9000);
            var quietFrom = frames / 2;
            var mixtureSamples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var gain = frame < quietFrom ? 0.9 : 0.02;
                for (var channel = 0; channel < 2; channel++)
                {
                    mixtureSamples[frame * 2 + channel] =
                        (short)Math.Round(gain * referenceSamples[frame * 2 + channel]);
                }
            }

            var reference = Samples(referenceSamples);
            var faintFrom = quietFrom + 24000;
            var faintTo = frames - 24000;
            var faintBefore = Energy(mixtureSamples, faintFrom, faintTo);

            var atDefaultFloor = Samples(mixtureSamples);
            PcmAudio.CancelCorrelated(
                atDefaultFloor, reference, out var defaultDiagnostics, muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.05, maximumGain: 20.0);

            var atLowFloor = Samples(mixtureSamples);
            PcmAudio.CancelCorrelated(
                atLowFloor, reference, out var lowDiagnostics, muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.05, maximumGain: 20.0, blockGainFloor: 0.005);

            Assert.IsTrue(
                defaultDiagnostics.QuietBlocks > 0,
                "the default floor was expected to skip the faint blocks");
            Assert.IsTrue(
                lowDiagnostics.SubtractedBlocks > defaultDiagnostics.SubtractedBlocks,
                $"lower floor covered {lowDiagnostics.SubtractedBlocks} blocks, " +
                $"default covered {defaultDiagnostics.SubtractedBlocks}");

            // The faint copy survives the default floor untouched and is gone under the lower one.
            var faintAtDefault = Energy(ToShorts(atDefaultFloor), faintFrom, faintTo);
            var faintAtLow = Energy(ToShorts(atLowFloor), faintFrom, faintTo);
            Assert.IsTrue(faintAtDefault > faintBefore * 0.9, "the default floor should leave it in");
            Assert.IsTrue(
                faintAtLow < faintBefore * 0.01,
                $"faint-region energy ratio was {faintAtLow / faintBefore:0.0000}");
        }

        [TestMethod]
        public void CancelCorrelated_ShortBlocksRemoveSparseHapticBursts()
        {
            // The user log had four half-second blocks with a real reference but no credible fit.
            // Haptic energy is impulsive: dilute a 25 ms burst with 475 ms of unrelated game audio
            // and the old block correlation misses it, even though it is obvious while active.
            const int frames = 192000; // 4 s
            var reference = BandLimitedNoise(frames, 311, 10000);
            var game = BandLimitedNoise(frames, 712, 10000);
            var mixture = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var strongOpening = frame < 36000;
                var burst = frame >= 48000 && (frame % 12000) < 1200; // 25 ms every 250 ms
                for (var channel = 0; channel < 2; channel++)
                {
                    var kept = (int)Math.Round(0.75 * game[frame * 2 + channel]);
                    var haptic = strongOpening
                        ? (int)Math.Round(0.9 * reference[frame * 2 + channel])
                        : burst
                            ? (int)Math.Round(0.2 * reference[frame * 2 + channel])
                            : 0;
                    if (!strongOpening && !burst)
                    {
                        // Endpoint loopback carries silence when no haptic waveform is rendered.
                        reference[frame * 2 + channel] = 0;
                    }
                    mixture[frame * 2 + channel] = (short)Math.Max(
                        short.MinValue, Math.Min(short.MaxValue, kept + haptic));
                    game[frame * 2 + channel] = (short)kept;
                }
            }

            var shortBlocks = Samples(mixture);
            var outcome = PcmAudio.CancelCorrelated(
                shortBlocks, Samples(reference), out var diagnostics,
                muteUnverifiedBlocks: true,
                maxLagFrames: 12000,
                minimumGain: 0.005,
                maximumGain: 20,
                blockGainFloor: 0.005,
                keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"corr={diagnostics.Correlation:0.000}, gain={diagnostics.Gain:0.000}, " +
                $"lag={diagnostics.StartLagMs:0.000}->{diagnostics.EndLagMs:0.000}, " +
                $"blocks={diagnostics.SubtractedBlocks}/{diagnostics.TotalBlocks}, " +
                $"restored={diagnostics.RestoredBlocks}, " +
                $"suppression={diagnostics.SuppressionDb:0.0}");
            var start = 48000;
            var originalProjection = ProjectionEnergy(
                mixture, reference, start, frames - 2400);
            var newProjection = ProjectionEnergy(
                ToShorts(shortBlocks), reference, start, frames - 2400);
            Assert.IsTrue(
                newProjection < originalProjection * 0.01,
                $"short-block correlated residual ratio was {newProjection / originalProjection:0.0000}; " +
                $"muted={diagnostics.MutedBlocks} subtracted={diagnostics.SubtractedBlocks}");

            // Sidecar fallback may gate a locally inseparable active burst, but it must leave the
            // majority of the unrelated game track intact rather than muting the whole clip.
            var keptEnergy = Energy(ToShorts(shortBlocks), start, frames - 2400);
            var gameEnergy = Energy(game, start, frames - 2400);
            Assert.IsTrue(
                keptEnergy > gameEnergy * 0.45,
                $"sparse removal kept only {keptEnergy / gameEnergy:0.000} of game energy");
        }

        [TestMethod]
        public void CancelCorrelated_BestEffortKeepsVerifiedBlocksWhenMostCannotBeVerified()
        {
            // A reference is positively identified in the opening blocks, then a timestamp tear
            // makes most active blocks impossible to subtract. Normal mode rejects the whole pass
            // and leaves the reference in place; haptic best effort commits the verified work and
            // restores every block it cannot prove clean.
            const int frames = 96000; // 2 s
            var reference = BandLimitedNoise(frames, 91, 9000);
            var mixture = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    mixture[frame * 2 + channel] = frame < 24000
                        ? (short)Math.Round(0.9 * reference[frame * 2 + channel])
                        : (short)0;
                }
            }

            var ordinary = Samples(mixture);
            var ordinaryOutcome = PcmAudio.CancelCorrelated(
                ordinary, Samples(reference), out _, muteUnverifiedBlocks: true,
                maxLagFrames: 12000, minimumGain: 0.005, maximumGain: 20,
                blockGainFloor: 0.005, keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400);
            Assert.AreEqual(PcmCancellationOutcome.Unseparable, ordinaryOutcome);
            CollectionAssert.AreEqual(Samples(mixture), ordinary);

            var bestEffort = Samples(mixture);
            var bestEffortOutcome = PcmAudio.CancelCorrelated(
                bestEffort, Samples(reference), out var bestEffortDiagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.005, maximumGain: 20,
                blockGainFloor: 0.005, keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400,
                commitVerifiedBlocksOnWeakPass: true);

            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, bestEffortOutcome);
            Assert.IsTrue(bestEffortDiagnostics.PartialCommit);
            Assert.AreEqual(0, bestEffortDiagnostics.MutedBlocks);
            Assert.IsTrue(bestEffortDiagnostics.SubtractedBlocks > 0);

            var bestEffortSamples = ToShorts(bestEffort);
            var openingBefore = Energy(mixture, 2400, 21600);
            var openingAfter = Energy(bestEffortSamples, 2400, 21600);
            Assert.IsTrue(
                openingAfter < openingBefore * 0.02,
                $"verified opening residual ratio was {openingAfter / openingBefore:0.0000}");

            // The three-quarters of the reference that never appeared in the mixture must remain
            // untouched and, critically, must not be turned into silence gates.
            var tailBefore = Energy(mixture, 28800, frames - 2400);
            var tailAfter = Energy(bestEffortSamples, 28800, frames - 2400);
            Assert.AreEqual(tailBefore, tailAfter, tailBefore * 0.0001 + 1);
        }

        [TestMethod]
        public void CancelCorrelated_HapticWeakGlobalGateFindsStrongSparseBlocks()
        {
            // Field-shaped hap1: its latest global fits were correlation 0.180-0.182 / gain
            // 0.05-0.08 and therefore hit the generic "already clean" shortcut at blocks=0/0,
            // even though individual 50 ms haptic bursts can be an excellent local match. One
            // active block per half-second reproduces that duty-cycle dilution.
            const int frames = 96000;
            var reference = BandLimitedNoise(frames, 171, 7000);
            var game = BandLimitedNoise(frames, 313, 4000);
            var mixture = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var burst = frame % 24000 < 2400;
                for (var channel = 0; channel < 2; channel++)
                {
                    var sample = (int)Math.Round(0.4 * game[frame * 2 + channel]);
                    if (burst)
                    {
                        sample += (int)Math.Round(0.5 * reference[frame * 2 + channel]);
                    }

                    mixture[frame * 2 + channel] = (short)sample;
                }
            }

            var ordinary = Samples(mixture);
            var ordinaryOutcome = PcmAudio.CancelCorrelated(
                ordinary, Samples(reference), out var ordinaryDiagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.005, maximumGain: 20,
                blockGainFloor: 0.005, keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400,
                commitVerifiedBlocksOnWeakPass: true);
            Assert.AreEqual(PcmCancellationOutcome.CleanNoGameDetected, ordinaryOutcome);
            Assert.AreEqual(0, ordinaryDiagnostics.TotalBlocks);
            Assert.IsTrue(
                ordinaryDiagnostics.Correlation >= 0.15 && ordinaryDiagnostics.Correlation < 0.20,
                $"fixture global correlation was {ordinaryDiagnostics.Correlation:0.000}");
            Assert.IsTrue(
                ordinaryDiagnostics.Gain >= 0.03 && ordinaryDiagnostics.Gain < 0.09,
                $"fixture global gain was {ordinaryDiagnostics.Gain:0.000}");

            var haptic = Samples(mixture);
            var outcome = PcmAudio.CancelCorrelated(
                haptic, Samples(reference), out var diagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.005, maximumGain: 20,
                blockGainFloor: 0.005, keepBlockSuppressionDb: 15,
                cancellationBlockFrames: 2400,
                commitVerifiedBlocksOnWeakPass: true,
                minimumCorrelation: 0.15,
                attemptVerifiedBlocksWhenGloballyClean: true);

            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, outcome);
            Assert.IsTrue(diagnostics.PartialCommit);
            Assert.IsTrue(diagnostics.SubtractedBlocks > 0);
            Assert.IsTrue(diagnostics.SuppressionDb >= 15);
            Assert.AreEqual(0, diagnostics.MutedBlocks);

            var cleaned = ToShorts(haptic);
            double beforeBurst = 0;
            double afterBurst = 0;
            double beforeBetween = 0;
            double afterBetween = 0;
            for (var frame = 2400; frame < frames - 2400; frame++)
            {
                var burst = frame % 24000 < 2400;
                for (var channel = 0; channel < 2; channel++)
                {
                    var index = frame * 2 + channel;
                    if (burst)
                    {
                        beforeBurst += (double)mixture[index] * mixture[index];
                        afterBurst += (double)cleaned[index] * cleaned[index];
                    }
                    else
                    {
                        beforeBetween += (double)mixture[index] * mixture[index];
                        afterBetween += (double)cleaned[index] * cleaned[index];
                    }
                }
            }

            Assert.IsTrue(
                afterBurst < beforeBurst * 0.25,
                $"sparse burst energy ratio was {afterBurst / beforeBurst:0.000}");
            Assert.AreEqual(
                beforeBetween,
                afterBetween,
                beforeBetween * 0.001 + 1,
                $"unrelated-region energy changed by " +
                $"{Math.Abs(afterBetween - beforeBetween) / beforeBetween:P3}");
        }

        [TestMethod]
        public void CancelCorrelated_TimestampAlignedFitTriesEverySubThresholdBlockSafely()
        {
            // A listener can still hear sparse haptics after the globally obvious blocks are gone.
            // Model a real copy that sits below the ordinary local correlation floor. The ordinary
            // policy leaves it alone; the stamped haptic path straight-subtracts every active block
            // at the slice-wide calibrated lag/gain and still proves suppression before committing.
            const int frames = 48000;
            var reference = new short[frames * 2];
            var game = new short[frames * 2];
            var referenceRandom = new Random(557);
            var gameRandom = new Random(991);
            var mixture = new short[frames * 2];
            for (var i = 0; i < mixture.Length; i++)
            {
                reference[i] = (short)referenceRandom.Next(-7000, 7001);
                game[i] = (short)gameRandom.Next(-8000, 8001);
                mixture[i] = (short)(game[i] + Math.Round(0.2 * reference[i]));
            }

            var polished = Samples(mixture);
            var outcome = PcmAudio.CancelCorrelated(
                polished, Samples(reference), out var diagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: 12000, minimumGain: 0.005, maximumGain: 20,
                blockGainFloor: 0.005, keepBlockSuppressionDb: 10,
                cancellationBlockFrames: 2400,
                commitVerifiedBlocksOnWeakPass: true,
                minimumCorrelation: 0.15,
                attemptVerifiedBlocksWhenGloballyClean: true);

            Assert.IsTrue(
                diagnostics.Correlation >= 0.15 && diagnostics.Correlation < 0.20,
                $"fixture global correlation was {diagnostics.Correlation:0.000}");
            Assert.AreEqual(PcmCancellationOutcome.CancelledVerified, outcome);
            Assert.AreEqual(diagnostics.TotalBlocks, diagnostics.FixedFitBlocks);
            Assert.IsTrue(diagnostics.SubtractedBlocks > 0);
            Assert.IsTrue(diagnostics.SuppressionDb >= 10);
            Assert.AreEqual(0, diagnostics.MutedBlocks);
            Assert.IsTrue(
                Math.Abs(diagnostics.EndLagMs - diagnostics.StartLagMs) < 0.2,
                $"fixed fit wandered {diagnostics.StartLagMs:0.000}->" +
                $"{diagnostics.EndLagMs:0.000}ms");
        }

        [TestMethod]
        public void WriteWav_WritesAReadableHeaderForTheExportFormat()
        {
            // The cleaned clip audio goes back to the exporter as an ordinary chunk file, so the
            // header has to describe exactly the format the rest of the pipeline assumes.
            var path = Path.Combine(Path.GetTempPath(), $"pa_wav_{Guid.NewGuid():N}.wav");
            var pcm = Samples(BandLimitedNoise(1200, 3, 4000));
            try
            {
                PcmAudio.WriteWav(path, pcm);
                var written = File.ReadAllBytes(path);

                Assert.AreEqual(44 + pcm.Length, written.Length);
                Assert.AreEqual("RIFF", Encoding.ASCII.GetString(written, 0, 4));
                Assert.AreEqual("WAVE", Encoding.ASCII.GetString(written, 8, 4));
                Assert.AreEqual("fmt ", Encoding.ASCII.GetString(written, 12, 4));
                Assert.AreEqual(1, BitConverter.ToInt16(written, 20));                       // PCM
                Assert.AreEqual(PcmAudio.Channels, BitConverter.ToInt16(written, 22));
                Assert.AreEqual(PcmAudio.SampleRate, BitConverter.ToInt32(written, 24));
                Assert.AreEqual(PcmAudio.BytesPerSecond, BitConverter.ToInt32(written, 28));
                Assert.AreEqual(PcmAudio.BlockAlign, BitConverter.ToInt16(written, 32));
                Assert.AreEqual(PcmAudio.BitsPerSample, BitConverter.ToInt16(written, 34));
                Assert.AreEqual("data", Encoding.ASCII.GetString(written, 36, 4));
                Assert.AreEqual(pcm.Length, BitConverter.ToInt32(written, 40));
                CollectionAssert.AreEqual(pcm, written.Skip(44).ToArray());
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void CancelCorrelated_RemovesGameComponentAndKeepsUncorrelatedAudio()
        {
            // The mixture holds the game plus loud audio the reference cannot explain (in
            // production: the chime itself, at any loudness). Verification measures suppression of
            // the reference-CORRELATED component, so the pass must remove the game and keep the
            // rest — a raw energy gate here would wrongly drop every chime-dominated slice.
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
            var otherEnergy = Energy(otherSamples, 1000, frames - 1000);

            var outcome = PcmAudio.CancelCorrelated(mixture, Samples(referenceSamples), out var diagnostics);

            Assert.AreEqual(
                PcmCancellationOutcome.CancelledVerified,
                outcome,
                $"gain={diagnostics.Gain}, correlation={diagnostics.Correlation}, suppression={diagnostics.SuppressionDb}dB");
            // The residual is the uncorrelated audio, near-unchanged: the game's share is gone.
            var residualEnergy = Energy(ToShorts(mixture), 1000, frames - 1000);
            Assert.IsTrue(
                residualEnergy > otherEnergy * 0.7 && residualEnergy < otherEnergy * 1.3,
                $"residual/other energy ratio was {residualEnergy / otherEnergy:0.000}");
        }

        [TestMethod]
        public void CancelCorrelated_SilentGameReferenceKeepsChime()
        {
            var chime = Samples(1000, -1000, 500, -500);
            var before = (byte[])chime.Clone();

            var outcome = PcmAudio.CancelCorrelated(chime, new byte[chime.Length], out var diagnostics);

            Assert.AreEqual(PcmCancellationOutcome.CleanNoGameDetected, outcome);
            Assert.AreEqual(1, diagnostics.Correlation);
            Assert.IsFalse(diagnostics.ReferenceHasSignal);
            Assert.AreEqual(0, diagnostics.ReferenceRms);
            CollectionAssert.AreEqual(before, chime);
        }
    }
}
