using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// How a game-reference cancellation pass ended. Anything but Unseparable leaves the mixture
    /// safe to composite into a clip.
    /// </summary>
    internal enum PcmCancellationOutcome
    {
        /// <summary>The game track was subtracted and the residual passed verification.</summary>
        CancelledVerified,

        /// <summary>The reference does not appear in the mixture; nothing was modified.</summary>
        CleanNoGameDetected,

        /// <summary>
        /// The game is (or may be) present but could not be verifiably removed; the mixture is
        /// unmodified and callers must omit the chime rather than composite audible game bleed.
        /// </summary>
        Unseparable,
    }

    /// <summary>Measurements from a cancellation pass, for logging.</summary>
    internal struct PcmCancellationDiagnostics
    {
        /// <summary>Global alignment lag at the loudest reference passage, in milliseconds.</summary>
        public double StartLagMs;

        /// <summary>Lag tracked by the final processed block, in milliseconds.</summary>
        public double EndLagMs;

        /// <summary>Fitted mixture/reference gain at the global alignment.</summary>
        public double Gain;

        /// <summary>Normalized correlation at the global alignment.</summary>
        public double Correlation;

        /// <summary>Upper-quartile per-block energy suppression achieved by the subtraction.</summary>
        public double SuppressionDb;

        /// <summary>Blocks whose game removal could not be verified and were silenced instead.</summary>
        public int MutedBlocks;

        /// <summary>Blocks the subtraction actually ran on, out of the whole slice.</summary>
        public int SubtractedBlocks;

        /// <summary>
        /// Blocks skipped because the reference's own level there was too low to fit against. The
        /// difference between these and the rest of the skipped blocks is the difference between
        /// "nothing was playing" and "a copy too quiet to pass the floor was left in".
        /// </summary>
        public int QuietBlocks;

        /// <summary>Weakest suppression among the blocks that were subtracted.</summary>
        public double WeakestBlockSuppressionDb;

        /// <summary>Blocks in the slice.</summary>
        public int TotalBlocks;

        /// <summary>
        /// Correlation between the finished residual and the reference, at the alignment the pass
        /// used. Near zero means the reference's signal is gone from the mixture — so anything a
        /// listener still hears is not the captured copy, and cancellation cannot be the answer.
        /// </summary>
        public double ResidualCorrelation;
    }

    /// <summary>
    /// Pure 16-bit PCM helpers for the clip export: mixing the recorded chime into a clip's
    /// audio. Kept free of Media Foundation so it unit-tests directly.
    /// </summary>
    internal static class PcmAudio
    {
        /// <summary>Sample rate of the export PCM format.</summary>
        public const int SampleRate = 48000;

        /// <summary>Channel count of the export PCM format.</summary>
        public const int Channels = 2;

        /// <summary>Sample depth of the export PCM format.</summary>
        public const int BitsPerSample = 16;

        /// <summary>Bytes per second of the export PCM format (48 kHz, stereo, 16-bit).</summary>
        public const int BytesPerSecond = SampleRate * Channels * BitsPerSample / 8;

        /// <summary>Sample-frame alignment in bytes (stereo 16-bit).</summary>
        public const int BlockAlign = 4;

        // Two independent application-loopback clients share the endpoint clock but can begin a
        // handful of packets apart. Search a bounded neighbourhood before cancellation so that a
        // sub-packet offset cannot leave the game behind as a comb-filtered echo.
        private const int MaxCancellationLagFrames = 2400; // 50 ms at 48 kHz
        private const int CorrelationStrideFrames = 8;
        private const int CorrelationWindowFrames = 24000; // score 0.5 s at the loudest passage
        private const double SilentReferenceRms = 16.0; // about -66 dBFS

        // ATTEMPT gate, not the accept gate: real recorder chunks can be fractured by pump timing
        // steps inside the global scoring window, which dilutes the single-lag correlation and
        // gain of a genuine leak well below unity. This only needs to screen out "clearly not the
        // same audio" (unrelated content scores ~0.05); the per-block re-lock and the projection
        // verification make the actual accept/reject decision.
        private const double MinimumCancellationCorrelation = 0.30;

        // Plausible amplitude ratio between the mixture's copy of the reference and the reference
        // itself. Both defaults assume the two captures are the same engine mix — true of the chime
        // pair, where a ratio far from 1 means the wrong signal. A caller whose reference is tapped
        // somewhere else in the graph must widen these: an endpoint capture carries that endpoint's
        // own volume and downmix scaling, so a ratio several times off can be legitimate (a virtual
        // endpoint measured 3.35). Verification, not this gate, is what proves a subtraction correct.
        private const double MinimumCancellationGain = 0.30;
        private const double MaximumCancellationGain = 1.5;

        // A loud reference that barely projects onto the mixture is not leaking into it at all
        // (the game runs outside the sidecar's process tree); pass the mixture through untouched.
        private const double CleanGainCeiling = 0.1;
        private const double CleanCorrelationCeiling = 0.2;

        // The two streams drift tens of ppm apart (observed ~33 ppm in the field), so one global
        // lag mis-aligns the tail of a multi-second slice. Re-estimate lag and gain per block and
        // follow the drift with a fractional-delay subtraction.
        private const int BlockFrames = 24000; // 0.5 s
        private const int BlockLagSearchFrames = 96; // +/- 2 ms around the tracked lag

        // Lag stride for the first pass of a wider-than-default search; the winner is then refined
        // at single-frame resolution. 1 ms at 48 kHz.
        private const int CoarseLagStrideFrames = 48;

        // Correlation difference below which two candidate lags count as scoring alike, so the one
        // nearest zero is taken. Only applied to searches wider than the default.
        private const double PeriodicAmbiguityMargin = 0.02;
        private const int CrossfadeFrames = 240; // 5 ms across block parameter steps
        private const double BlockGainFloor = 0.05; // below this the block has no game to remove

        // The recorder pumps can step the inter-track offset by more than the narrow search width
        // (observed 1-2 ms steps every few seconds on real chunks). When the tracked-lag search
        // comes back weak, re-lock that block with a full-width search; the wide result is adopted
        // only when clearly stronger, so a genuinely absent reference cannot fake a lock.
        private const double BlockRelockCorrelation = 0.60;
        private const double BlockRelockMargin = 0.10;

        // Subtraction must demonstrably remove the game, not merely correlate with it: require
        // this much energy suppression in the blocks where subtraction ran. Upper-quartile, so a
        // chime-dominated block (whose residual is legitimately loud) cannot fail a good pass.
        private const double MinimumSuppressionDb = 10.0;
        private const double FullSuppressionDb = 60.0;

        /// <summary>
        /// Writes a buffer of this format's PCM as a RIFF/WAVE file, so a processed audio window can
        /// be handed back to the export pipeline as an ordinary chunk. Keeping the exporter on files
        /// is what leaves its planning and A/V alignment untouched.
        /// </summary>
        public static void WriteWav(string path, byte[] pcm)
        {
            if (string.IsNullOrEmpty(path) || pcm == null)
            {
                throw new ArgumentNullException(pcm == null ? nameof(pcm) : nameof(path));
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Tag("RIFF"));
                writer.Write(36 + pcm.Length);
                writer.Write(Tag("WAVE"));

                writer.Write(Tag("fmt "));
                writer.Write(16);                                   // PCM fmt chunk size
                writer.Write((short)1);                             // WAVE_FORMAT_PCM
                writer.Write((short)Channels);
                writer.Write(SampleRate);
                writer.Write(BytesPerSecond);
                writer.Write((short)BlockAlign);
                writer.Write((short)BitsPerSample);

                writer.Write(Tag("data"));
                writer.Write(pcm.Length);
                writer.Write(pcm);
            }
        }

        /// <summary>A RIFF four-character chunk id. Written as bytes, never through an encoding.</summary>
        private static byte[] Tag(string fourCc)
        {
            var bytes = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                bytes[i] = (byte)fourCc[i];
            }

            return bytes;
        }

        /// <summary>Converts a 100-ns tick offset to a block-aligned byte offset.</summary>
        public static long TicksToAlignedBytes(long ticks)
        {
            var bytes = (long)(ticks / 10_000_000.0 * BytesPerSecond);
            return bytes & ~(long)(BlockAlign - 1);
        }

        /// <summary>
        /// Applies a linear fade-out over the final <paramref name="seconds"/> of a 16-bit PCM
        /// buffer in place, so a chime cut mid-ring ends silently instead of clicking.
        /// </summary>
        public static void FadeOutTail(byte[] pcm, double seconds)
        {
            if (pcm == null || pcm.Length < BlockAlign || seconds <= 0)
            {
                return;
            }

            var fadeBytes = Math.Min((long)pcm.Length & ~(long)(BlockAlign - 1), TicksToAlignedBytes((long)(seconds * 10_000_000)));
            if (fadeBytes < BlockAlign)
            {
                return;
            }

            var start = pcm.Length - fadeBytes;
            for (long i = start; i + 1 < pcm.Length; i += 2)
            {
                var scale = 1.0 - ((i - start) / (double)fadeBytes);
                var value = (short)(pcm[i] | (pcm[i + 1] << 8));
                var faded = (short)(value * scale);
                pcm[i] = (byte)(faded & 0xff);
                pcm[i + 1] = (byte)((faded >> 8) & 0xff);
            }
        }

        /// <summary>
        /// Saturating add of 16-bit little-endian source samples into the destination in place.
        /// Offsets and count are in bytes and are clamped to both buffers; odd trailing bytes are
        /// ignored (16-bit samples only move in pairs).
        /// </summary>
        public static void MixInto(byte[] dest, long destOffset, byte[] source, long sourceOffset, long byteCount)
        {
            if (dest == null || source == null || destOffset < 0 || sourceOffset < 0)
            {
                return;
            }

            var count = Math.Min(byteCount, Math.Min(dest.Length - destOffset, source.Length - sourceOffset));
            count &= ~1L;
            if (count <= 0)
            {
                return;
            }

            for (long i = 0; i + 1 < count; i += 2)
            {
                var d = (short)(dest[destOffset + i] | (dest[destOffset + i + 1] << 8));
                var s = (short)(source[sourceOffset + i] | (source[sourceOffset + i + 1] << 8));
                var mixed = d + s;
                if (mixed > short.MaxValue)
                {
                    mixed = short.MaxValue;
                }
                else if (mixed < short.MinValue)
                {
                    mixed = short.MinValue;
                }

                dest[destOffset + i] = (byte)(mixed & 0xff);
                dest[destOffset + i + 1] = (byte)((mixed >> 8) & 0xff);
            }
        }

        /// <summary>
        /// Removes a simultaneously captured game-only reference from a Playnite-tree mixture in
        /// place. A bounded global correlation search aligns the two process-loopback streams,
        /// then each half-second block re-fits lag (with fractional-delay interpolation) and gain
        /// so inter-stream drift cannot leave the slice tail comb-filtered. The subtraction is
        /// committed only when the residual energy in the processed blocks confirms the game was
        /// actually removed; otherwise the mixture is left untouched and callers must omit the
        /// chime rather than mix unidentified audio into the clip.
        /// <para>
        /// <paramref name="muteUnverifiedBlocks"/> decides what happens to a block the subtraction
        /// could not be verified on, inside an otherwise verified pass. Silencing it is right for a
        /// sidecar that will be discarded if it is dirty; it is wrong for a track that IS the clip's
        /// audio, where holes are worse than the residual, so the haptic pass turns it off.
        /// </para>
        /// </summary>
        public static PcmCancellationOutcome CancelCorrelated(
            byte[] mixture,
            byte[] gameReference,
            out PcmCancellationDiagnostics diagnostics,
            bool muteUnverifiedBlocks = true,
            int maxLagFrames = MaxCancellationLagFrames,
            double minimumGain = MinimumCancellationGain,
            double maximumGain = MaximumCancellationGain,
            double blockGainFloor = BlockGainFloor)
        {
            diagnostics = default(PcmCancellationDiagnostics);
            if (mixture == null || gameReference == null ||
                mixture.Length < BlockAlign || gameReference.Length < BlockAlign)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            var mixtureFrames = mixture.Length / BlockAlign;
            var referenceFrames = gameReference.Length / BlockAlign;
            var maxLag = Math.Min(
                Math.Max(MaxCancellationLagFrames, maxLagFrames),
                Math.Max(0, Math.Min(mixtureFrames, referenceFrames) / 4));
            var loudestStart = FindLoudestWindowStart(gameReference, referenceFrames);

            // Score several candidate windows spread across the slice, not just the loudest one:
            // the recorder streams can carry an alignment tear (a pump timing step, observed to
            // coincide with a render stream starting — i.e. the chime itself), and a single window
            // that straddles the tear reads a fractured correlation for a perfectly separable
            // slice. A tear cannot fracture every window.
            var loudestScore = ScanWindow(mixture, gameReference, loudestStart, maxLag);
            var best = loudestScore;
            foreach (var candidateStart in new[] { 0, (int)((long)referenceFrames / 3), (int)(2L * referenceFrames / 3) })
            {
                if (Math.Abs(candidateStart - loudestStart) < CorrelationWindowFrames / 2)
                {
                    continue;
                }

                var score = ScanWindow(mixture, gameReference, candidateStart, maxLag);
                if (score.Count > 0 && score.Value > best.Value)
                {
                    best = score;
                }
            }

            if (best.Count <= 0)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            var referenceRms = loudestScore.Count <= 0 || loudestScore.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(loudestScore.ReferenceEnergy / loudestScore.Count);
            if (referenceRms <= SilentReferenceRms)
            {
                // There is no audible game signal to leak out of the sidecar. Treat it as already
                // clean so a chime over a silent/loading scene is not discarded for low correlation.
                diagnostics.Correlation = 1;
                return PcmCancellationOutcome.CleanNoGameDetected;
            }

            var globalGain = best.ReferenceEnergy <= 0 ? 0 : best.Dot / best.ReferenceEnergy;
            diagnostics.Correlation = best.Value;
            diagnostics.Gain = globalGain;
            diagnostics.StartLagMs = best.LagFrames * 1000.0 / 48000.0;
            diagnostics.EndLagMs = diagnostics.StartLagMs;

            if (Math.Abs(globalGain) < CleanGainCeiling && Math.Abs(best.Value) < CleanCorrelationCeiling)
            {
                return PcmCancellationOutcome.CleanNoGameDetected;
            }

            if (best.Value < MinimumCancellationCorrelation ||
                globalGain < minimumGain || globalGain > maximumGain)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            // Subtract into a copy so a failed verification leaves the caller's mixture pristine.
            var working = (byte[])mixture.Clone();
            var suppressionsDb = new List<double>();
            var measuredBlocks = new List<MeasuredBlock>();
            var previousLag = (double)best.LagFrames;
            var previousGain = 0.0;
            var firstBlock = true;

            for (var blockStart = 0; blockStart < mixtureFrames; blockStart += BlockFrames)
            {
                var blockEnd = Math.Min(mixtureFrames, blockStart + BlockFrames);
                var block = FitBlock(
                    mixture, gameReference, blockStart, blockEnd,
                    (int)Math.Round(previousLag), best.LagFrames, maximumGain);

                var blockGain = block.Gain;
                var blockLag = block.LagFrames;
                var tornBlock = false;
                if (!block.HasSignal || blockGain < blockGainFloor)
                {
                    // A silent reference means nothing to remove. A LOUD reference that cannot be
                    // projected onto the mixture at any lag is different: the global gate already
                    // proved the game leaks into this sidecar, so this block's audio is unexplained
                    // (a recorder timing tear) — score it as zero-suppression so it is muted on an
                    // otherwise-verified pass and sinks the quartile on a badly torn one.
                    tornBlock = block.HasSignal;
                    blockGain = 0;
                    blockLag = previousLag;
                    if (block.HasSignal)
                    {
                        diagnostics.QuietBlocks++;
                    }
                }

                SubtractBlock(
                    working,
                    gameReference,
                    blockStart,
                    blockEnd,
                    previousGain,
                    previousLag,
                    blockGain,
                    blockLag,
                    firstBlock ? 0 : CrossfadeFrames);

                diagnostics.TotalBlocks++;
                if (blockGain > 0)
                {
                    diagnostics.SubtractedBlocks++;
                    var blockSuppression = MeasureSuppressionDb(
                        mixture, working, gameReference, blockStart, blockEnd, (int)Math.Round(blockLag));
                    suppressionsDb.Add(blockSuppression);
                    measuredBlocks.Add(new MeasuredBlock
                    {
                        StartFrame = blockStart,
                        EndFrame = blockEnd,
                        SuppressionDb = blockSuppression,
                    });
                    previousLag = blockLag;
                }
                else if (tornBlock)
                {
                    suppressionsDb.Add(0);
                    measuredBlocks.Add(new MeasuredBlock
                    {
                        StartFrame = blockStart,
                        EndFrame = blockEnd,
                        SuppressionDb = 0,
                    });
                }

                previousGain = blockGain;
                firstBlock = false;
            }

            if (suppressionsDb.Count == 0)
            {
                // The global fit promised a game track but no block found one to subtract; the
                // mixture is effectively clean and was not modified beyond ramp no-ops.
                return PcmCancellationOutcome.CleanNoGameDetected;
            }

            suppressionsDb.Sort();
            var suppression = suppressionsDb[(suppressionsDb.Count - 1) * 3 / 4];
            diagnostics.SuppressionDb = suppression;
            diagnostics.WeakestBlockSuppressionDb = suppressionsDb[0];
            diagnostics.EndLagMs = previousLag * 1000.0 / 48000.0;
            if (suppression < MinimumSuppressionDb)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            // A pump timing tear can leave an isolated block where the game demonstrably survived
            // the subtraction. Shipping it would composite a burst of wrong-time game audio into
            // the chime — the exact artifact this pass exists to remove — so silence those blocks
            // (ramped, so no clicks) rather than let the quartile pass carry them through.
            if (muteUnverifiedBlocks)
            {
                foreach (var measured in measuredBlocks)
                {
                    if (measured.SuppressionDb < MinimumSuppressionDb)
                    {
                        MuteBlock(working, measured.StartFrame, measured.EndFrame);
                        diagnostics.MutedBlocks++;
                    }
                }
            }

            // How much of the reference still projects onto what shipped. The suppression figure
            // above is measured per block on the blocks that were subtracted; this is the whole
            // slice, so a low number here with a residual a listener can still hear means the sound
            // is not this reference's signal and no cancellation will remove it.
            var residual = ScoreCorrelation(working, gameReference, best.LagFrames, loudestStart);
            diagnostics.ResidualCorrelation = residual.Count > 0 ? Math.Abs(residual.Value) : 0;

            Buffer.BlockCopy(working, 0, mixture, 0, mixture.Length);
            return PcmCancellationOutcome.CancelledVerified;
        }

        /// <summary>Silences one block in place, ramping over CrossfadeFrames at both edges.</summary>
        private static void MuteBlock(byte[] working, int blockStartFrame, int blockEndFrame)
        {
            var blockFrames = blockEndFrame - blockStartFrame;
            var ramp = Math.Min(CrossfadeFrames, blockFrames / 2);
            for (var frame = blockStartFrame; frame < blockEndFrame; frame++)
            {
                var intoBlock = frame - blockStartFrame;
                var untilEnd = blockEndFrame - 1 - frame;
                double scale = 0;
                if (intoBlock < ramp)
                {
                    scale = 1.0 - intoBlock / (double)ramp;
                }
                else if (untilEnd < ramp)
                {
                    scale = 1.0 - untilEnd / (double)ramp;
                }

                if (scale >= 1)
                {
                    continue;
                }

                for (var channel = 0; channel < 2; channel++)
                {
                    var offset = frame * BlockAlign + channel * 2;
                    WriteInt16(working, offset, (short)(ReadInt16(working, offset) * scale));
                }
            }
        }

        private struct MeasuredBlock
        {
            public int StartFrame;
            public int EndFrame;
            public double SuppressionDb;
        }

        /// <summary>
        /// Refines lag (to fractional precision via parabolic peak interpolation) and least-squares
        /// gain for one block, searching a small neighbourhood around the tracked lag.
        /// </summary>
        private static BlockFit FitBlock(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            int centerLagFrames,
            int relockCenterLagFrames,
            double maximumGain)
        {
            var search = SearchLags(
                mixture, reference, blockStartFrame, blockEndFrame,
                centerLagFrames, BlockLagSearchFrames);
            var scores = search.Scores;
            var bestIndex = search.BestIndex;
            var bestValue = bestIndex >= 0 ? scores[bestIndex].Value : double.NegativeInfinity;

            if (bestIndex < 0 || bestValue < BlockRelockCorrelation)
            {
                // Centred on the alignment the global pass found, not on zero: when the two streams
                // sit far apart, a search around zero cannot reach the real lag at all.
                var wide = SearchLags(
                    mixture, reference, blockStartFrame, blockEndFrame,
                    relockCenterLagFrames, MaxCancellationLagFrames);
                if (wide.BestIndex >= 0 &&
                    wide.Scores[wide.BestIndex].Value >
                        Math.Max(bestValue + BlockRelockMargin, BlockRelockCorrelation))
                {
                    scores = wide.Scores;
                    bestIndex = wide.BestIndex;
                }
            }

            var fit = default(BlockFit);
            if (bestIndex < 0)
            {
                return fit;
            }

            var bestScore = scores[bestIndex];
            var rms = bestScore.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(bestScore.ReferenceEnergy / bestScore.Count);
            if (rms <= SilentReferenceRms)
            {
                return fit;
            }

            var fraction = 0.0;
            if (bestIndex > 0 && bestIndex < scores.Length - 1 &&
                scores[bestIndex - 1].Count > 0 && scores[bestIndex + 1].Count > 0)
            {
                var left = scores[bestIndex - 1].Value;
                var peak = scores[bestIndex].Value;
                var right = scores[bestIndex + 1].Value;
                var denominator = left - 2 * peak + right;
                if (denominator < -1e-12)
                {
                    fraction = Math.Max(-0.5, Math.Min(0.5, 0.5 * (left - right) / denominator));
                }
            }

            fit.HasSignal = true;
            fit.LagFrames = bestScore.LagFrames + fraction;

            // Clamped to the caller's plausible maximum, not the default: a caller that accepts a
            // wider ratio globally would otherwise have every block silently pinned back to 1.5.
            fit.Gain = Math.Max(0, Math.Min(
                maximumGain,
                bestScore.ReferenceEnergy <= 0 ? 0 : bestScore.Dot / bestScore.ReferenceEnergy));
            return fit;
        }

        private struct LagSearch
        {
            public CorrelationScore[] Scores;
            public int BestIndex;
        }

        private static LagSearch SearchLags(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            int centerLagFrames,
            int halfWidthFrames)
        {
            var scores = new CorrelationScore[2 * halfWidthFrames + 1];
            var bestIndex = -1;
            var bestValue = double.NegativeInfinity;
            for (var i = 0; i < scores.Length; i++)
            {
                var lag = centerLagFrames - halfWidthFrames + i;
                scores[i] = ScoreBlock(mixture, reference, lag, blockStartFrame, blockEndFrame);
                if (scores[i].Count > 0 && scores[i].Value > bestValue)
                {
                    bestValue = scores[i].Value;
                    bestIndex = i;
                }
            }

            return new LagSearch { Scores = scores, BestIndex = bestIndex };
        }

        /// <summary>
        /// Subtracts the gain-scaled, fractionally delayed reference from one block of the working
        /// buffer, crossfading from the previous block's parameters over the first
        /// <paramref name="crossfadeFrames"/> frames so lag/gain steps cannot click.
        /// </summary>
        private static void SubtractBlock(
            byte[] working,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double previousGain,
            double previousLag,
            double gain,
            double lag,
            int crossfadeFrames)
        {
            if (gain <= 0 && previousGain <= 0)
            {
                return;
            }

            for (var frame = blockStartFrame; frame < blockEndFrame; frame++)
            {
                var progress = frame - blockStartFrame;
                var blend = crossfadeFrames > 0 && progress < crossfadeFrames
                    ? progress / (double)crossfadeFrames
                    : 1.0;
                for (var channel = 0; channel < 2; channel++)
                {
                    var subtracted = blend >= 1.0
                        ? gain * SampleAt(reference, frame + lag, channel)
                        : (1.0 - blend) * previousGain * SampleAt(reference, frame + previousLag, channel)
                          + blend * gain * SampleAt(reference, frame + lag, channel);
                    if (subtracted == 0)
                    {
                        continue;
                    }

                    var offset = frame * BlockAlign + channel * 2;
                    var cancelled = ReadInt16(working, offset) - (int)Math.Round(subtracted);
                    cancelled = Math.Max(short.MinValue, Math.Min(short.MaxValue, cancelled));
                    WriteInt16(working, offset, (short)cancelled);
                }
            }
        }

        /// <summary>Reads a linearly interpolated sample at a fractional frame position.</summary>
        private static double SampleAt(byte[] pcm, double framePosition, int channel)
        {
            var frames = pcm.Length / BlockAlign;
            var lower = (int)Math.Floor(framePosition);
            var fraction = framePosition - lower;
            double first = lower >= 0 && lower < frames
                ? ReadInt16(pcm, lower * BlockAlign + channel * 2)
                : 0;
            if (fraction <= 0)
            {
                return first;
            }

            var upper = lower + 1;
            double second = upper >= 0 && upper < frames
                ? ReadInt16(pcm, upper * BlockAlign + channel * 2)
                : 0;
            return first + (second - first) * fraction;
        }

        // Misalignment leaves the surviving game correlated at lags a frame or two off the
        // estimate, so the projection peak searches this neighbourhood on both sides.
        private const int SuppressionProbeLagFrames = 8;

        /// <summary>
        /// Suppression of the reference-correlated (game) component of one block, in dB: the peak
        /// squared projection onto the reference near the block lag, before vs after subtraction.
        /// A raw energy ratio would be bounded by whatever legitimately shares the block — a loud
        /// chime over quiet game audio caps it near 0 dB and fails good passes — whereas the chime
        /// is uncorrelated with the reference and cannot bias a projection.
        /// </summary>
        private static double MeasureSuppressionDb(
            byte[] original,
            byte[] working,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            int lagFrames)
        {
            var before = ProjectionPeak(original, reference, blockStartFrame, blockEndFrame, lagFrames);
            var after = ProjectionPeak(working, reference, blockStartFrame, blockEndFrame, lagFrames);
            if (before <= 0)
            {
                return 0;
            }

            if (after <= 0)
            {
                return FullSuppressionDb;
            }

            return Math.Min(FullSuppressionDb, 10.0 * Math.Log10(before / after));
        }

        /// <summary>Largest normalized squared projection onto the reference near a lag.</summary>
        private static double ProjectionPeak(
            byte[] signal,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            int centerLagFrames)
        {
            double peak = 0;
            for (var lag = centerLagFrames - SuppressionProbeLagFrames;
                 lag <= centerLagFrames + SuppressionProbeLagFrames;
                 lag++)
            {
                var score = ScoreBlock(signal, reference, lag, blockStartFrame, blockEndFrame);
                if (score.Count > 0 && score.ReferenceEnergy > 0)
                {
                    var projection = score.Dot * score.Dot / score.ReferenceEnergy;
                    if (projection > peak)
                    {
                        peak = projection;
                    }
                }
            }

            return peak;
        }

        /// <summary>
        /// Best lag for one analysis window over the full search range. Beyond the default range the
        /// search goes coarse-to-fine — a stride over the whole span, then every lag around the
        /// winner — so a wide window (needed when two capture clients sit far apart, as a controller
        /// endpoint does) costs about what the narrow one always cost. Haptic and game audio are
        /// broadband enough at these strides for the correlation peak to survive the coarse pass.
        /// </summary>
        private static CorrelationScore ScanWindow(
            byte[] mixture,
            byte[] reference,
            int analysisStart,
            int maxLag)
        {
            if (maxLag <= MaxCancellationLagFrames)
            {
                return ScanLagRange(mixture, reference, analysisStart, -maxLag, maxLag, 1);
            }

            var coarse = ScanLagRange(
                mixture, reference, analysisStart, -maxLag, maxLag, CoarseLagStrideFrames, true);
            if (coarse.Count <= 0)
            {
                return coarse;
            }

            var fine = ScanLagRange(
                mixture,
                reference,
                analysisStart,
                coarse.LagFrames - CoarseLagStrideFrames,
                coarse.LagFrames + CoarseLagStrideFrames,
                1,
                true);
            return fine.Count > 0 && fine.Value > coarse.Value ? fine : coarse;
        }

        private static CorrelationScore ScanLagRange(
            byte[] mixture,
            byte[] reference,
            int analysisStart,
            int fromLag,
            int toLag,
            int step)
        {
            return ScanLagRange(mixture, reference, analysisStart, fromLag, toLag, step, false);
        }

        /// <summary>
        /// Best lag in a range. With <paramref name="preferSmallLag"/>, a candidate only displaces the
        /// incumbent if it scores meaningfully better, and among candidates that score alike the one
        /// nearest zero wins.
        /// <para>
        /// This matters for a periodic reference: a rumble waveform correlates almost as well one
        /// period away as at the truth, so a wide search hands the fit a row of near-equal wrong
        /// answers, and subtracting a phase-shifted copy removes little where the true alignment
        /// would remove tens of dB. Alignment is already corrected at capture time from each
        /// capture's own packet stamp, so the answer nearest zero is also the likeliest one.
        /// </para>
        /// </summary>
        private static CorrelationScore ScanLagRange(
            byte[] mixture,
            byte[] reference,
            int analysisStart,
            int fromLag,
            int toLag,
            int step,
            bool preferSmallLag)
        {
            var best = default(CorrelationScore);
            best.Value = double.NegativeInfinity;
            for (var lag = fromLag; lag <= toLag; lag += step)
            {
                var score = ScoreCorrelation(mixture, reference, lag, analysisStart);
                if (!preferSmallLag)
                {
                    if (score.Value > best.Value)
                    {
                        best = score;
                    }

                    continue;
                }

                if (score.Value > best.Value + PeriodicAmbiguityMargin ||
                    (score.Value > best.Value - PeriodicAmbiguityMargin &&
                     Math.Abs(score.LagFrames) < Math.Abs(best.LagFrames)))
                {
                    best = score;
                }
            }

            return best;
        }

        private static int FindLoudestWindowStart(byte[] reference, int referenceFrames)
        {
            var windowFrames = Math.Min(CorrelationWindowFrames, referenceFrames);
            var lastStart = referenceFrames - windowFrames;
            var step = Math.Max(1, windowFrames / 2);
            var bestStart = 0;
            var bestEnergy = -1d;

            for (var start = 0; ; start = Math.Min(start + step, lastStart))
            {
                double energy = 0;
                for (var frame = start; frame < start + windowFrames; frame += CorrelationStrideFrames)
                {
                    var offset = frame * BlockAlign;
                    for (var channel = 0; channel < 2; channel++)
                    {
                        var sample = ReadInt16(reference, offset + channel * 2);
                        energy += sample * (double)sample;
                    }
                }

                if (energy > bestEnergy)
                {
                    bestEnergy = energy;
                    bestStart = start;
                }

                if (start == lastStart)
                {
                    return bestStart;
                }
            }
        }

        private static CorrelationScore ScoreCorrelation(
            byte[] mixture,
            byte[] reference,
            int lagFrames,
            int analysisStart)
        {
            var mixtureFrames = mixture.Length / BlockAlign;
            var referenceFrames = reference.Length / BlockAlign;
            var referenceStart = Math.Max(analysisStart, lagFrames);
            var referenceEnd = Math.Min(
                Math.Min(referenceFrames, analysisStart + CorrelationWindowFrames),
                mixtureFrames + lagFrames);
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            for (var referenceFrame = referenceStart;
                 referenceFrame < referenceEnd;
                 referenceFrame += CorrelationStrideFrames)
            {
                var mixtureFrame = referenceFrame - lagFrames;
                var mixtureByte = mixtureFrame * BlockAlign;
                var referenceByte = referenceFrame * BlockAlign;
                for (var channel = 0; channel < 2; channel++)
                {
                    var mixed = ReadInt16(mixture, mixtureByte + channel * 2);
                    var source = ReadInt16(reference, referenceByte + channel * 2);
                    dot += mixed * (double)source;
                    mixtureEnergy += mixed * (double)mixed;
                    referenceEnergy += source * (double)source;
                    count++;
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            return new CorrelationScore
            {
                LagFrames = lagFrames,
                Dot = dot,
                ReferenceEnergy = referenceEnergy,
                Count = count,
                Value = denominator > 0 ? dot / denominator : 0,
            };
        }

        /// <summary>Correlation over one mixture block, in mixture-frame coordinates.</summary>
        private static CorrelationScore ScoreBlock(
            byte[] mixture,
            byte[] reference,
            int lagFrames,
            int blockStartFrame,
            int blockEndFrame)
        {
            var referenceFrames = reference.Length / BlockAlign;
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            for (var frame = blockStartFrame; frame < blockEndFrame; frame += CorrelationStrideFrames)
            {
                var referenceFrame = frame + lagFrames;
                if (referenceFrame < 0 || referenceFrame >= referenceFrames)
                {
                    continue;
                }

                var mixtureByte = frame * BlockAlign;
                var referenceByte = referenceFrame * BlockAlign;
                for (var channel = 0; channel < 2; channel++)
                {
                    var mixed = ReadInt16(mixture, mixtureByte + channel * 2);
                    var source = ReadInt16(reference, referenceByte + channel * 2);
                    dot += mixed * (double)source;
                    mixtureEnergy += mixed * (double)mixed;
                    referenceEnergy += source * (double)source;
                    count++;
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            return new CorrelationScore
            {
                LagFrames = lagFrames,
                Dot = dot,
                ReferenceEnergy = referenceEnergy,
                Count = count,
                Value = denominator > 0 ? dot / denominator : 0,
            };
        }

        private static short ReadInt16(byte[] bytes, long offset)
        {
            return (short)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static void WriteInt16(byte[] bytes, long offset, short value)
        {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        private struct CorrelationScore
        {
            public int LagFrames;
            public double Dot;
            public double ReferenceEnergy;
            public long Count;
            public double Value;
        }

        private struct BlockFit
        {
            public bool HasSignal;
            public double LagFrames;
            public double Gain;
        }
    }
}
