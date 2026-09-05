using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Services.Capture
{
    [TestClass]
    public class ChimeRemovalEngineTests
    {
        private const int Rate = PcmAudio.SampleRate;

        [TestMethod]
        public void ResolvedFileRemovesLiveChimeWithoutCapturedSidecar()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 901, 900);
            var chime = Chime(Rate, 440, 7000, 17);
            var endpoint = (short[])game.Clone();
            var launch = Rate / 2;
            var rendered = launch + 1733;
            Add(endpoint, chime, rendered);
            var original = Bytes(endpoint);

            var result = ChimeRemovalEngine.RemoveAll(
                original,
                null,
                null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L, (launch + chime.Length) * 4L, Bytes(chime)),
                });

            Assert.IsTrue(result.Verified, result.FailureReason);
            Assert.IsTrue(result.Changed);
            AssertChimeSuppressed(result.CleanedPcm, game, chime, rendered, -20, Describe(result));
            AssertGamePreserved(result.CleanedPcm, game, -35);
            CollectionAssert.AreEqual(Bytes(endpoint), original, "the input is transactionally immutable");
        }

        [TestMethod]
        public void QuietLiveChimeBelowOrdinaryPresenceGateCannotAuthorizeReplacement()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 909, 1000);
            var sound = Chime(Rate, 577, 6500, 141);
            var launch = Rate;
            var endpoint = (short[])game.Clone();
            AddScaled(endpoint, sound, launch, 0.05);
            var input = Bytes(endpoint);
            var before = (byte[])input.Clone();

            var result = ChimeRemovalEngine.RemoveAll(
                input, null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });

            Assert.IsFalse(result.Verified, Describe(result));
            Assert.IsNull(result.CleanedPcm);
            Assert.IsTrue(
                result.Attempts.Any(a => a.ReferenceKind == "resolved-file-residual"),
                Describe(result));
            CollectionAssert.AreEqual(before, input);
        }

        [TestMethod]
        public void CapturedReferenceAmbiguousAbsenceFailsClosedWithoutChangingGame()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 910, 900);
            var capturedChime = Chime(Rate, 811, 5000, 151);
            var captured = new short[frames * 2];
            Add(captured, capturedChime, Rate);
            var input = Bytes(game);

            var result = ChimeRemovalEngine.RemoveAll(
                input,
                Bytes(captured),
                Bytes(game),
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), Rate * 4L, Rate * 8L, null),
                });

            Assert.IsFalse(result.Verified, Describe(result));
            Assert.IsNull(result.CleanedPcm);
            Assert.IsTrue(
                result.Attempts.Any(a =>
                    a.ReferenceKind == "captured-playnite-residual" && !a.Verified),
                Describe(result));
            CollectionAssert.AreEqual(Bytes(game), input);
        }

        [TestMethod]
        public void SeveralIndependentWavesAreAllRemovedInOneTransaction()
        {
            var frames = Rate * 7;
            var game = Noise(frames, 902, 700);
            var endpoint = (short[])game.Clone();
            var sources = new List<ChimeRemovalSource>();
            var placements = new List<Tuple<short[], int>>();
            var starts = new[] { Rate / 2, Rate * 2, Rate * 7 / 2, Rate * 5 };
            var skews = new[] { 211, 1200, 37, 2300 };
            for (var i = 0; i < starts.Length; i++)
            {
                var sound = Chime(Rate * 3 / 4, 390 + i * 97, 5200 - i * 500, 40 + i);
                Add(endpoint, sound, starts[i] + skews[i]);
                placements.Add(Tuple.Create(sound, starts[i] + skews[i]));
                sources.Add(new ChimeRemovalSource(
                    Guid.NewGuid(), starts[i] * 4L, (starts[i] + sound.Length) * 4L, Bytes(sound)));
            }

            var result = ChimeRemovalEngine.RemoveAll(Bytes(endpoint), null, null, sources);

            Assert.IsTrue(result.Verified, result.FailureReason);
            foreach (var placement in placements)
            {
                AssertChimeSuppressed(
                    result.CleanedPcm, game, placement.Item1, placement.Item2, -18, Describe(result));
            }
            AssertGamePreserved(result.CleanedPcm, game, -32);
        }

        [TestMethod]
        public void QuietGameBedDoesNotVetoAProvenRemovalThroughResidualCorrelation()
        {
            // Field run 2026-09-05: fits with correlation 0.999-1.000 and 31-41 dB suppression
            // were rejected on residual correlation 0.199 and 0.524. That score is normalized over
            // one window, so a remnant far below the 30 dB gate still correlates strongly with the
            // reference whenever the game is quiet there. Suppression is the audibility gate.
            var frames = Rate * 4;
            var game = Noise(frames, 1011, 12);
            var sound = Chime(Rate * 2, 523, 4800, 71);
            var live = Drift(sound, 1.0, 0.94);
            var launch = Rate;
            var rendered = launch + 4000;
            var endpoint = (short[])game.Clone();
            Add(endpoint, live, rendered);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });

            var first = result.Attempts.First(a => a.ReferenceKind == "resolved-file");
            Assert.IsTrue(first.Diagnostics.SuppressionDb >= 30, Describe(result));
            Assert.IsTrue(
                first.Diagnostics.ResidualCorrelation > 0.15,
                "the scenario must reproduce a high normalized residual; " + Describe(result));
            // The whole-window fit is rejected for the honest reason, its weakest standard block
            // still holding the drift remnant, and the time-local pass verifies instead.
            Assert.IsFalse(first.Verified, Describe(result));
            Assert.IsTrue(first.Diagnostics.WeakestBlockSuppressionDb < 30, Describe(result));
            Assert.IsTrue(
                result.Attempts.Any(a => a.ReferenceKind == "resolved-file-local" && a.Verified),
                Describe(result));
            Assert.IsTrue(result.Verified, Describe(result));
            AssertChimeSuppressed(result.CleanedPcm, game, live, rendered, -25, Describe(result));
        }

        [TestMethod]
        public void MidSoundCaptureTearIsRelockedBlockByBlock()
        {
            // Field 2026-09-05: the onset of each live sound was removed 38-59 dB, but from
            // 1.2-2.7 s in the rest survived at up to full level at a lag 8-24 frames off the
            // onset's. A whole-window fit reports 40+ dB regardless because one least-squares gain
            // zeroes its own projection; the honest weakest-block figure rejects it and the
            // time-local pass re-locks every block after the tear.
            var frames = Rate * 4;
            var game = Noise(frames, 1013, 25);
            var sound = Chime(Rate * 5 / 2, 494, 4800, 79);
            var live = Tear(sound, Rate * 3 / 2, 18);
            var launch = Rate;
            var rendered = launch + 3900;
            var endpoint = (short[])game.Clone();
            Add(endpoint, live, rendered);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });

            var first = result.Attempts.First(a => a.ReferenceKind == "resolved-file");
            Assert.IsFalse(first.Verified, Describe(result));
            var local = result.Attempts.First(a => a.ReferenceKind == "resolved-file-local");
            Assert.IsTrue(local.Verified, Describe(result));
            Assert.IsTrue(local.Diagnostics.RelockedBlocks > 0, Describe(result));
            Assert.IsTrue(
                local.Diagnostics.MaxBlockLagShiftMs > 0.3 && local.Diagnostics.MaxBlockLagShiftMs < 0.45,
                Describe(result));
            Assert.IsTrue(result.Verified, Describe(result));
            AssertChimeSuppressed(result.CleanedPcm, game, live, rendered, -30, Describe(result));
        }

        [TestMethod]
        public void OneOccurrenceRemovesDirectAndDelayedCopiesAtDifferentLags()
        {
            var frames = Rate * 5;
            var game = Noise(frames, 911, 500);
            var sound = Chime(Rate * 3 / 4, 641, 4800, 161);
            var launch = Rate * 3 / 2;
            var direct = launch - 20000;
            var echo = launch + 24000;
            var endpoint = (short[])game.Clone();
            Add(endpoint, sound, direct);
            AddScaled(endpoint, sound, echo, 0.42);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });

            Assert.IsTrue(result.Verified, Describe(result));
            Assert.IsTrue(
                result.Attempts.Any(a =>
                    a.ReferenceKind.StartsWith("resolved-file-copy-2") && a.Verified),
                Describe(result));
            AssertChimeSuppressed(result.CleanedPcm, game, sound, direct, -18, Describe(result));
            AssertChimeSuppressed(
                result.CleanedPcm, game, Scale(sound, 0.42), echo, -18, Describe(result));
            AssertGamePreserved(result.CleanedPcm, game, -30, Describe(result));
        }

        [TestMethod]
        public void UnresolvedOccurrenceCanUseGamePurgedCapturedReference()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 903, 1200);
            var chime = Chime(Rate, 613, 6000, 72);
            var launch = Rate;
            var endpoint = (short[])game.Clone();
            Add(endpoint, chime, launch);
            var playniteTree = (short[])game.Clone();
            Add(playniteTree, chime, launch);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint),
                Bytes(playniteTree),
                Bytes(game),
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L, (launch + chime.Length) * 4L, null),
                });

            Assert.IsTrue(result.Verified, result.FailureReason);
            AssertChimeSuppressed(result.CleanedPcm, game, chime, launch, -18, Describe(result));
            AssertGameProjectionPreserved(result.CleanedPcm, game, 0.01);
            AssertOutsideIntervalUnchanged(
                result.CleanedPcm,
                Bytes(endpoint),
                launch - Rate / 100,
                launch + chime.Length / 2 + Rate / 100);
        }

        [TestMethod]
        public void MissingReferenceFailsWithoutReturningPartiallyModifiedAudio()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 904, 500);
            var first = Chime(Rate / 2, 440, 5000, 91);
            var second = Chime(Rate / 2, 880, 5000, 92);
            var endpoint = (short[])game.Clone();
            Add(endpoint, first, Rate / 2);
            Add(endpoint, second, Rate * 3 / 2);
            var input = Bytes(endpoint);
            var before = (byte[])input.Clone();

            var result = ChimeRemovalEngine.RemoveAll(
                input,
                null,
                null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), Rate * 2L, Rate * 4L, Bytes(first)),
                    new ChimeRemovalSource(
                        Guid.NewGuid(), Rate * 6L, Rate * 8L, null),
                });

            Assert.IsFalse(result.Verified);
            Assert.IsNull(result.CleanedPcm);
            CollectionAssert.AreEqual(before, input);
        }

        [TestMethod]
        public void FileThatOnlyLooksAbsentCannotAuthorizeAReplacementWithoutCaptureProof()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 905, 600);
            var live = Chime(Rate, 731, 6000, 101);
            var wrongFile = Chime(Rate, 419, 6000, 102);
            var endpoint = (short[])game.Clone();
            Add(endpoint, live, Rate);
            var input = Bytes(endpoint);
            var before = (byte[])input.Clone();

            var result = ChimeRemovalEngine.RemoveAll(
                input,
                null,
                null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(wrongFile)),
                });

            Assert.IsFalse(result.Verified, "a mismatched decoded file must fail closed");
            Assert.IsNull(result.CleanedPcm);
            CollectionAssert.AreEqual(before, input);
        }

        [TestMethod]
        public void IdenticalSimultaneousOccurrencesCanBeRemovedByOneVerifiedFit()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 906, 500);
            var sound = Chime(Rate, 523, 4300, 111);
            var endpoint = (short[])game.Clone();
            Add(endpoint, sound, Rate);
            Add(endpoint, sound, Rate);
            var source = new ChimeRemovalSource(
                Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(sound));
            var duplicate = new ChimeRemovalSource(
                Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(sound));

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null, new[] { source, duplicate });

            Assert.IsTrue(result.Verified, result.FailureReason);
            Assert.IsTrue(result.Changed);
            AssertChimeSuppressed(result.CleanedPcm, game, sound, Rate, -15, Describe(result));
            AssertGamePreserved(result.CleanedPcm, game, -30);
        }

        [TestMethod]
        public void FilePlaybackSearchCoversLargeEndpointTimelineOffset()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 907, 550);
            var sound = Chime(Rate * 3 / 4, 467, 5700, 121);
            var launch = Rate / 3;
            var rendered = launch + 32000; // 667 ms: old endpoint anchoring could move by 200+ ms.
            var endpoint = (short[])game.Clone();
            Add(endpoint, sound, rendered);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });

            Assert.IsTrue(result.Verified, Describe(result));
            AssertChimeSuppressed(result.CleanedPcm, game, sound, rendered, -18, Describe(result));
            AssertGamePreserved(result.CleanedPcm, game, -30);
        }

        [TestMethod]
        public void LaterSoundTruncatesAndReplacesAnEarlierSoundCleanly()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 908, 450);
            var first = Chime(Rate, 431, 4800, 131);
            var second = Chime(Rate, 719, 4600, 132);
            var firstStart = Rate;
            var secondStart = Rate + Rate / 3;
            var firstAudible = first.Take((secondStart - firstStart) * 2).ToArray();
            var endpoint = (short[])game.Clone();
            Add(endpoint, firstAudible, firstStart);
            Add(endpoint, second, secondStart);

            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), firstStart * 4L,
                        secondStart * 4L, Bytes(first)),
                    new ChimeRemovalSource(
                        Guid.NewGuid(), secondStart * 4L,
                        (secondStart + second.Length / 2) * 4L, Bytes(second)),
                });

            Assert.IsTrue(result.Verified, Describe(result));
            AssertChimeSuppressed(
                result.CleanedPcm, game, firstAudible, firstStart, -15, Describe(result));
            AssertChimeSuppressed(result.CleanedPcm, game, second, secondStart, -15, Describe(result));
            AssertGamePreserved(result.CleanedPcm, game, -28, Describe(result));
        }

        private static short[] Noise(int frames, int seed, int amplitude)
        {
            var random = new Random(seed);
            var samples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var value = (short)(random.NextDouble() * amplitude * 2 - amplitude);
                samples[frame * 2] = value;
                samples[frame * 2 + 1] = (short)(value * 0.91);
            }

            return samples;
        }

        private static short[] Chime(int frames, double hz, int amplitude, int seed)
        {
            var random = new Random(seed);
            var samples = new short[frames * 2];
            for (var frame = 0; frame < frames; frame++)
            {
                var envelope = Math.Exp(-3.0 * frame / Math.Max(1, frames));
                var wave = Math.Sin(2 * Math.PI * hz * frame / Rate) +
                    0.37 * Math.Sin(2 * Math.PI * (hz * 1.713) * frame / Rate) +
                    0.04 * (random.NextDouble() * 2 - 1);
                var value = (short)Math.Max(
                    short.MinValue,
                    Math.Min(short.MaxValue, amplitude * envelope * wave));
                samples[frame * 2] = value;
                samples[frame * 2 + 1] = (short)(value * 0.94);
            }

            return samples;
        }

        private static void Add(short[] destination, short[] source, int startFrame)
        {
            for (var frame = 0; frame < source.Length / 2; frame++)
            {
                var target = startFrame + frame;
                if (target < 0 || target >= destination.Length / 2)
                {
                    continue;
                }

                for (var channel = 0; channel < 2; channel++)
                {
                    var index = target * 2 + channel;
                    var mixed = destination[index] + source[frame * 2 + channel];
                    destination[index] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mixed));
                }
            }
        }

        private static void AddScaled(
            short[] destination,
            short[] source,
            int startFrame,
            double gain)
        {
            Add(destination, Scale(source, gain), startFrame);
        }

        private static short[] Scale(short[] source, double gain)
        {
            var scaled = new short[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                scaled[i] = (short)Math.Max(
                    short.MinValue,
                    Math.Min(short.MaxValue, Math.Round(source[i] * gain)));
            }

            return scaled;
        }

        private static short[] Tear(short[] source, int tearFrame, int shiftFrames)
        {
            var frames = source.Length / 2;
            var torn = new short[source.Length];
            for (var frame = 0; frame < frames; frame++)
            {
                var from = frame < tearFrame ? frame : frame - shiftFrames;
                for (var channel = 0; channel < 2; channel++)
                {
                    torn[frame * 2 + channel] = from >= 0 && from < frames
                        ? source[from * 2 + channel]
                        : (short)0;
                }
            }

            return torn;
        }

        private static short[] Drift(short[] source, double startGain, double endGain)
        {
            var frames = source.Length / 2;
            var drifted = new short[source.Length];
            for (var frame = 0; frame < frames; frame++)
            {
                var gain = startGain + (endGain - startGain) * frame / Math.Max(1, frames - 1);
                for (var channel = 0; channel < 2; channel++)
                {
                    var index = frame * 2 + channel;
                    drifted[index] = (short)Math.Max(
                        short.MinValue,
                        Math.Min(short.MaxValue, Math.Round(source[index] * gain)));
                }
            }

            return drifted;
        }

        private static byte[] Bytes(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static short[] Samples(byte[] bytes)
        {
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }

        private static void AssertChimeSuppressed(
            byte[] cleaned,
            short[] game,
            short[] chime,
            int startFrame,
            double maximumErrorDb,
            string details = null)
        {
            var result = Samples(cleaned);
            double error = 0;
            double source = 0;
            for (var frame = 0; frame < chime.Length / 2; frame++)
            {
                var target = startFrame + frame;
                for (var channel = 0; channel < 2; channel++)
                {
                    var residual = result[target * 2 + channel] - game[target * 2 + channel];
                    error += residual * (double)residual;
                    var expected = chime[frame * 2 + channel];
                    source += expected * (double)expected;
                }
            }

            var db = 10 * Math.Log10(Math.Max(1, error) / Math.Max(1, source));
            Assert.IsTrue(db <= maximumErrorDb, $"chime residual {db:0.0} dB; {details}");
        }

        private static string Describe(ChimeRemovalResult result)
        {
            return string.Join(" | ", System.Linq.Enumerable.Select(
                result.Attempts,
                a => $"{a.ReferenceKind}:{a.Outcome} lag={a.Diagnostics.StartLagMs:0.###} " +
                    $"gain={a.Diagnostics.Gain:0.###} corr={a.Diagnostics.Correlation:0.###} " +
                    $"supp={a.Diagnostics.SuppressionDb:0.0} weakest={a.Diagnostics.WeakestBlockSuppressionDb:0.0}@{a.Diagnostics.WeakestBlockStartMs:0}ms " +
                    $"residual={a.Diagnostics.ResidualCorrelation:0.###} " +
                    $"blocks={a.Diagnostics.SubtractedBlocks}/{a.Diagnostics.TotalBlocks} " +
                    $"restored={a.Diagnostics.RestoredBlocks} relocked={a.Diagnostics.RelockedBlocks}" +
                    (a.Diagnostics.RelockedBlocks > 0 ? $" shift={a.Diagnostics.MaxBlockLagShiftMs:0.##}ms" : "")));
        }

        private static void AssertGamePreserved(
            byte[] cleaned,
            short[] game,
            double maximumErrorDb,
            string details = null)
        {
            var result = Samples(cleaned);
            double error = 0;
            double source = 0;
            for (var i = 0; i < result.Length; i++)
            {
                var residual = result[i] - game[i];
                error += residual * (double)residual;
                source += game[i] * (double)game[i];
            }

            var db = 10 * Math.Log10(Math.Max(1, error) / Math.Max(1, source));
            Assert.IsTrue(db <= maximumErrorDb, $"game error {db:0.0} dB; {details}");
        }

        private static void AssertGameProjectionPreserved(
            byte[] cleaned,
            short[] game,
            double maximumGainError)
        {
            var result = Samples(cleaned);
            double dot = 0;
            double energy = 0;
            for (var i = 0; i < result.Length; i++)
            {
                dot += result[i] * (double)game[i];
                energy += game[i] * (double)game[i];
            }

            var gain = energy <= 0 ? 0 : dot / energy;
            Assert.IsTrue(
                Math.Abs(gain - 1) <= maximumGainError,
                $"game projection gain {gain:0.000}");
        }

        private static void AssertOutsideIntervalUnchanged(
            byte[] cleaned,
            byte[] original,
            int startFrame,
            int endFrame)
        {
            var start = Math.Max(0, startFrame * PcmAudio.BlockAlign);
            var end = Math.Min(cleaned.Length, endFrame * PcmAudio.BlockAlign);
            for (var i = 0; i < cleaned.Length; i++)
            {
                if (i >= start && i < end)
                {
                    continue;
                }

                Assert.AreEqual(original[i], cleaned[i], $"byte {i} outside the chime interval changed");
            }
        }
    }
}
