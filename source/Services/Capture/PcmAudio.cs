using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

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

        /// <summary>The same fixed calibrated lag at the end of the slice, in milliseconds.</summary>
        public double EndLagMs;

        /// <summary>Fitted mixture/reference gain at the global alignment.</summary>
        public double Gain;

        /// <summary>Normalized correlation at the global alignment.</summary>
        public double Correlation;

        /// <summary>Whether the loudest reference window contains audible signal.</summary>
        public bool ReferenceHasSignal;

        /// <summary>RMS level of the loudest reference window, in 16-bit sample units.</summary>
        public double ReferenceRms;

        /// <summary>Upper-quartile per-block energy suppression achieved by the subtraction.</summary>
        public double SuppressionDb;

        /// <summary>Blocks the subtraction actually ran on, out of the whole slice.</summary>
        public int SubtractedBlocks;

        /// <summary>
        /// Blocks skipped because the reference's own level there was too low to fit against. The
        /// difference between these and the rest of the skipped blocks is the difference between
        /// "nothing was playing" and "a copy too quiet to pass the floor was left in".
        /// </summary>
        public int QuietBlocks;

        /// <summary>
        /// Blocks straight-subtracted with the slice-wide calibrated lag. They still
        /// require the ordinary measured suppression proof.
        /// </summary>
        public int FixedFitBlocks;

        /// <summary>
        /// Whether a weak whole-slice result was accepted by retaining only its independently
        /// verified blocks and restoring every other block exactly as recorded.
        /// </summary>
        public bool PartialCommit;

        /// <summary>
        /// Blocks whose subtraction could not be shown to improve them and were put back as
        /// recorded. Uncertain audio is never muted.
        /// </summary>
        public int RestoredBlocks;

        /// <summary>Blocks in the slice.</summary>
        public int TotalBlocks;

        /// <summary>
        /// Correlation between the finished residual and the reference, at the alignment the pass
        /// used. Near zero means the reference's signal is gone from the mixture — so anything a
        /// listener still hears is not the captured copy, and cancellation cannot be the answer.
        /// </summary>
        public double ResidualCorrelation;

        /// <summary>
        /// Blocks whose lag was re-locked away from the slice-wide calibration because the
        /// reference correlated better there. A recorder alignment tear inside the reference's
        /// span moves every block after it.
        /// </summary>
        public int RelockedBlocks;

        /// <summary>Largest per-block lag departure from the slice-wide calibration, in ms.</summary>
        public double MaxBlockLagShiftMs;
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
        // Internal so the scoring-equivalence tests mirror the exact window the scorer walks.
        internal const int CorrelationStrideFrames = 8;
        internal const int CorrelationWindowFrames = 24000; // score 0.5 s at the loudest passage
        private const double SilentReferenceRms = 16.0; // about -66 dBFS

        // ATTEMPT gate, not the accept gate. It only screens out an obviously unrelated reference;
        // held-out projection measurements make the actual keep/restore decision after subtraction.
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

        // The stamped tracks use one calibrated endpoint-latency offset. Blocks only re-fit scale;
        // they never chase local correlation peaks or move the shared timeline.
        private const int BlockFrames = 24000; // 0.5 s


        // Correlation difference below which two candidate lags count as scoring alike, so the one
        // nearest zero is taken. Only applied to searches wider than the default.
        private const double PeriodicAmbiguityMargin = 0.02;

        // Candidate count above which a lag scan scores its grid on all cores. Each candidate's
        // score is independent and the buffers are only read; selection stays a sequential fold in
        // lag order, so the winner is exactly the sequential loop's.
        private const int ParallelLagScanThreshold = 256;
        private const int CrossfadeFrames = 240; // 5 ms across block parameter steps
        private const double BlockGainFloor = 0.05; // below this the block has no game to remove
        private const int BlockFitStrideFrames = 2; // even frames fit; odd frames independently prove

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
            if (ticks <= 0)
            {
                return 0;
            }

            // Convert to the nearest sample frame, not first to a truncated byte count. One 48 kHz
            // frame is 208.333 DateTime ticks; truncating 208 ticks to three bytes and aligning down
            // moved a timestamp that represents frame 1 back onto frame 0.
            var wholeSeconds = ticks / TimeSpan.TicksPerSecond;
            var remainder = ticks % TimeSpan.TicksPerSecond;
            var frames = checked(
                wholeSeconds * SampleRate +
                (remainder * SampleRate + TimeSpan.TicksPerSecond / 2) /
                    TimeSpan.TicksPerSecond);
            return checked(frames * BlockAlign);
        }

        /// <summary>
        /// Whether a captured reference needs no work or produced a usable cancellation. A usable
        /// haptic pass may be partial: only independently verified blocks are changed, while all
        /// uncertain audio remains exactly as recorded.
        /// </summary>
        public static bool IsReferenceSafelyAbsentOrRemoved(
            PcmCancellationOutcome outcome,
            PcmCancellationDiagnostics diagnostics)
        {
            return !diagnostics.ReferenceHasSignal ||
                outcome == PcmCancellationOutcome.CancelledVerified;
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
        /// Removes a simultaneously captured reference from a mixture in place. One bounded global
        /// search calibrates the capture-path latency. Every reference-active block is then
        /// subtracted at that fixed sample offset, fitting only its amplitude because sparse haptic
        /// bursts can have a different local level than the whole slice. A disjoint set of samples
        /// verifies the result; a failed block is restored exactly.
        /// Haptic callers may fit and prove left/right gains independently because controller
        /// actuator channels can receive different endpoint scaling; ordinary/chime callers retain
        /// the shared stereo gain by default.
        /// <para>
        /// A caller may supply <paramref name="calibratedLagFrames"/> when that alignment was
        /// independently measured from a stronger common signal. This bypasses only the lag sweep
        /// and its correlation entry gate; fitted gains and held-out block verification are
        /// unchanged, so a bad calibration still restores the original samples.
        /// </para>
        /// </summary>
        public static PcmCancellationOutcome CancelCorrelated(
            byte[] mixture,
            byte[] gameReference,
            out PcmCancellationDiagnostics diagnostics,
            int maxLagFrames = MaxCancellationLagFrames,
            double minimumGain = MinimumCancellationGain,
            double maximumGain = MaximumCancellationGain,
            double blockGainFloor = BlockGainFloor,
            double keepBlockSuppressionDb = MinimumSuppressionDb,
            int cancellationBlockFrames = BlockFrames,
            double maximumResidualCorrelation = double.MaxValue,
            bool commitVerifiedBlocksOnWeakPass = false,
            double minimumCorrelation = MinimumCancellationCorrelation,
            bool attemptVerifiedBlocksWhenGloballyClean = false,
            bool preferEarlyAlignmentWindow = false,
            int verificationLagRadiusFrames = 0,
            bool independentChannelGains = false,
            int gainCrossfadeFrames = CrossfadeFrames,
            int fractionalLagSteps = 0,
            double? calibratedLagFrames = null,
            bool preferSmallLagOnWideSearch = true,
            int blockLagRadiusFrames = 0)
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

            // One short-per-sample view of each buffer, decoded once: the lag search below scores
            // thousands of candidate lags, and reassembling every 16-bit sample from bytes on each
            // of them dominated the whole cleanup's cost. Scoring on a view is bit-identical to
            // scoring on the bytes (PcmAudioLagScanEquivalenceTests holds the two side by side).
            var mixtureView = SampleView.From(mixture);
            var referenceView = SampleView.From(gameReference);
            var referenceScore = ScoreCorrelation(
                referenceView, referenceView, 0, loudestStart);
            diagnostics.ReferenceRms = referenceScore.Count <= 0 || referenceScore.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(referenceScore.ReferenceEnergy / referenceScore.Count);
            diagnostics.ReferenceHasSignal = diagnostics.ReferenceRms > SilentReferenceRms;
            if (!diagnostics.ReferenceHasSignal)
            {
                // There is no audible signal in the reference. Treat it as already clean so a
                // chime over a silent/loading scene is not discarded for low correlation.
                diagnostics.Correlation = 1;
                return PcmCancellationOutcome.CleanNoGameDetected;
            }

            var earlyReference = ScoreCorrelation(referenceView, referenceView, 0, 0);
            var earlyReferenceRms = earlyReference.Count <= 0 || earlyReference.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(earlyReference.ReferenceEnergy / earlyReference.Count);

            // A caller that explicitly prefers the early graph state ultimately selected the
            // early scan below even after paying for four other wide lag sweeps. In the common
            // case where that early scan already proves the reference present, those sweeps cannot
            // affect any gate or the chosen lag: best is early and the clean gate is already
            // impossible. Start there and skip the mathematically irrelevant work. A weak early
            // score still scans every spread window to preserve the old "present but unseparable"
            // safety verdict instead of misclassifying a shifted reference as clean.
            var earlyPreferred = !calibratedLagFrames.HasValue &&
                preferEarlyAlignmentWindow && earlyReferenceRms > SilentReferenceRms;
            var best = default(CorrelationScore);
            var presence = 0d;
            if (earlyPreferred)
            {
                best = ScanWindow(
                    mixtureView, referenceView, 0, maxLag, preferSmallLagOnWideSearch);
                presence = best.Count > 0 ? Math.Abs(best.Value) : 0;
            }

            if (!earlyPreferred || best.Count <= 0 || presence < CleanCorrelationCeiling)
            {
                // Score several candidate windows spread across the slice, not just the loudest
                // one: recorder streams can carry an alignment tear (a pump timing step, observed
                // when a render stream starts), and one window can straddle it. A tear cannot
                // fracture every window.
                var loudestScore = calibratedLagFrames.HasValue
                    ? ScoreCorrelationAtLag(
                        mixtureView,
                        referenceView,
                        calibratedLagFrames.Value,
                        loudestStart)
                    : ScanWindow(
                        mixtureView,
                        referenceView,
                        loudestStart,
                        maxLag,
                        preferSmallLagOnWideSearch);
                if (!earlyPreferred || best.Count <= 0)
                {
                    best = loudestScore;
                }

                presence = Math.Max(
                    presence,
                    loudestScore.Count > 0 ? Math.Abs(loudestScore.Value) : 0);
                foreach (var candidateStart in new[]
                    { 0, (int)((long)referenceFrames / 3), (int)(2L * referenceFrames / 3) })
                {
                    if (Math.Abs(candidateStart - loudestStart) < CorrelationWindowFrames / 2)
                    {
                        continue;
                    }

                    var score = calibratedLagFrames.HasValue
                        ? ScoreCorrelationAtLag(
                            mixtureView,
                            referenceView,
                            calibratedLagFrames.Value,
                            candidateStart)
                        : ScanWindow(
                            mixtureView,
                            referenceView,
                            candidateStart,
                            maxLag,
                            preferSmallLagOnWideSearch);
                    if (score.Count <= 0)
                    {
                        continue;
                    }

                    presence = Math.Max(presence, Math.Abs(score.Value));
                    if (!earlyPreferred && score.Value > best.Value)
                    {
                        best = score;
                    }
                }
            }

            if (best.Count <= 0)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            if (fractionalLagSteps > 0 && !calibratedLagFrames.HasValue)
            {
                best = RefineFractionalLag(
                    mixtureView,
                    referenceView,
                    best,
                    Math.Max(2, fractionalLagSteps));
            }

            var globalGain = best.ReferenceEnergy <= 0 ? 0 : best.Dot / best.ReferenceEnergy;
            diagnostics.Correlation = best.Value;
            diagnostics.Gain = globalGain;
            var fixedLagFrames = best.ExactLagFrames;
            diagnostics.StartLagMs = fixedLagFrames * 1000.0 / 48000.0;
            diagnostics.EndLagMs = diagnostics.StartLagMs;

            if (Math.Abs(globalGain) < CleanGainCeiling &&
                Math.Abs(best.Value) < CleanCorrelationCeiling &&
                presence < CleanCorrelationCeiling &&
                !attemptVerifiedBlocksWhenGloballyClean)
            {
                return PcmCancellationOutcome.CleanNoGameDetected;
            }

            if ((!calibratedLagFrames.HasValue &&
                    best.Value < Math.Max(0, minimumCorrelation)) ||
                globalGain < minimumGain || globalGain > maximumGain)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            // Subtract into a copy so a failed verification leaves the caller's mixture pristine.
            var working = (byte[])mixture.Clone();
            var suppressionsDb = new List<double>();
            var measuredBlocks = new List<MeasuredBlock>();
            var previousLeftGain = 0.0;
            var previousRightGain = 0.0;
            var previousLagFrames = fixedLagFrames;
            var firstBlock = true;
            var requestedCrossfadeFrames = Math.Max(0, gainCrossfadeFrames);
            var blockFrames = Math.Max(
                Math.Max(1, cancellationBlockFrames),
                requestedCrossfadeFrames * 2);

            for (var blockStart = 0; blockStart < mixtureFrames;)
            {
                var blockEnd = Math.Min(mixtureFrames, blockStart + blockFrames);

                // A recorder alignment tear inside the reference's span leaves every block after
                // it at a slightly different lag (2026-09-05 clips: 8-24 frames, either direction,
                // 1.2-2.7 s into a 4.5 s sound). A caller that allows it lets each block re-lock
                // within a small radius of the slice-wide calibration; the block's own measured
                // suppression, probed across both lags, still decides whether it stays.
                var blockLagFrames = fixedLagFrames;
                if (blockLagRadiusFrames > 0)
                {
                    blockLagFrames = RelockBlockLag(
                        mixture, gameReference, blockStart, blockEnd,
                        fixedLagFrames, blockLagRadiusFrames);

                    // A tear lies inside a block, not on its boundary, and the block re-locks to
                    // whichever side holds more of the sound. Find where the block's correlation at
                    // that lag breaks, re-lock the other side on its own, and if it sits at a
                    // different lag finish this iteration at the break so neither side is
                    // subtracted at the other's alignment.
                    var tear = FindLagBreakFrame(
                        mixture, gameReference, blockStart, blockEnd, blockLagFrames,
                        out var prefixIsOffLag);
                    if (tear > blockStart && tear < blockEnd)
                    {
                        if (prefixIsOffLag)
                        {
                            var prefixLag = RelockBlockLag(
                                mixture, gameReference, blockStart, tear,
                                fixedLagFrames, blockLagRadiusFrames);
                            if (Math.Abs(prefixLag - blockLagFrames) >= 1)
                            {
                                blockEnd = tear;
                                blockLagFrames = prefixLag;
                            }
                        }
                        else
                        {
                            var tailLag = RelockBlockLag(
                                mixture, gameReference, tear, blockEnd,
                                fixedLagFrames, blockLagRadiusFrames);
                            if (Math.Abs(tailLag - blockLagFrames) >= 1)
                            {
                                blockEnd = tear;
                            }
                        }
                    }

                    if (blockLagFrames != fixedLagFrames)
                    {
                        diagnostics.RelockedBlocks++;
                        diagnostics.MaxBlockLagShiftMs = Math.Max(
                            diagnostics.MaxBlockLagShiftMs,
                            Math.Abs(blockLagFrames - fixedLagFrames) * 1000.0 / SampleRate);
                    }
                }

                // A re-locked block is measured across both lags so a spurious re-lock cannot hide
                // the copy still sitting at the calibration; an unmoved block keeps the caller's
                // exact radius.
                var probeRadiusFrames = blockLagFrames == fixedLagFrames
                    ? Math.Max(0, verificationLagRadiusFrames)
                    : Math.Max(
                        Math.Max(0, verificationLagRadiusFrames),
                        (int)Math.Ceiling(Math.Abs(blockLagFrames - fixedLagFrames)) +
                            SuppressionProbeLagFrames);
                var block = FitKnownBlock(
                    mixture, gameReference, blockStart, blockEnd,
                    blockLagFrames, maximumGain, independentChannelGains);
                var blockScore = ScoreBlock(
                    mixture, gameReference, blockLagFrames, blockStart, blockEnd);
                var blockReferenceRms = blockScore.Count <= 0 || blockScore.ReferenceEnergy <= 0
                    ? 0
                    : Math.Sqrt(blockScore.ReferenceEnergy / blockScore.Count);
                var blockMixtureEnergy = MixtureEnergy(mixture, blockStart, blockEnd);

                // The timestamps plus one slice-wide calibration choose the samples. The local
                // least-squares fit only chooses their scale, bounded by the caller's gain range.
                // Controller callers fit the two actuator channels separately.
                var leftGain = block.LeftGain;
                var rightGain = block.RightGain;
                var failedActiveBlock = false;

                if (!block.LeftHasSignal || leftGain < blockGainFloor)
                {
                    leftGain = 0;
                }
                if (!block.RightHasSignal || rightGain < blockGainFloor)
                {
                    rightGain = 0;
                }

                var hasFittedGain = leftGain > 0 || rightGain > 0;
                var blockGainSquared = hasFittedGain
                    ? (leftGain * leftGain + rightGain * rightGain) / 2
                    : globalGain * globalGain;
                var blockPlayedEnergy = blockGainSquared * blockScore.ReferenceEnergy;
                var blockAudible = blockPlayedEnergy >=
                    Math.Max(0, blockMixtureEnergy - blockPlayedEnergy);
                if (block.HasSignal && hasFittedGain)
                {
                    // Timestamp alignment plus the one slice-wide calibration decides what to try.
                    // Measurement below still decides whether it remains; otherwise the recorded
                    // block is restored byte-for-byte.
                    diagnostics.FixedFitBlocks++;
                }

                if (!block.HasSignal || !hasFittedGain)
                {
                    // A silent reference means nothing to remove. An active reference whose fitted
                    // copy falls below the safe floor is a failed block: restore it exactly
                    // instead of amplifying a noise-sized estimate.
                    failedActiveBlock = block.HasSignal;
                    if (block.HasSignal)
                    {
                        diagnostics.QuietBlocks++;
                    }
                }

                var crossfadeFrames = firstBlock
                    ? 0
                    : Math.Min(requestedCrossfadeFrames, (blockEnd - blockStart) / 2);
                var blockWasModified = hasFittedGain ||
                    (crossfadeFrames > 0 &&
                     (previousLeftGain > 0 || previousRightGain > 0) &&
                     block.HasSignal);
                SubtractBlock(
                    working,
                    gameReference,
                    blockStart,
                    blockEnd,
                    previousLeftGain,
                    previousRightGain,
                    leftGain,
                    rightGain,
                    previousLagFrames,
                    blockLagFrames,
                    crossfadeFrames);

                diagnostics.TotalBlocks++;
                if (hasFittedGain)
                {
                    diagnostics.SubtractedBlocks++;
                    double blockSuppression;
                    if (independentChannelGains)
                    {
                        blockSuppression = FullSuppressionDb;
                        if (block.LeftHasSignal)
                        {
                            blockSuppression = leftGain <= 0
                                ? 0
                                : Math.Min(blockSuppression, MeasureSuppressionDb(
                                    mixture, working, gameReference, blockStart, blockEnd,
                                    blockLagFrames,
                                    probeRadiusFrames,
                                    1,
                                    0));
                        }
                        if (block.RightHasSignal)
                        {
                            blockSuppression = rightGain <= 0
                                ? 0
                                : Math.Min(blockSuppression, MeasureSuppressionDb(
                                    mixture, working, gameReference, blockStart, blockEnd,
                                    blockLagFrames,
                                    probeRadiusFrames,
                                    1,
                                    1));
                        }
                    }
                    else
                    {
                        blockSuppression = MeasureSuppressionDb(
                            mixture, working, gameReference, blockStart, blockEnd,
                            blockLagFrames,
                            probeRadiusFrames,
                            1);
                    }
                    suppressionsDb.Add(blockSuppression);
                    measuredBlocks.Add(new MeasuredBlock
                    {
                        StartFrame = blockStart,
                        EndFrame = blockEnd,
                        SuppressionDb = blockSuppression,
                        Subtracted = true,
                        ReferenceRms = blockReferenceRms,
                        Audible = blockAudible,
                    });
                }
                else if (failedActiveBlock)
                {
                    if (blockWasModified)
                    {
                        // The gain ramp from the preceding block reaches into this one even though
                        // its own fitted gain is zero. Count that real modification so a no-mute
                        // caller restores it instead of injecting the reference into uncertain audio.
                        diagnostics.SubtractedBlocks++;
                    }
                    suppressionsDb.Add(0);
                    measuredBlocks.Add(new MeasuredBlock
                    {
                        StartFrame = blockStart,
                        EndFrame = blockEnd,
                        SuppressionDb = 0,
                        Subtracted = blockWasModified,
                        ReferenceRms = blockReferenceRms,
                        Audible = blockAudible,
                    });
                }

                previousLeftGain = leftGain;
                previousRightGain = rightGain;
                previousLagFrames = blockLagFrames;
                if (hasFittedGain)
                {
                    diagnostics.EndLagMs = blockLagFrames * 1000.0 / SampleRate;
                }
                firstBlock = false;
                blockStart = blockEnd;
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
            var weakOverallPass = suppression < MinimumSuppressionDb;
            if (weakOverallPass && !commitVerifiedBlocksOnWeakPass)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            // A block that did not show the requested suppression must never ship tentatively
            // changed. Restore it exactly; cancellation never authorizes muting recorded audio.
            foreach (var measured in measuredBlocks)
            {
                if (measured.SuppressionDb >= keepBlockSuppressionDb)
                {
                    continue;
                }

                if (!measured.Subtracted)
                {
                    // Nothing was taken out of this block, so there is nothing to put back.
                    continue;
                }

                // Not muting does not mean shipping whatever the subtraction did. A block that
                // cannot show it improved goes back to exactly what was recorded, so this pass can
                // only ever leave audio alone or verifiably clean it — never make it worse.
                RestoreBlock(
                    working,
                    mixture,
                    measured.StartFrame,
                    measured.EndFrame,
                    exactBlockRestore: commitVerifiedBlocksOnWeakPass);
                diagnostics.RestoredBlocks++;
                diagnostics.SubtractedBlocks--;
            }

            // Haptic references can be active for most of the slice but removable only during short
            // bursts. A best-effort caller retains blocks that passed the per-block proof and
            // restores everything else. Mark every such mixed result partial, even when its retained
            // blocks make the whole-slice quartile strong, so the field log does not overclaim it.
            if (commitVerifiedBlocksOnWeakPass &&
                diagnostics.SubtractedBlocks > 0 && diagnostics.RestoredBlocks > 0)
            {
                var retainedSuppressions = new List<double>();
                foreach (var measured in measuredBlocks)
                {
                    if (measured.Subtracted && measured.SuppressionDb >= keepBlockSuppressionDb)
                    {
                        retainedSuppressions.Add(measured.SuppressionDb);
                    }
                }

                retainedSuppressions.Sort();
                if (retainedSuppressions.Count > 0)
                {
                    diagnostics.PartialCommit = true;
                    diagnostics.SuppressionDb = retainedSuppressions[
                        (retainedSuppressions.Count - 1) * 3 / 4];
                }
            }

            if (diagnostics.SubtractedBlocks == 0)
            {
                // Every tentative change was restored. Report a rejected pass instead of claiming
                // verified cancellation over a byte-identical active-reference track.
                return PcmCancellationOutcome.Unseparable;
            }

            // How much of the reference still projects onto what shipped. The suppression figure
            // above is measured per block on the blocks that were subtracted; this is the whole
            // slice, so a low number here with a residual a listener can still hear means the sound
            // is not this reference's signal and no cancellation will remove it.
            // `working` was just mutated by the subtraction, so it gets a fresh view here rather
            // than reusing one built before the pass.
            var residual = ScoreCorrelationAtLag(
                SampleView.From(working), referenceView, fixedLagFrames, loudestStart);
            diagnostics.ResidualCorrelation = residual.Count > 0 ? Math.Abs(residual.Value) : 0;
            if (diagnostics.RestoredBlocks == 0 &&
                diagnostics.ResidualCorrelation > Math.Max(0, maximumResidualCorrelation))
            {
                // The caller asked for an independent residual ceiling. Leave the original buffer
                // untouched so it can keep the recorded track rather than commit a weak pass.
                // A partial pass is exempt: its restored blocks legitimately still carry the
                // reference, and every committed block already proved itself on held-out samples —
                // rejecting the whole pass here would trade verified removal for none at all.
                return PcmCancellationOutcome.Unseparable;
            }

            Buffer.BlockCopy(working, 0, mixture, 0, mixture.Length);
            return PcmCancellationOutcome.CancelledVerified;
        }

        /// <summary>
        /// Puts the recorded audio back over one block, crossfading at both edges so the seam with
        /// the subtracted blocks either side cannot click.
        /// </summary>
        private static void RestoreBlock(
            byte[] working,
            byte[] mixture,
            int blockStartFrame,
            int blockEndFrame,
            bool exactBlockRestore = false)
        {
            var blockFrames = blockEndFrame - blockStartFrame;
            var ramp = Math.Min(CrossfadeFrames, blockFrames / 2);
            if (exactBlockRestore)
            {
                // An uncertain haptic block must be precisely what was recorded. Put the transition
                // in the tail of the preceding (already accepted) block; ramping inside this block
                // was subtracting or injecting up to 5 ms of a reference we had just rejected.
                var outsideRamp = Math.Min(ramp, blockStartFrame);
                for (var frame = blockStartFrame - outsideRamp; frame < blockStartFrame; frame++)
                {
                    var weight = (frame - (blockStartFrame - outsideRamp) + 1) /
                        (double)Math.Max(1, outsideRamp);
                    BlendTowardRecorded(working, mixture, frame, weight);
                }

                Buffer.BlockCopy(
                    mixture,
                    blockStartFrame * BlockAlign,
                    working,
                    blockStartFrame * BlockAlign,
                    blockFrames * BlockAlign);
                return;
            }

            for (var frame = blockStartFrame; frame < blockEndFrame; frame++)
            {
                var intoBlock = frame - blockStartFrame;
                var fromEnd = blockEndFrame - 1 - frame;
                var weight = 1.0;
                if (ramp > 0 && intoBlock < ramp)
                {
                    weight = (intoBlock + 1) / (double)ramp;
                }
                else if (ramp > 0 && fromEnd < ramp)
                {
                    weight = (fromEnd + 1) / (double)ramp;
                }

                BlendTowardRecorded(working, mixture, frame, weight);
            }
        }

        private static void BlendTowardRecorded(
            byte[] working,
            byte[] mixture,
            int frame,
            double weight)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                var offset = frame * BlockAlign + channel * 2;
                var subtracted = (double)ReadInt16(working, offset);
                var recorded = (double)ReadInt16(mixture, offset);
                var blended = subtracted + weight * (recorded - subtracted);
                WriteInt16(working, offset, (short)Math.Max(
                    short.MinValue, Math.Min(short.MaxValue, Math.Round(blended))));
            }
        }

        private struct MeasuredBlock
        {
            public int StartFrame;
            public int EndFrame;
            public double SuppressionDb;

            /// <summary>RMS of the reference inside this block at the block's lag.</summary>
            public double ReferenceRms;

            /// <summary>
            /// Whether the played copy of the reference in this block is at least as loud as the
            /// rest of the recorded audio there. Below that, the projection measure's own noise
            /// floor exceeds any strong gate and a remnant is masked, so the block cannot be the
            /// held-out weakest figure.
            /// </summary>
            public bool Audible;

            /// <summary>
            /// Whether anything was actually subtracted here. A block scored zero because its
            /// reference could not be fitted was never modified, so there is nothing to put back —
            /// and is left exactly as recorded.
            /// </summary>
            public bool Subtracted;
        }

        /// <summary>
        /// Uses the lag calibrated over the stamped slice without searching this block. Only the
        /// least-squares scale is fitted locally because a sparse burst's slice-wide gain is diluted
        /// by quiet intervals. This is the shared deterministic path for haptic and chime cleanup:
        /// every reference-active block is attempted, then measured to decide whether it stays.
        /// </summary>
        private static BlockFit FitKnownBlock(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double lagFrames,
            double maximumGain,
            bool independentChannelGains)
        {
            if (independentChannelGains)
            {
                var left = FitKnownChannel(
                    mixture, reference, blockStartFrame, blockEndFrame,
                    lagFrames, maximumGain, 0);
                var right = FitKnownChannel(
                    mixture, reference, blockStartFrame, blockEndFrame,
                    lagFrames, maximumGain, 1);
                return new BlockFit
                {
                    HasSignal = left.HasSignal || right.HasSignal,
                    LeftHasSignal = left.HasSignal,
                    RightHasSignal = right.HasSignal,
                    LeftGain = left.Gain,
                    RightGain = right.Gain,
                };
            }

            var score = ScoreBlock(
                mixture, reference, lagFrames, blockStartFrame, blockEndFrame);
            var rms = score.Count <= 0 || score.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(score.ReferenceEnergy / score.Count);
            var hasSignal = rms > SilentReferenceRms;
            var gain = Math.Max(0, Math.Min(
                maximumGain,
                score.ReferenceEnergy <= 0 ? 0 : score.Dot / score.ReferenceEnergy));
            return new BlockFit
            {
                HasSignal = hasSignal,
                LeftHasSignal = hasSignal,
                RightHasSignal = hasSignal,
                LeftGain = gain,
                RightGain = gain,
            };
        }

        private static ChannelFit FitKnownChannel(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double lagFrames,
            double maximumGain,
            int channel)
        {
            var score = ScoreBlock(
                mixture, reference, lagFrames, blockStartFrame, blockEndFrame, 0, channel);
            var rms = score.Count <= 0 || score.ReferenceEnergy <= 0
                ? 0
                : Math.Sqrt(score.ReferenceEnergy / score.Count);
            return new ChannelFit
            {
                HasSignal = rms > SilentReferenceRms,
                Gain = Math.Max(0, Math.Min(
                    maximumGain,
                    score.ReferenceEnergy <= 0 ? 0 : score.Dot / score.ReferenceEnergy)),
            };
        }

        /// <summary>
        /// Subtracts the gain-scaled reference at the fixed calibrated sample offset, crossfading
        /// gain over the first <paramref name="crossfadeFrames"/> frames so level steps cannot click.
        /// </summary>
        private static void SubtractBlock(
            byte[] working,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double previousLeftGain,
            double previousRightGain,
            double leftGain,
            double rightGain,
            double previousLagFrames,
            double lagFrames,
            int crossfadeFrames)
        {
            if (leftGain <= 0 && rightGain <= 0 &&
                previousLeftGain <= 0 && previousRightGain <= 0)
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
                    var referenceSample = ReadInterpolatedSample(
                        reference, frame + lagFrames, channel);
                    var previousGain = channel == 0 ? previousLeftGain : previousRightGain;
                    var gain = channel == 0 ? leftGain : rightGain;
                    double subtracted;
                    if (blend >= 1.0)
                    {
                        subtracted = gain * referenceSample;
                    }
                    else
                    {
                        // The preceding block may have re-locked to a different lag; fade from
                        // its copy of the reference to this block's, not merely between gains.
                        var previousSample = ReadInterpolatedSample(
                            reference, frame + previousLagFrames, channel);
                        subtracted = (1.0 - blend) * previousGain * previousSample +
                            blend * gain * referenceSample;
                    }

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
        /// <summary>
        /// Finds the lag within <paramref name="radiusFrames"/> of the slice-wide calibration at
        /// which this block correlates best with the reference: coarse integer steps, a fine
        /// integer pass, then quarter-frame steps. Returns the calibration unchanged when the
        /// block does not correlate at least 0.5 anywhere or does not improve on it, so a block
        /// that is mostly game audio cannot wander onto a spurious lag.
        /// </summary>
        private static double RelockBlockLag(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double calibratedLagFrames,
            int radiusFrames)
        {
            var center = Math.Round(calibratedLagFrames);
            var bestLag = center;
            var bestValue = ScoreBlock(
                mixture, reference, center, blockStartFrame, blockEndFrame).Value;
            var calibratedValue = bestValue;

            // Frame-exact search within 1 ms of the calibration first: recorder tears measured in
            // the field were 8-24 frames. A tonal sound's partials repeat a few milliseconds out,
            // so a distant lag can score within a hair of the true one; the far search below may
            // only win by a clear margin over the best near lag.
            const int nearRadius = 48;
            for (var offset = -nearRadius; offset <= nearRadius; offset++)
            {
                if (offset == 0 || Math.Abs(offset) > radiusFrames)
                {
                    continue;
                }

                var value = ScoreBlock(
                    mixture, reference, center + offset, blockStartFrame, blockEndFrame).Value;
                if (value > bestValue)
                {
                    bestValue = value;
                    bestLag = center + offset;
                }
            }

            var nearBestValue = bestValue;
            var farBestLag = bestLag;
            var farBestValue = bestValue;
            for (var offset = -radiusFrames; offset <= radiusFrames; offset += 8)
            {
                if (Math.Abs(offset) <= nearRadius)
                {
                    continue;
                }

                var value = ScoreBlock(
                    mixture, reference, center + offset, blockStartFrame, blockEndFrame).Value;
                if (value > farBestValue)
                {
                    farBestValue = value;
                    farBestLag = center + offset;
                }
            }

            if (farBestLag != bestLag)
            {
                for (var offset = -7; offset <= 7; offset++)
                {
                    var lag = farBestLag + offset;
                    if (Math.Abs(lag - center) > radiusFrames || Math.Abs(lag - center) <= nearRadius)
                    {
                        continue;
                    }

                    var value = ScoreBlock(
                        mixture, reference, lag, blockStartFrame, blockEndFrame).Value;
                    if (value > farBestValue)
                    {
                        farBestValue = value;
                        farBestLag = lag;
                    }
                }

                if (farBestValue > nearBestValue + 0.05)
                {
                    bestValue = farBestValue;
                    bestLag = farBestLag;
                }
            }

            var fineBest = bestLag;
            for (var quarter = -3; quarter <= 3; quarter++)
            {
                if (quarter == 0)
                {
                    continue;
                }

                var lag = fineBest + quarter / 4.0;
                var value = ScoreBlock(
                    mixture, reference, lag, blockStartFrame, blockEndFrame).Value;
                if (value > bestValue)
                {
                    bestValue = value;
                    bestLag = lag;
                }
            }

            if (bestValue < 0.5 || bestValue <= calibratedValue)
            {
                return calibratedLagFrames;
            }

            return bestLag;
        }

        /// <summary>Recorded energy of a block, sampled on the same stride the block scorer uses.</summary>
        private static double MixtureEnergy(byte[] pcm, int blockStartFrame, int blockEndFrame)
        {
            double energy = 0;
            for (var frame = blockStartFrame; frame < blockEndFrame; frame += BlockFitStrideFrames)
            {
                for (var channel = 0; channel < 2; channel++)
                {
                    double sample = ReadInt16(pcm, frame * BlockAlign + channel * 2);
                    energy += sample * sample;
                }
            }

            return energy;
        }

        /// <summary>
        /// Locates a recorder alignment tear inside a block from the block's own 5 ms chunk
        /// correlations at its re-locked lag. Chunks where the reference is silent are ignored.
        /// The tear is the first chunk at which the chunks stop (or start) correlating well, "well"
        /// being at least 70% of the best chunk. <paramref name="prefixIsOffLag"/> says which side
        /// of the tear the lag does not fit. Returns <paramref name="blockEndFrame"/> when every
        /// chunk with signal correlates alike.
        /// </summary>
        private static int FindLagBreakFrame(
            byte[] mixture,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double lagFrames,
            out bool prefixIsOffLag)
        {
            const int chunkFrames = 240;
            prefixIsOffLag = false;
            var starts = new List<int>();
            var values = new List<double>();
            var best = 0.0;
            for (var chunkStart = blockStartFrame; chunkStart < blockEndFrame; chunkStart += chunkFrames)
            {
                var chunkEnd = Math.Min(blockEndFrame, chunkStart + chunkFrames);
                var score = ScoreBlock(mixture, reference, lagFrames, chunkStart, chunkEnd);
                var rms = score.Count <= 0 || score.ReferenceEnergy <= 0
                    ? 0
                    : Math.Sqrt(score.ReferenceEnergy / score.Count);
                if (rms <= SilentReferenceRms)
                {
                    continue;
                }

                starts.Add(chunkStart);
                values.Add(score.Value);
                best = Math.Max(best, score.Value);
            }

            if (values.Count < 2 || best <= 0)
            {
                return blockEndFrame;
            }

            var threshold = best * 0.7;
            var firstGood = values[0] >= threshold;
            for (var i = 1; i < values.Count; i++)
            {
                if ((values[i] >= threshold) != firstGood)
                {
                    prefixIsOffLag = !firstGood;
                    return starts[i];
                }
            }

            return blockEndFrame;
        }

        private static double MeasureSuppressionDb(
            byte[] original,
            byte[] working,
            byte[] reference,
            int blockStartFrame,
            int blockEndFrame,
            double lagFrames,
            int probeLagFrames = SuppressionProbeLagFrames,
            int scoreOffsetFrames = 0,
            int channel = -1)
        {
            var before = ProjectionPeak(
                original, reference, blockStartFrame, blockEndFrame, lagFrames,
                probeLagFrames, scoreOffsetFrames, channel);
            var after = ProjectionPeak(
                working, reference, blockStartFrame, blockEndFrame, lagFrames,
                probeLagFrames, scoreOffsetFrames, channel);
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
            double centerLagFrames,
            int probeLagFrames,
            int scoreOffsetFrames,
            int channel)
        {
            double peak = 0;
            var radius = Math.Max(0, probeLagFrames);
            var coarseStep = radius > 32 ? 8 : 1;
            var bestOffset = 0;
            for (var lagOffset = -radius;
                 lagOffset <= radius;
                 lagOffset += coarseStep)
            {
                var lag = centerLagFrames + lagOffset;
                var score = ScoreBlock(
                    signal, reference, lag, blockStartFrame, blockEndFrame,
                    scoreOffsetFrames, channel);
                if (score.Count > 0 && score.ReferenceEnergy > 0)
                {
                    var projection = score.Dot * score.Dot / score.ReferenceEnergy;
                    if (projection > peak)
                    {
                        peak = projection;
                        bestOffset = lagOffset;
                    }
                }
            }

            // A wide residual audit only needs a coarse sweep to find the neighbourhood. Refine
            // around its winner so a narrow broadband peak cannot fall between coarse candidates.
            if (coarseStep > 1)
            {
                var from = Math.Max(-radius, bestOffset - coarseStep + 1);
                var to = Math.Min(radius, bestOffset + coarseStep - 1);
                for (var lagOffset = from; lagOffset <= to; lagOffset++)
                {
                    var lag = centerLagFrames + lagOffset;
                    var score = ScoreBlock(
                        signal, reference, lag, blockStartFrame, blockEndFrame,
                        scoreOffsetFrames, channel);
                    if (score.Count > 0 && score.ReferenceEnergy > 0)
                    {
                        var projection = score.Dot * score.Dot / score.ReferenceEnergy;
                        if (projection > peak)
                        {
                            peak = projection;
                        }
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
            SampleView mixture,
            SampleView reference,
            int analysisStart,
            int maxLag,
            bool preferSmallLag)
        {
            if (maxLag <= MaxCancellationLagFrames)
            {
                return ScanLagRange(mixture, reference, analysisStart, -maxLag, maxLag, 1);
            }

            // The wide range is swept at 2-frame resolution, never coarser. A 48-frame coarse
            // stride stepped straight over the narrow peak a broadband reference produces (its
            // autocorrelation spans only a few frames), so the sweep saw nothing but sidelobes and
            // the refinement then polished a wrong neighbourhood — observed live as 40-155 ms
            // locks against a stable, strong peak at -12.7 ms. Adjacent-frame correlation of real
            // audio stays far above that sidelobe floor, so a 2-frame stride cannot lose the peak.
            var sweep = ScanLagRange(
                mixture, reference, analysisStart, -maxLag, maxLag, 2, preferSmallLag);
            if (sweep.Count <= 0)
            {
                return sweep;
            }

            var fine = ScanLagRange(
                mixture,
                reference,
                analysisStart,
                sweep.LagFrames - 2,
                sweep.LagFrames + 2,
                1,
                preferSmallLag);
            return fine.Count > 0 && fine.Value > sweep.Value ? fine : sweep;
        }

        private static CorrelationScore ScanLagRange(
            SampleView mixture,
            SampleView reference,
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
            SampleView mixture,
            SampleView reference,
            int analysisStart,
            int fromLag,
            int toLag,
            int step,
            bool preferSmallLag)
        {
            return ScanLagRange(
                mixture, reference, analysisStart, fromLag, toLag, step, preferSmallLag,
                forceSequential: false);
        }

        // Internal (with forceSequential) so the equivalence tests can hold the parallel grid and
        // the plain loop side by side on the same inputs.
        internal static CorrelationScore ScanLagRange(
            SampleView mixture,
            SampleView reference,
            int analysisStart,
            int fromLag,
            int toLag,
            int step,
            bool preferSmallLag,
            bool forceSequential)
        {
            var best = default(CorrelationScore);
            best.Value = double.NegativeInfinity;
            var candidates = toLag < fromLag ? 0 : (toLag - fromLag) / step + 1;
            if (!forceSequential && candidates >= ParallelLagScanThreshold)
            {
                // Score the whole grid on all cores (each score touches only read-only buffers),
                // then pick the winner with the same fold, in the same lag order, as the
                // sequential loop below — the incumbent logic resolves ties identically, so the
                // result is bit-for-bit the sequential one.
                var scores = new CorrelationScore[candidates];
                Parallel.For(0, candidates, index => scores[index] = ScoreCorrelation(
                    mixture, reference, fromLag + index * step, analysisStart));
                for (var index = 0; index < candidates; index++)
                {
                    best = BetterLagScore(best, scores[index], preferSmallLag);
                }

                return best;
            }

            for (var lag = fromLag; lag <= toLag; lag += step)
            {
                best = BetterLagScore(
                    best, ScoreCorrelation(mixture, reference, lag, analysisStart), preferSmallLag);
            }

            return best;
        }

        /// <summary>The sequential incumbent rule, factored so both scan paths share it exactly.</summary>
        private static CorrelationScore BetterLagScore(
            CorrelationScore best, CorrelationScore score, bool preferSmallLag)
        {
            if (!preferSmallLag)
            {
                return score.Value > best.Value ? score : best;
            }

            if (score.Value > best.Value + PeriodicAmbiguityMargin ||
                (score.Value > best.Value - PeriodicAmbiguityMargin &&
                 Math.Abs(score.LagFrames) < Math.Abs(best.LagFrames)))
            {
                return score;
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
            SampleView mixture,
            SampleView reference,
            int lagFrames,
            int analysisStart)
        {
            return ScoreCorrelationAtLag(mixture, reference, lagFrames, analysisStart);
        }

        /// <summary>
        /// Refines the one slice-wide integer alignment without changing it per block. A QPC stamp
        /// identifies the correct sample neighbourhood, but two independent audio engines can still
        /// present the same waveform between sample centres. Linear interpolation at a fixed
        /// fractional lag removes the quiet phasey copy that nearest-frame subtraction leaves.
        /// </summary>
        private static CorrelationScore RefineFractionalLag(
            SampleView mixture,
            SampleView reference,
            CorrelationScore integerBest,
            int stepsPerFrame)
        {
            var best = ScoreCorrelationAtLag(
                mixture, reference, integerBest.LagFrames, integerBest.AnalysisStart);
            for (var step = 0; step <= stepsPerFrame; step++)
            {
                var offset = -0.5 + step / (double)stepsPerFrame;
                var candidate = ScoreCorrelationAtLag(
                    mixture,
                    reference,
                    integerBest.LagFrames + offset,
                    integerBest.AnalysisStart);
                if (candidate.Count > 0 && candidate.Value > best.Value)
                {
                    best = candidate;
                }
            }

            return best;
        }

        // Internal so the equivalence tests can score the same inputs this scorer and a
        // byte-walking mirror of it see, and assert bit-identical results.
        internal static CorrelationScore ScoreCorrelationAtLag(
            SampleView mixture,
            SampleView reference,
            double lagFrames,
            int analysisStart)
        {
            var mixtureFrames = mixture.Frames;
            var referenceFrames = reference.Frames;
            var referenceEnd = Math.Min(referenceFrames, analysisStart + CorrelationWindowFrames);
            var mixtureStart = Math.Max(0, (int)Math.Ceiling(analysisStart - lagFrames));
            var mixtureEnd = Math.Min(
                mixtureFrames,
                (int)Math.Ceiling(referenceEnd - lagFrames));
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            var mixtureSamples = mixture.Samples;
            var referenceSamples = reference.Samples;
            var wholeLag = Math.Floor(lagFrames);
            if (lagFrames == wholeLag && wholeLag >= int.MinValue && wholeLag <= int.MaxValue)
            {
                // Integer lag — the case every candidate of the wide sweep hits. Reads land on
                // sample centres, so the interpolated read reduces to the sample itself
                // (fraction 0 returns the lower sample exactly) and the direct index below is
                // value-identical to it, including the zero it returns out of range.
                var lag = (int)wholeLag;
                for (var mixtureFrame = mixtureStart;
                     mixtureFrame < mixtureEnd;
                     mixtureFrame += CorrelationStrideFrames)
                {
                    var referenceFrame = mixtureFrame + lag;
                    var inRange = referenceFrame >= 0 && referenceFrame <= referenceFrames - 1;
                    var mixtureIndex = mixtureFrame * Channels;
                    var referenceIndex = referenceFrame * Channels;
                    for (var channel = 0; channel < 2; channel++)
                    {
                        var mixed = mixtureSamples[mixtureIndex + channel];
                        double source = inRange ? referenceSamples[referenceIndex + channel] : 0;
                        dot += mixed * source;
                        mixtureEnergy += mixed * (double)mixed;
                        referenceEnergy += source * source;
                        count++;
                    }
                }
            }
            else
            {
                for (var mixtureFrame = mixtureStart;
                     mixtureFrame < mixtureEnd;
                     mixtureFrame += CorrelationStrideFrames)
                {
                    var mixtureIndex = mixtureFrame * Channels;
                    var referenceFrame = mixtureFrame + lagFrames;
                    for (var channel = 0; channel < 2; channel++)
                    {
                        var mixed = mixtureSamples[mixtureIndex + channel];
                        var source = ReadInterpolatedSample(reference, referenceFrame, channel);
                        dot += mixed * source;
                        mixtureEnergy += mixed * (double)mixed;
                        referenceEnergy += source * source;
                        count++;
                    }
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            var value = denominator > 0 ? dot / denominator : 0;

            // A lag near or beyond the window length leaves only a sliver of overlap, and a
            // handful of samples correlates near-perfectly by chance — observed live as a
            // corr=1.000 two-sample "peak" outscoring the true 0.79 alignment. A window that
            // could not sample at least three quarters of its span scores nothing.
            var windowFrames = Math.Max(1, referenceEnd - analysisStart);
            if (count / 2 * CorrelationStrideFrames < windowFrames * 3L / 4)
            {
                value = 0;
            }

            return new CorrelationScore
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

        /// <summary>Correlation over one mixture block, in mixture-frame coordinates.</summary>
        private static CorrelationScore ScoreBlock(
            byte[] mixture,
            byte[] reference,
            double lagFrames,
            int blockStartFrame,
            int blockEndFrame,
            int scoreOffsetFrames = 0,
            int channel = -1)
        {
            var referenceFrames = reference.Length / BlockAlign;
            double dot = 0;
            double mixtureEnergy = 0;
            double referenceEnergy = 0;
            long count = 0;

            var firstFrame = blockStartFrame + Math.Max(
                0, Math.Min(BlockFitStrideFrames - 1, scoreOffsetFrames));
            for (var frame = firstFrame; frame < blockEndFrame; frame += BlockFitStrideFrames)
            {
                var referenceFrame = frame + lagFrames;
                if (referenceFrame < 0 || referenceFrame > referenceFrames - 1)
                {
                    continue;
                }

                var mixtureByte = frame * BlockAlign;
                var firstChannel = channel >= 0 ? channel : 0;
                var lastChannel = channel >= 0 ? channel : 1;
                for (var currentChannel = firstChannel;
                     currentChannel <= lastChannel;
                    currentChannel++)
                {
                    var mixed = ReadInt16(mixture, mixtureByte + currentChannel * 2);
                    var source = ReadInterpolatedSample(
                        reference, referenceFrame, currentChannel);
                    dot += mixed * source;
                    mixtureEnergy += mixed * (double)mixed;
                    referenceEnergy += source * source;
                    count++;
                }
            }

            var denominator = Math.Sqrt(mixtureEnergy * referenceEnergy);
            return new CorrelationScore
            {
                LagFrames = (int)Math.Round(lagFrames),
                ExactLagFrames = lagFrames,
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

        private static double ReadInterpolatedSample(
            byte[] bytes, double framePosition, int channel)
        {
            var frames = bytes.Length / BlockAlign;
            if (framePosition < 0 || framePosition > frames - 1 || channel < 0 || channel >= Channels)
            {
                return 0;
            }

            var lower = (int)Math.Floor(framePosition);
            var fraction = framePosition - lower;
            var first = (double)ReadInt16(bytes, lower * BlockAlign + channel * 2);
            if (fraction <= 0 || lower + 1 >= frames)
            {
                return first;
            }

            var second = (double)ReadInt16(bytes, (lower + 1) * BlockAlign + channel * 2);
            return first + (second - first) * fraction;
        }

        private static void WriteInt16(byte[] bytes, long offset, short value)
        {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
        }

        /// <summary>
        /// Mirrors the byte-buffer <see cref="ReadInterpolatedSample(byte[], double, int)"/>
        /// exactly — same bounds behaviour, same arithmetic — over a decoded view.
        /// </summary>
        private static double ReadInterpolatedSample(
            SampleView view, double framePosition, int channel)
        {
            var frames = view.Frames;
            if (framePosition < 0 || framePosition > frames - 1 || channel < 0 || channel >= Channels)
            {
                return 0;
            }

            var lower = (int)Math.Floor(framePosition);
            var fraction = framePosition - lower;
            var first = (double)view.Samples[lower * Channels + channel];
            if (fraction <= 0 || lower + 1 >= frames)
            {
                return first;
            }

            var second = (double)view.Samples[(lower + 1) * Channels + channel];
            return first + (second - first) * fraction;
        }

        /// <summary>
        /// A PCM buffer decoded to one short per sample, built once per cancellation pass so the
        /// lag search does not reassemble every 16-bit sample from bytes on each of its thousands
        /// of candidate lags. Values are exactly what <see cref="ReadInt16"/> produces, so scoring
        /// a view is bit-identical to scoring the bytes. Internal for the equivalence tests.
        /// </summary>
        internal struct SampleView
        {
            public short[] Samples;
            public int Frames;

            public static SampleView From(byte[] pcm)
            {
                var frames = pcm.Length / BlockAlign;
                var samples = new short[frames * Channels];
                Buffer.BlockCopy(pcm, 0, samples, 0, frames * BlockAlign);
                return new SampleView { Samples = samples, Frames = frames };
            }
        }

        internal struct CorrelationScore
        {
            public int LagFrames;
            public double ExactLagFrames;
            public int AnalysisStart;
            public double Dot;
            public double ReferenceEnergy;
            public long Count;
            public double Value;
        }

        private struct BlockFit
        {
            public bool HasSignal;
            public bool LeftHasSignal;
            public bool RightHasSignal;
            public double LeftGain;
            public double RightGain;
        }

        private struct ChannelFit
        {
            public bool HasSignal;
            public double Gain;
        }
    }
}
