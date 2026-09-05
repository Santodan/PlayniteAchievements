using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>One known UPS source placed on a bounded cleanup window's PCM timeline.</summary>
    internal sealed class ChimeRemovalSource
    {
        public ChimeRemovalSource(
            Guid occurrenceId,
            long startByte,
            long endByte,
            byte[] sourcePcm)
        {
            OccurrenceId = occurrenceId;
            StartByte = Math.Max(0, startByte) & ~(long)(PcmAudio.BlockAlign - 1);
            EndByte = Math.Max(StartByte, endByte) & ~(long)(PcmAudio.BlockAlign - 1);
            SourcePcm = sourcePcm;
        }

        public Guid OccurrenceId { get; }

        public long StartByte { get; }

        public long EndByte { get; }

        /// <summary>Decoded pristine sound at the volume UPS used, or null when unresolved.</summary>
        public byte[] SourcePcm { get; }
    }

    internal sealed class ChimeRemovalAttempt
    {
        public Guid? OccurrenceId { get; set; }

        public string ReferenceKind { get; set; }

        public PcmCancellationOutcome Outcome { get; set; }

        public PcmCancellationDiagnostics Diagnostics { get; set; }

        public bool Verified { get; set; }
    }

    /// <summary>Transactional result for one maximal overlapping occurrence cluster.</summary>
    internal sealed class ChimeRemovalResult
    {
        private ChimeRemovalResult()
        {
        }

        public bool Verified { get; private set; }

        public bool Changed { get; private set; }

        public string FailureReason { get; private set; }

        public byte[] CleanedPcm { get; private set; }

        /// <summary>
        /// A game-free chime reference suitable for purging the same live sounds from the Game
        /// Only non-game sidecar before that sidecar is subtracted from the endpoint mix.
        /// </summary>
        public byte[] ChimeReferencePcm { get; private set; }

        public IReadOnlyList<ChimeRemovalAttempt> Attempts { get; private set; }

        public static ChimeRemovalResult Success(
            byte[] cleanedPcm,
            byte[] referencePcm,
            bool changed,
            IReadOnlyList<ChimeRemovalAttempt> attempts)
        {
            return new ChimeRemovalResult
            {
                Verified = true,
                Changed = changed,
                CleanedPcm = cleanedPcm,
                ChimeReferencePcm = referencePcm,
                Attempts = attempts ?? new ChimeRemovalAttempt[0],
            };
        }

        public static ChimeRemovalResult Failure(
            string reason,
            IReadOnlyList<ChimeRemovalAttempt> attempts)
        {
            return new ChimeRemovalResult
            {
                Verified = false,
                Changed = false,
                FailureReason = reason,
                Attempts = attempts ?? new ChimeRemovalAttempt[0],
            };
        }
    }

    /// <summary>
    /// Removes every known UPS playback from one short cluster. It uses the resolved source files
    /// first because those contain no game audio, then uses a game-purged Playnite-tree capture as
    /// a fallback/residual reference. All work happens on a clone and is returned only when every
    /// occurrence has a verified path; failure can therefore never mute or damage the clip.
    /// </summary>
    internal static class ChimeRemovalEngine
    {
        private const double MinimumAcceptedSuppressionDb = 30.0;

        /// <summary>
        /// Gate on the held-out weakest audible block. PcmAudio scores it honestly now (standard
        /// blocks even for a whole-window fit, masked and edge blocks excluded), and real
        /// renders of a real jingle reach 24-27 dB in their weakest block while removing the rest
        /// by 40-50 dB (field run 2026-09-05, third session, where a 30 dB gate here rejected two
        /// of five waves and left every clip with all of its live sounds and no replacement). A
        /// remnant 20 dB under its own block is far below a retained live sound.
        /// </summary>
        private const double MinimumAcceptedWeakestBlockDb = 20.0;
        private const int MaximumCopiesPerSource = 8;

        public static ChimeRemovalResult RemoveAll(
            byte[] endpointPcm,
            byte[] rawPlayniteTreePcm,
            byte[] gameReferencePcm,
            IReadOnlyList<ChimeRemovalSource> sources)
        {
            var attempts = new List<ChimeRemovalAttempt>();
            if (endpointPcm == null || endpointPcm.Length < PcmAudio.BlockAlign)
            {
                return ChimeRemovalResult.Failure("endpoint audio is unavailable", attempts);
            }

            sources = sources ?? new ChimeRemovalSource[0];
            if (sources.Count == 0)
            {
                return ChimeRemovalResult.Failure("the cluster has no sound occurrences", attempts);
            }

            var working = (byte[])endpointPcm.Clone();
            var aggregateFileReference = new byte[endpointPcm.Length];
            var missingFileReferences = 0;
            var fileFailures = 0;
            var uncorroboratedFileAbsences = 0;
            var changed = false;
            var removedFileReferences = new List<byte[]>();

            foreach (var source in sources.OrderBy(s => s.StartByte))
            {
                var reference = BuildPlacedReference(endpointPcm.Length, source);
                if (reference == null)
                {
                    missingFileReferences++;
                    continue;
                }

                PcmAudio.MixInto(
                    aggregateFileReference,
                    0,
                    reference,
                    0,
                    reference.Length);
                var run = RemoveKnownReference(
                    working,
                    reference,
                    source.OccurrenceId,
                    "resolved-file",
                    ReferenceCancellationPolicy.FilePlaybackLagFrames,
                    attempts);
                changed |= run.Changed;
                var coveredByIdenticalRemoval = removedFileReferences.Any(
                    removed => removed.SequenceEqual(reference));
                if (!run.Verified)
                {
                    // Two simultaneous waves may use the exact same file and placement. The first
                    // least-squares fit sees their summed gain and removes both; the second then
                    // sees only unrelated audio, whose aggressive absence probe deliberately
                    // fails closed. Exact reference identity plus independent proof that this
                    // source existed in the original makes that case unambiguous.
                    if (!coveredByIdenticalRemoval ||
                        !IsVerifiedPresent(
                            endpointPcm,
                            reference,
                            ReferenceCancellationPolicy.FilePlaybackLagFrames))
                    {
                        fileFailures++;
                    }
                }
                else if (!run.Changed)
                {
                    // "This file does not project onto the endpoint" is not enough by itself:
                    // an effects/enhancement path may have transformed a still-audible live
                    // chime. It is safe without the captured reference only when an earlier file
                    // cancellation changed the overlap and this exact source was independently
                    // present in the original endpoint (for example two identical simultaneous
                    // occurrences removed by one fitted gain).
                    var coveredByPriorRemoval = coveredByIdenticalRemoval &&
                        IsVerifiedPresent(endpointPcm, reference, maxLagFrames:
                            ReferenceCancellationPolicy.FilePlaybackLagFrames);
                    if (!coveredByPriorRemoval)
                    {
                        uncorroboratedFileAbsences++;
                    }
                }
                else
                {
                    removedFileReferences.Add(reference);
                }
            }

            byte[] capturedReference = null;
            if (rawPlayniteTreePcm != null && gameReferencePcm != null)
            {
                capturedReference = FitLength(rawPlayniteTreePcm, endpointPcm.Length);
                var gameReference = FitLength(gameReferencePcm, endpointPcm.Length);
                var isolationOutcome = ReferenceCancellationPolicy.RemoveGameFromReference(
                    capturedReference,
                    gameReference,
                    out var isolation,
                    ReferenceCancellationPolicy.LocalCaptureLagFrames);
                var isolationVerified =
                    isolationOutcome == PcmCancellationOutcome.CleanNoGameDetected ||
                    isolationOutcome == PcmCancellationOutcome.CancelledVerified;
                attempts.Add(new ChimeRemovalAttempt
                {
                    ReferenceKind = "captured-game-purge",
                    Outcome = isolationOutcome,
                    Diagnostics = isolation,
                    Verified = isolationVerified,
                });
                if (!isolationVerified)
                {
                    capturedReference = null;
                }
            }

            // The captured render is the authoritative fallback for an unresolved source and the
            // residual mop-up for resampling/player differences. It is never mandatory when every
            // pristine source independently proved absent: field hardware can make the game purge
            // unavailable even though the file reference removed the endpoint chime by 40+ dB.
            var capturedVerified = false;
            if (capturedReference != null)
            {
                var capturedRun = RemoveKnownReference(
                    working,
                    capturedReference,
                    null,
                    "captured-playnite",
                    ReferenceCancellationPolicy.LocalCaptureLagFrames,
                    attempts);
                capturedVerified = capturedRun.Verified;
                changed |= capturedRun.Changed;
            }

            if (missingFileReferences > 0 && !capturedVerified)
            {
                return ChimeRemovalResult.Failure(
                    $"{missingFileReferences} occurrence(s) had no resolved source and the captured reference was not verified",
                    attempts);
            }

            if (fileFailures > 0 && !capturedVerified)
            {
                return ChimeRemovalResult.Failure(
                    $"{fileFailures} resolved source cancellation(s) were not verified",
                    attempts);
            }

            if (uncorroboratedFileAbsences > 0 && !capturedVerified)
            {
                return ChimeRemovalResult.Failure(
                    $"{uncorroboratedFileAbsences} file reference(s) only proved a weak absence and the captured reference was not verified",
                    attempts);
            }

            var referenceForPurge = capturedVerified
                ? capturedReference
                : HasSignal(aggregateFileReference) ? aggregateFileReference : null;
            if (referenceForPurge == null)
            {
                return ChimeRemovalResult.Failure("no verified chime reference remained", attempts);
            }

            return ChimeRemovalResult.Success(working, referenceForPurge, changed, attempts);
        }

        private static RemovalRun RemoveKnownReference(
            byte[] working,
            byte[] reference,
            Guid? occurrenceId,
            string kind,
            int maxLagFrames,
            List<ChimeRemovalAttempt> attempts)
        {
            // One UPS launch can reach the endpoint more than once (for example through two render
            // paths). Peel distinct, independently proven lags until an ordinary post-removal scan
            // reports no further copy. Each peel is transactional, so ambiguity cannot undo or
            // contaminate an earlier 30 dB-proven improvement.
            var transaction = (byte[])working.Clone();
            var changed = false;
            for (var copy = 0; copy < MaximumCopiesPerSource; copy++)
            {
                var copyKind = copy == 0 ? kind : kind + $"-copy-{copy + 1}";
                var run = RemoveOneKnownReference(
                    transaction,
                    reference,
                    occurrenceId,
                    copyKind,
                    maxLagFrames,
                    attempts,
                    requireResidualAbsenceProof: !changed);
                if (!run.Verified)
                {
                    if (changed)
                    {
                        // Every attempted fit is itself transactional. An ambiguous later search
                        // cannot invalidate copies already removed with 30 dB held-out proof; keep
                        // those improvements instead of rolling the original live sound back in.
                        Buffer.BlockCopy(transaction, 0, working, 0, working.Length);
                        return new RemovalRun(true, true);
                    }

                    return new RemovalRun(false, false);
                }

                if (!run.Changed)
                {
                    if (changed)
                    {
                        Buffer.BlockCopy(transaction, 0, working, 0, working.Length);
                    }

                    return new RemovalRun(true, changed);
                }

                changed = true;
            }

            // Every committed copy independently passed the strong output gate. Keep those bounded
            // improvements; the cap prevents a pathological periodic source from looping forever.
            Buffer.BlockCopy(transaction, 0, working, 0, working.Length);
            return new RemovalRun(true, true);
        }

        private static RemovalRun RemoveOneKnownReference(
            byte[] working,
            byte[] reference,
            Guid? occurrenceId,
            string kind,
            int maxLagFrames,
            List<ChimeRemovalAttempt> attempts,
            bool requireResidualAbsenceProof)
        {
            var sawRejectedCancellation = false;
            var sawDeferredAbsence = false;
            var isResolvedFile = kind.StartsWith(
                "resolved-file",
                StringComparison.Ordinal);
            for (var pass = 0; pass < 3; pass++)
            {
                var residualPass = pass == 2;
                var timeLocalPass = pass == 1;
                // Every fit is its own transaction. PcmAudio proves its internal block edits, but
                // this engine imposes a stronger whole-occurrence 30 dB gate; a fit that misses
                // that gate must not become the starting point for another pass.
                var candidate = (byte[])working.Clone();
                var outcome = ReferenceCancellationPolicy.Subtract(
                    candidate,
                    reference,
                    out var diagnostics,
                    residualPass: residualPass,
                    // A pristine file is one coherent render and gets the most accurate gain from
                    // its complete active span. The time-local pass is the fallback for a capture
                    // whose level changed during playback. The low-floor residual pass must stay
                    // whole-window: fitting silent 500 ms blocks at residual floors can model
                    // unrelated noise and manufacture an inverse chime.
                    blockFrames: timeLocalPass
                        ? ReferenceCancellationPolicy.ChimeBlockFrames
                        : (int?)null,
                    maxLagFrames: maxLagFrames,
                    detectClean: true,
                    // The file is a unique source timeline, so take its strongest peak. The
                    // near-zero ambiguity bias is for periodic controller references; applying it
                    // to a broadband chime can move the fit several frames off the true maximum.
                    preferSmallLagOnWideSearch: !isResolvedFile,
                    // The ordinary clean shortcut has deliberately loose ceilings. On the final
                    // low-floor pass, make it try the fitted copy so held-out suppression—not the
                    // shortcut—decides whether a quiet live chime is actually present.
                    attemptVerifiedBlocksWhenGloballyClean: residualPass,
                    // A recorder alignment tear inside the sound moves everything after it to a
                    // slightly different lag. The time-local pass lets each block re-lock.
                    blockLagRadiusFrames: timeLocalPass
                        ? ReferenceCancellationPolicy.BlockRelockRadiusFrames
                        : 0);
                var verified = IsVerifiedAbsent(outcome, diagnostics);
                AddAttempt(
                    attempts,
                    occurrenceId,
                    pass == 0 ? kind : kind + (timeLocalPass ? "-local" : "-residual"),
                    outcome,
                    diagnostics,
                    verified);

                if (verified)
                {
                    if (outcome == PcmCancellationOutcome.CleanNoGameDetected)
                    {
                        // An ordinary-floor absence can hide a quiet live copy — the field failure
                        // that originally produced a second chime. Require the low-floor detector
                        // to agree before absence may authorize a replacement. Once a concrete
                        // cancellation failed the stronger output proof, even that later "clean"
                        // is contradictory rather than evidence that the sound vanished.
                        if ((!requireResidualAbsenceProof || residualPass) &&
                            !sawRejectedCancellation)
                        {
                            return new RemovalRun(true, false);
                        }

                        sawDeferredAbsence = true;
                        continue;
                    }

                    Buffer.BlockCopy(candidate, 0, working, 0, working.Length);
                    return new RemovalRun(true, true);
                }

                if (residualPass && sawDeferredAbsence && !sawRejectedCancellation &&
                    outcome == PcmCancellationOutcome.Unseparable &&
                    diagnostics.TotalBlocks == 0 && diagnostics.RestoredBlocks == 0 &&
                    Math.Abs(diagnostics.Correlation) < 0.15)
                {
                    // The ordinary passes found the sound absent and the low-floor pass, which is
                    // made to try a fit anyway, found nothing that even reached its floors: no
                    // block was attempted, none restored, correlation under the ordinary floor.
                    // That is the detector agreeing, not a contradiction; after Game Only
                    // isolation removed a sound, every clean window reports this shape (field
                    // run 2026-09-05, third session). A pass that attempted a block and had to
                    // restore it is the ambiguous case and still fails closed.
                    return new RemovalRun(true, false);
                }

                if (outcome == PcmCancellationOutcome.CancelledVerified)
                {
                    sawRejectedCancellation = true;

                    // Correlation chooses the most likely neighbourhood, but one sample of game
                    // noise can make an adjacent lag score microscopically higher while leaving a
                    // derivative-shaped audible residue. Verify the exact neighbours against the
                    // strong output gate and commit only a genuinely clean one. This confines
                    // timestamp influence to five samples around measured audio evidence.
                    var measuredLagFrames =
                        diagnostics.StartLagMs * PcmAudio.SampleRate / 1000.0;
                    var center = (int)Math.Round(measuredLagFrames);
                    for (var delta = -2; delta <= 2; delta++)
                    {
                        var exactLag = center + delta;
                        var neighbour = (byte[])working.Clone();
                        var neighbourOutcome = ReferenceCancellationPolicy.Subtract(
                            neighbour,
                            reference,
                            out var neighbourDiagnostics,
                            residualPass: residualPass,
                            blockFrames: timeLocalPass
                                ? ReferenceCancellationPolicy.ChimeBlockFrames
                                : (int?)null,
                            maxLagFrames: maxLagFrames,
                            detectClean: true,
                            calibratedLagFrames: exactLag,
                            preferSmallLagOnWideSearch: !isResolvedFile,
                            attemptVerifiedBlocksWhenGloballyClean: residualPass,
                            blockLagRadiusFrames: timeLocalPass
                                ? ReferenceCancellationPolicy.BlockRelockRadiusFrames
                                : 0);
                        var neighbourVerified =
                            IsVerifiedAbsent(neighbourOutcome, neighbourDiagnostics) &&
                            neighbourOutcome == PcmCancellationOutcome.CancelledVerified;
                        AddAttempt(
                            attempts,
                            occurrenceId,
                            $"{kind}-exact-{exactLag:+0;-0;0}",
                            neighbourOutcome,
                            neighbourDiagnostics,
                            neighbourVerified);
                        if (neighbourVerified)
                        {
                            Buffer.BlockCopy(neighbour, 0, working, 0, working.Length);
                            return new RemovalRun(true, true);
                        }
                    }
                }
            }

            return new RemovalRun(false, false);
        }

        private static void AddAttempt(
            List<ChimeRemovalAttempt> attempts,
            Guid? occurrenceId,
            string kind,
            PcmCancellationOutcome outcome,
            PcmCancellationDiagnostics diagnostics,
            bool verified)
        {
            attempts.Add(new ChimeRemovalAttempt
            {
                OccurrenceId = occurrenceId,
                ReferenceKind = kind,
                Outcome = outcome,
                Diagnostics = diagnostics,
                Verified = verified,
            });
        }

        private static bool IsVerifiedAbsent(
            PcmCancellationOutcome outcome,
            PcmCancellationDiagnostics diagnostics)
        {
            if (outcome == PcmCancellationOutcome.CleanNoGameDetected)
            {
                return diagnostics.ReferenceHasSignal;
            }

            // Suppression is the projection of the reference onto the audio before and after the
            // fit, so it measures how much reference-shaped signal remains relative to what the
            // live sound contributed. ResidualCorrelation is a normalized score over one window:
            // a remnant 40 dB down still correlates strongly with the reference when the game is
            // quiet at that moment, so it cannot serve as a ceiling here. It stays in the
            // diagnostics for the field log. Field run 2026-09-05: two of six waves with
            // correlation 0.999-1.000 and 31-41 dB suppression were rejected on residual 0.199 and
            // 0.524, leaving every live sound in fourteen clips.
            return outcome == PcmCancellationOutcome.CancelledVerified &&
                ReferenceCancellationPolicy.IsComplete(diagnostics) &&
                diagnostics.SuppressionDb >= MinimumAcceptedSuppressionDb &&
                diagnostics.WeakestBlockSuppressionDb >= MinimumAcceptedWeakestBlockDb;
        }

        private static bool IsVerifiedPresent(
            byte[] original,
            byte[] reference,
            int maxLagFrames)
        {
            var probe = (byte[])original.Clone();
            var run = RemoveKnownReference(
                probe,
                reference,
                null,
                "presence-probe",
                maxLagFrames,
                new List<ChimeRemovalAttempt>());
            return run.Verified && run.Changed;
        }

        private static byte[] BuildPlacedReference(int windowLength, ChimeRemovalSource source)
        {
            if (source?.SourcePcm == null || source.SourcePcm.Length < PcmAudio.BlockAlign ||
                source.StartByte >= windowLength || source.EndByte <= source.StartByte)
            {
                return null;
            }

            var reference = new byte[windowLength];
            var count = Math.Min(
                source.SourcePcm.LongLength,
                Math.Min(source.EndByte - source.StartByte, windowLength - source.StartByte));
            PcmAudio.MixInto(reference, source.StartByte, source.SourcePcm, 0, count);
            return HasSignal(reference) ? reference : null;
        }

        private static byte[] FitLength(byte[] pcm, int length)
        {
            var result = new byte[length];
            if (pcm != null)
            {
                Buffer.BlockCopy(pcm, 0, result, 0, Math.Min(pcm.Length, result.Length));
            }

            return result;
        }

        private static bool HasSignal(byte[] pcm)
        {
            if (pcm == null)
            {
                return false;
            }

            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                if (pcm[i] != 0 || pcm[i + 1] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private struct RemovalRun
        {
            public RemovalRun(bool verified, bool changed)
            {
                Verified = verified;
                Changed = changed;
            }

            public bool Verified;
            public bool Changed;
        }
    }
}
