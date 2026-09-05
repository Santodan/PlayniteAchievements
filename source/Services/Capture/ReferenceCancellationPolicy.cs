namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The one parameter set the clip pipeline cancels a known reference out of captured audio
    /// with: the session's simultaneously captured reference track (everything outside the game
    /// tree, or the sound host's unlock sound).
    /// <para>
    /// Extracted from the export service so it can be measured: it depends on nothing but
    /// <see cref="PcmAudio"/>, so <c>tools/capture-harness/ChimeBurstProbe</c> compiles it in
    /// and exercises the real thresholds. Tuning these by reasoning about the audio rather than
    /// running that probe has produced two wrong answers in a row.
    /// </para>
    /// </summary>
    internal static class ReferenceCancellationPolicy
    {
        /// <summary>Half-second blocks at 48 kHz: the granularity the time-local subtraction uses.</summary>
        public const int IsolationBlockFrames = 24000;
        public const int LocalCaptureLagFrames = 12000;

        /// <summary>
        /// How far a time-local block may re-lock its lag from the slice-wide calibration: 10 ms,
        /// well above the sub-millisecond recorder tears seen in the field and well below the
        /// tens of milliseconds at which a tonal sound's partials start to repeat. Used for the
        /// sound-host reference, where a tear inside the chime is exactly the failure the field
        /// showed (tails surviving at another lag); the Game Only reference keeps one lag.
        /// </summary>
        public const int BlockRelockRadiusFrames = 480;

        public static PcmCancellationOutcome Subtract(
            byte[] mixture,
            byte[] reference,
            out PcmCancellationDiagnostics diagnostics,
            bool residualPass,
            int? blockFrames = null,
            int maxLagFrames = 12000,
            double? calibratedLagFrames = null,
            int blockLagRadiusFrames = 0)
        {
            var floor = residualPass ? 0.001 : 0.005;
            return PcmAudio.CancelCorrelated(
                mixture,
                reference,
                out diagnostics,
                maxLagFrames: maxLagFrames,
                minimumGain: floor,
                maximumGain: 20,
                blockGainFloor: floor,
                keepBlockSuppressionDb: 10,
                cancellationBlockFrames: blockFrames ?? mixture.Length / PcmAudio.BlockAlign,
                // The residual ceiling catches a reference that is not this signal.
                maximumResidualCorrelation: 0.35,
                commitVerifiedBlocksOnWeakPass: true,
                minimumCorrelation: residualPass ? 0.03 : 0.15,
                attemptVerifiedBlocksWhenGloballyClean: true,
                verificationLagRadiusFrames: 128,
                independentChannelGains: true,
                gainCrossfadeFrames: 0,
                fractionalLagSteps: 32,
                calibratedLagFrames: calibratedLagFrames,
                blockLagRadiusFrames: blockLagRadiusFrames);
        }

        /// <summary>
        /// Removes a game-tree capture from a process-tree reference without ever replacing an
        /// uncertain span with silence. The caller receives a changed reference only after the
        /// entire active game copy is absent or every committed block has passed held-out proof.
        /// A failed final proof leaves <paramref name="reference"/> byte-for-byte unchanged.
        /// </summary>
        public static PcmCancellationOutcome RemoveGameFromReference(
            byte[] reference,
            byte[] gameReference,
            out PcmCancellationDiagnostics diagnostics,
            int maxLagFrames = LocalCaptureLagFrames,
            double? calibratedLagFrames = null)
        {
            diagnostics = default(PcmCancellationDiagnostics);
            if (reference == null || gameReference == null ||
                reference.Length < PcmAudio.BlockAlign || gameReference.Length < PcmAudio.BlockAlign)
            {
                return PcmCancellationOutcome.Unseparable;
            }

            var working = (byte[])reference.Clone();
            var changed = false;
            for (var pass = 0; pass < 3; pass++)
            {
                var outcome = PcmAudio.CancelCorrelated(
                    working,
                    gameReference,
                    out diagnostics,
                    maxLagFrames: maxLagFrames,
                    commitVerifiedBlocksOnWeakPass: true,
                    preferEarlyAlignmentWindow: true,
                    verificationLagRadiusFrames: 480,
                    calibratedLagFrames: pass == 0 ? calibratedLagFrames : null);

                if (outcome == PcmCancellationOutcome.CleanNoGameDetected)
                {
                    // A silent/missed game capture cannot prove the other process reference is
                    // game-free. Treating zero-filled gam_ as success allowed a Playnite-tree or
                    // non-game reference that actually contained game audio to remove that game
                    // from the endpoint mix. Real silence safely falls back to recorded audio.
                    if (!diagnostics.ReferenceHasSignal)
                    {
                        return PcmCancellationOutcome.Unseparable;
                    }

                    // Clean is authoritative only before any partial edit. After a pass changed
                    // some blocks without proving the whole active reference absent, a weak
                    // residual may simply fall under the global presence gate. Discard the entire
                    // transaction rather than treating that ambiguity as game-free audio.
                    return changed
                        ? PcmCancellationOutcome.Unseparable
                        : outcome;
                }

                if (outcome != PcmCancellationOutcome.CancelledVerified)
                {
                    return PcmCancellationOutcome.Unseparable;
                }

                changed = true;
                if (IsComplete(diagnostics) && diagnostics.ResidualCorrelation < 0.20)
                {
                    System.Buffer.BlockCopy(working, 0, reference, 0, reference.Length);
                    return PcmCancellationOutcome.CancelledVerified;
                }
            }

            // Three independently verified partial passes without a clean residual are not proof
            // that the reference is game-free. Discard them instead of feeding an uncertain
            // reference into a later subtraction.
            return PcmCancellationOutcome.Unseparable;
        }

        public static bool IsComplete(PcmCancellationDiagnostics diagnostics)
        {
            return diagnostics.SubtractedBlocks > 0 &&
                !diagnostics.PartialCommit &&
                diagnostics.RestoredBlocks == 0;
        }
    }
}
