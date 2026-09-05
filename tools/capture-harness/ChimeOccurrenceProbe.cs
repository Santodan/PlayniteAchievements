using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Deterministic end-to-end probe for the production occurrence registry and transactional
    /// removal engine. No sound device, driver, routing change, or plugin process is required.
    /// </summary>
    internal static class ChimeOccurrenceProbe
    {
        private const int Rate = PcmAudio.SampleRate;
        private static int _failures;

        public static int Main()
        {
            Console.WriteLine("Production occurrence/removal probe");
            Console.WriteLine("===================================");
            ProbeOccurrencePlanning();
            ProbeResolvedFileAndOneComposite();
            ProbeLargeTimelineOffset();
            ProbeMultipleCopiesOfOneSound();
            ProbeQuietGameDriftingRender();
            ProbeMidSoundCaptureTear();
            ProbePartiallyIsolatedLeftover();
            ProbeTruncatedPlayback();
            ProbeSeveralSounds();
            ProbeCapturedFallback();
            ProbeSilentGameReferenceFailsClosed();
            ProbeMismatchedFileFailsClosed();
            ProbeSimultaneousDuplicateSounds();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL PASS" : $"FAILED: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void ProbeOccurrencePlanning()
        {
            var registry = new WaveSoundOccurrenceRegistry();
            var session = Guid.NewGuid();
            var t0 = new DateTime(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc);
            var owners = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var first = registry.Register(
                session, Guid.NewGuid(), t0, 4, 0.3, owners, "one.wav", 0.4, 100, 0.75);
            var second = registry.Register(
                session, Guid.NewGuid(), t0.AddSeconds(1), 4, 0.3,
                new[] { Guid.NewGuid() }, "two.wav", 0.4, 100, 0.75);
            var clusters = registry.GetOverlappingClusters(
                session, t0.AddSeconds(-1), t0.AddSeconds(8));

            Check(owners.All(id => ReferenceEquals(first, registry.FindOwned(session, id))),
                "one wave owns all of its unlocks");
            Check(first.EndUtc == second.LaunchUtc,
                "a later UPS launch truncates the previous player");
            Check(clusters.Count == 1 && clusters[0].Occurrences.Count == 2,
                "overlapping removal windows form one transaction");
            Check(clusters.Count == 1 &&
                    clusters[0].StartUtc == t0.AddSeconds(-1.05) &&
                    clusters[0].EndUtc == t0.AddSeconds(5.75),
                "bounded reads include the full lag-search radius");
            Check(first.OccurrenceId != second.OccurrenceId,
                "waves are identified independently of time and file");
        }

        private static void ProbeResolvedFileAndOneComposite()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 1001, 800);
            var sound = Chime(Rate, 447, 6500, 11);
            var endpoint = (short[])game.Clone();
            var launch = Rate / 2;
            var rendered = launch + 1900;
            Add(endpoint, sound, rendered);
            var timer = Stopwatch.StartNew();
            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L, (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });
            timer.Stop();

            var removalDb = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, rendered)
                : double.PositiveInfinity;
            Check(result.Verified && result.Changed && removalDb <= -20,
                "resolved file removes a skewed live render without a sidecar",
                $"verified={result.Verified} residual={removalDb:0.0}dB {Describe(result)}");

            if (result.Verified)
            {
                var final = (byte[])result.CleanedPcm.Clone();
                var oneComposite = new byte[final.Length];
                var target = Rate * 5 / 2;
                PcmAudio.MixInto(oneComposite, target * 4L, Bytes(sound), 0, sound.Length * 2L);
                PcmAudio.MixInto(final, 0, oneComposite, 0, oneComposite.Length);
                var gain = ProjectionGain(final, game, sound, target);
                Check(Math.Abs(gain - 1) <= 0.03,
                    "exactly one replacement is composited at the chosen time",
                    $"fitted replacement gain={gain:0.000}");
            }

            Console.WriteLine($"  INFO one-wave engine time {timer.ElapsedMilliseconds}ms");
        }

        private static void ProbeSeveralSounds()
        {
            var frames = Rate * 7;
            var game = Noise(frames, 1002, 650);
            var endpoint = (short[])game.Clone();
            var sources = new List<ChimeRemovalSource>();
            var placements = new List<Tuple<short[], int>>();
            var starts = new[] { Rate / 2, Rate * 2, Rate * 7 / 2, Rate * 5 };
            var skews = new[] { 77, 700, 1700, 2600 };
            for (var i = 0; i < starts.Length; i++)
            {
                var sound = Chime(Rate * 3 / 4, 370 + i * 113, 5600 - i * 450, 20 + i);
                var rendered = starts[i] + skews[i];
                Add(endpoint, sound, rendered);
                placements.Add(Tuple.Create(sound, rendered));
                sources.Add(new ChimeRemovalSource(
                    Guid.NewGuid(), starts[i] * 4L,
                    (starts[i] + sound.Length / 2) * 4L, Bytes(sound)));
            }

            var timer = Stopwatch.StartNew();
            var result = ChimeRemovalEngine.RemoveAll(Bytes(endpoint), null, null, sources);
            timer.Stop();
            var worstDb = result.Verified
                ? placements.Max(p => ErrorDb(result.CleanedPcm, game, p.Item1, p.Item2))
                : double.PositiveInfinity;
            Check(result.Verified && result.Changed && worstDb <= -18,
                "all four differently timed sounds are removed in one transaction",
                $"worst residual={worstDb:0.0}dB {Describe(result)}");
            Check(!result.Verified || Math.Abs(GameProjectionGain(result.CleanedPcm, game) - 1) <= 0.01,
                "multi-sound cleanup preserves the game projection");
            Check(timer.Elapsed < TimeSpan.FromSeconds(20),
                "four-wave bounded cleanup stays off the export critical path budget",
                $"elapsed={timer.Elapsed.TotalSeconds:0.00}s");
            Console.WriteLine($"  INFO four-wave engine time {timer.ElapsedMilliseconds}ms");
        }

        private static void ProbeLargeTimelineOffset()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 1006, 550);
            var sound = Chime(Rate * 3 / 4, 467, 5700, 61);
            var launch = Rate / 3;
            var rendered = launch + 32000;
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
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, rendered)
                : double.PositiveInfinity;
            Check(result.Verified && residual <= -30,
                "file removal covers a 667 ms endpoint-timeline displacement",
                $"residual={residual:0.0}dB {Describe(result)}");
        }

        private static void ProbeTruncatedPlayback()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 1007, 450);
            var first = Chime(Rate, 431, 4800, 71);
            var second = Chime(Rate, 719, 4600, 72);
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
                        Guid.NewGuid(), firstStart * 4L, secondStart * 4L, Bytes(first)),
                    new ChimeRemovalSource(
                        Guid.NewGuid(), secondStart * 4L,
                        (secondStart + second.Length / 2) * 4L, Bytes(second)),
                });
            var firstResidual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, firstAudible, firstStart)
                : double.PositiveInfinity;
            var secondResidual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, second, secondStart)
                : double.PositiveInfinity;
            Check(result.Verified && firstResidual <= -30 && secondResidual <= -30,
                "a new UPS launch cleanly replaces a truncated prior sound",
                $"residuals={firstResidual:0.0}/{secondResidual:0.0}dB {Describe(result)}");
        }

        private static void ProbeMultipleCopiesOfOneSound()
        {
            var frames = Rate * 5;
            var game = Noise(frames, 1008, 500);
            var sound = Chime(Rate * 3 / 4, 641, 4800, 81);
            var launch = Rate * 3 / 2;
            var first = launch - 20000;
            var second = launch + 24000;
            var endpoint = (short[])game.Clone();
            Add(endpoint, sound, first);
            var quieter = Scale(sound, 0.42);
            Add(endpoint, quieter, second);
            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });
            var firstResidual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, first)
                : double.PositiveInfinity;
            var secondResidual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, quieter, second)
                : double.PositiveInfinity;
            Check(result.Verified && firstResidual <= -18 && secondResidual <= -18,
                "one UPS occurrence removes two independently delayed render copies",
                $"residuals={firstResidual:0.0}/{secondResidual:0.0}dB {Describe(result)}");
        }

        private static void ProbeQuietGameDriftingRender()
        {
            // Field run 2026-09-05 (six-wave test burst): every resolved-file fit reported
            // correlation 0.999-1.000 and 31-50 dB suppression, yet two waves were rejected on
            // residual correlation alone (0.199 and 0.524). Normalized correlation of a tiny
            // leftover is high whenever the game bed is quiet, so it cannot veto a removal that
            // suppression already proved. The live copy here is a sustained sound whose level
            // drifts 6% across playback; one global gain cannot follow that, so a reference-shaped
            // remnant survives in the loudest window at a level far below the game-safe gate.
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
            var first = result.Attempts.FirstOrDefault(a => a.ReferenceKind == "resolved-file");
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, live, rendered)
                : double.PositiveInfinity;
            // The whole-window fit is rejected for the honest reason: its weakest standard block
            // holds the drift remnant. The time-local pass then fits each block and verifies.
            Check(result.Verified && first != null &&
                    first.Diagnostics.SuppressionDb >= 30 &&
                    first.Diagnostics.ResidualCorrelation > 0.15 &&
                    first.Diagnostics.WeakestBlockSuppressionDb < 30 &&
                    result.Attempts.Any(a => a.ReferenceKind == "resolved-file-local" && a.Verified) &&
                    residual <= -25,
                "a quiet game bed cannot veto a proven removal through normalized residual correlation",
                $"residual={residual:0.0}dB firstResidualCorr=" +
                $"{(first == null ? double.NaN : first.Diagnostics.ResidualCorrelation):0.000} {Describe(result)}");
        }

        private static void ProbeMidSoundCaptureTear()
        {
            // Field 2026-09-05 clips: the sound's onset was removed 38-59 dB but everything from
            // 1.2-2.7 s in survived at up to full level, at a lag 8-24 frames off the onset's, in
            // either direction. A loopback alignment tear inside the sound moved its tail. One lag
            // over the whole window cannot fit that; the time-local pass re-locks each block.
            var frames = Rate * 4;
            var game = Noise(frames, 1013, 25);
            var sound = Jingle(Rate * 5 / 2, 494, 6000, 79);
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
            var first = result.Attempts.FirstOrDefault(a => a.ReferenceKind == "resolved-file");
            var local = result.Attempts.FirstOrDefault(a => a.ReferenceKind == "resolved-file-local");
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, live, rendered)
                : double.PositiveInfinity;
            Check(result.Verified && first != null && !first.Verified &&
                    local != null && local.Verified && local.Diagnostics.RelockedBlocks > 0 &&
                    residual <= -30,
                "a mid-sound capture tear is re-locked block by block instead of leaving the tail",
                $"residual={residual:0.0}dB {Describe(result)}");
        }

        private static void ProbePartiallyIsolatedLeftover()
        {
            // Game Only isolation subtracts the Playnite-tree reference in 0.5 s blocks of the clip
            // window and restores blocks it cannot prove. When the dedicated pass on the pristine
            // mixture failed, the engine runs again on that same window, so its blocks share the
            // grid: some hold the whole live sound, others only a remnant, and the fit has to be
            // time-local. Here the block that holds the onset was subtracted to 3% and everything
            // after the next block boundary was restored.
            var frames = Rate * 4;
            var game = Noise(frames, 1012, 40);
            var sound = Jingle(Rate * 2, 587, 6000, 73);
            var launch = Rate;
            var rendered = launch + 3800;
            var boundary = ReferenceCancellationPolicy.ChimeBlockFrames * 3;
            var isolated = (short[])game.Clone();
            Add(isolated, sound, rendered);
            Add(isolated, Scale(Head(sound, (boundary - rendered) * 2), -0.97), rendered);
            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(isolated), null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), launch * 4L,
                        (launch + sound.Length / 2) * 4L, Bytes(sound)),
                });
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, rendered)
                : double.PositiveInfinity;
            Check(result.Verified && residual <= -25,
                "the dedicated engine clears a block-varying remnant left by Game Only isolation",
                $"residual={residual:0.0}dB {Describe(result)}");
        }

        private static void ProbeCapturedFallback()
        {
            var frames = Rate * 4;
            var game = Noise(frames, 1003, 1000);
            var sound = Chime(Rate, 619, 6000, 31);
            var start = Rate;
            var endpoint = (short[])game.Clone();
            var playnite = (short[])game.Clone();
            Add(endpoint, sound, start);
            Add(playnite, sound, start);
            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), Bytes(playnite), Bytes(game),
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), start * 4L, (start + sound.Length / 2) * 4L, null),
                });
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, start)
                : double.PositiveInfinity;
            Check(result.Verified && residual <= -18,
                "an unresolved occurrence uses the game-purged captured UPS render",
                $"residual={residual:0.0}dB {Describe(result)}");
        }

        private static void ProbeMismatchedFileFailsClosed()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 1004, 500);
            var live = Chime(Rate, 733, 6000, 41);
            var wrong = Chime(Rate, 401, 6000, 42);
            var endpoint = (short[])game.Clone();
            Add(endpoint, live, Rate);
            var input = Bytes(endpoint);
            var before = (byte[])input.Clone();
            var result = ChimeRemovalEngine.RemoveAll(
                input, null, null,
                new[]
                {
                    new ChimeRemovalSource(
                        Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(wrong)),
                });
            Check(!result.Verified && result.CleanedPcm == null && input.SequenceEqual(before),
                "a transformed/wrong file fails closed and cannot authorize a second chime",
                result.FailureReason);
        }

        private static void ProbeSilentGameReferenceFailsClosed()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 1009, 900);
            var sound = Chime(Rate, 709, 5200, 91);
            var endpoint = (short[])game.Clone();
            var playnite = (short[])game.Clone();
            Add(endpoint, sound, Rate);
            Add(playnite, sound, Rate);
            var input = Bytes(endpoint);
            var before = (byte[])input.Clone();
            var result = ChimeRemovalEngine.RemoveAll(
                input, Bytes(playnite), new byte[input.Length],
                new[]
                {
                    new ChimeRemovalSource(Guid.NewGuid(), Rate * 4L, Rate * 8L, null),
                });
            Check(!result.Verified && input.SequenceEqual(before),
                "a silent game-tree capture cannot authorize game-bearing chime subtraction",
                result.FailureReason);
        }

        private static void ProbeSimultaneousDuplicateSounds()
        {
            var frames = Rate * 3;
            var game = Noise(frames, 1005, 450);
            var sound = Chime(Rate, 557, 4000, 51);
            var endpoint = (short[])game.Clone();
            Add(endpoint, sound, Rate);
            Add(endpoint, sound, Rate);
            var result = ChimeRemovalEngine.RemoveAll(
                Bytes(endpoint), null, null,
                new[]
                {
                    new ChimeRemovalSource(Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(sound)),
                    new ChimeRemovalSource(Guid.NewGuid(), Rate * 4L, Rate * 8L, Bytes(sound)),
                });
            var residual = result.Verified
                ? ErrorDb(result.CleanedPcm, game, sound, Rate)
                : double.PositiveInfinity;
            Check(result.Verified && residual <= -15,
                "two identical simultaneous occurrences are not conflated or left doubled",
                $"residual={residual:0.0}dB {Describe(result)}");
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
                    0.37 * Math.Sin(2 * Math.PI * hz * 1.713 * frame / Rate) +
                    0.04 * (random.NextDouble() * 2 - 1);
                var value = (short)Math.Max(short.MinValue,
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

        /// <summary>
        /// A struck five-note arpeggio: each note has inharmonic partials with per-partial decay,
        /// a 4 ms attack transient, and a decaying broadband component. <see cref="Chime"/> is one
        /// decaying two-partial tone, and any single decaying tone is near-periodic: a remnant that
        /// lacks the sound's head can lock a lag search onto a repeat of its own partials. Distinct
        /// pitches struck across the whole duration give every part of the sound unique events, so
        /// a lag miss on this generator is the search's, not the signal's.
        /// </summary>
        private static short[] Jingle(int frames, double baseHz, int amplitude, int seed)
        {
            double[] notes = { 1.0, 1.25, 1.5, 1.875, 2.25 };
            double[] ratios = { 1.0, 1279.0 / 880.0, 1834.0 / 880.0, 2503.0 / 880.0, 3391.0 / 880.0 };
            double[] decays = { 3.0, 4.1, 5.7, 7.3, 9.1 };
            var random = new Random(seed);
            var samples = new short[frames * 2];
            var spacing = frames / notes.Length;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0.0;
                for (var n = 0; n < notes.Length; n++)
                {
                    var onset = n * spacing;
                    if (frame < onset)
                    {
                        continue;
                    }
                    var t = (frame - onset) / (double)Rate;
                    for (var p = 0; p < ratios.Length; p++)
                    {
                        sum += Math.Exp(-decays[p] * t) *
                            Math.Sin(2 * Math.PI * baseHz * notes[n] * ratios[p] * t) / (p + 1);
                    }
                    if (t < 0.004)
                    {
                        sum += (random.NextDouble() - 0.5) * 2.0 * Math.Exp(-600.0 * t);
                    }
                    sum += 0.12 * Math.Exp(-2.5 * t) * (random.NextDouble() - 0.5) * 2.0;
                }
                var value = (short)Math.Max(short.MinValue,
                    Math.Min(short.MaxValue, Math.Round(amplitude * sum / 3.0)));
                samples[frame * 2] = value;
                samples[frame * 2 + 1] = (short)(value * 0.94);
            }
            return samples;
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

        /// <summary>
        /// The live copy after a capture alignment tear: identical up to <paramref name="tearFrame"/>,
        /// then every later frame arrives <paramref name="shiftFrames"/> later (positive) or
        /// earlier (negative), as the 2026-09-05 clips showed for the tails of live sounds.
        /// </summary>
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

        private static short[] Head(short[] source, int samples)
        {
            var head = new short[Math.Min(source.Length, samples)];
            Array.Copy(source, head, head.Length);
            return head;
        }

        private static byte[] Bytes(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static short[] Shorts(byte[] bytes)
        {
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }

        private static double ErrorDb(byte[] cleanedBytes, short[] game, short[] sound, int startFrame)
        {
            var cleaned = Shorts(cleanedBytes);
            double error = 0;
            double source = 0;
            for (var frame = 0; frame < sound.Length / 2; frame++)
            {
                var target = startFrame + frame;
                for (var channel = 0; channel < 2; channel++)
                {
                    var residual = cleaned[target * 2 + channel] - game[target * 2 + channel];
                    error += residual * (double)residual;
                    var expected = sound[frame * 2 + channel];
                    source += expected * (double)expected;
                }
            }
            return 10 * Math.Log10(Math.Max(1, error) / Math.Max(1, source));
        }

        private static double ProjectionGain(byte[] finalBytes, short[] game, short[] sound, int startFrame)
        {
            var final = Shorts(finalBytes);
            double dot = 0;
            double energy = 0;
            for (var frame = 0; frame < sound.Length / 2; frame++)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    var source = sound[frame * 2 + channel];
                    var actual = final[(startFrame + frame) * 2 + channel] -
                        game[(startFrame + frame) * 2 + channel];
                    dot += actual * (double)source;
                    energy += source * (double)source;
                }
            }
            return energy <= 0 ? 0 : dot / energy;
        }

        private static double GameProjectionGain(byte[] cleanedBytes, short[] game)
        {
            var cleaned = Shorts(cleanedBytes);
            double dot = 0;
            double energy = 0;
            for (var i = 0; i < cleaned.Length; i++)
            {
                dot += cleaned[i] * (double)game[i];
                energy += game[i] * (double)game[i];
            }
            return energy <= 0 ? 0 : dot / energy;
        }

        private static string Describe(ChimeRemovalResult result)
        {
            if (result == null)
            {
                return "no result";
            }
            return string.Join(" | ", result.Attempts.Select(a =>
                $"{a.ReferenceKind}:{a.Outcome} lag={a.Diagnostics.StartLagMs:0.0} " +
                $"corr={a.Diagnostics.Correlation:0.000} supp={a.Diagnostics.SuppressionDb:0.0} " +
                $"weakest={a.Diagnostics.WeakestBlockSuppressionDb:0.0}@{a.Diagnostics.WeakestBlockStartMs:0}ms " +
                $"restored={a.Diagnostics.RestoredBlocks} relocked={a.Diagnostics.RelockedBlocks}" +
                (a.Diagnostics.RelockedBlocks > 0 ? $" shift={a.Diagnostics.MaxBlockLagShiftMs:0.##}ms" : "")));
        }

        private static void Check(bool passed, string name, string detail = null)
        {
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")} {name}" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})"));
            if (!passed)
            {
                _failures++;
            }
        }
    }
}
