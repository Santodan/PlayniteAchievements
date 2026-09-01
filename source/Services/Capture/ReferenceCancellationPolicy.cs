namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// The one parameter set the clip pipeline cancels a known reference out of captured audio
    /// with — the non-game slice, and the live chime.
    /// <para>
    /// Extracted from the export service so it can be measured: it depends on nothing but
    /// <see cref="PcmAudio"/>, so <c>tools/capture-harness/ChimeRoundTripProbe</c> compiles it in
    /// and exercises the real thresholds. Tuning these by reasoning about the audio rather than
    /// running that probe has produced two wrong answers in a row.
    /// </para>
    /// </summary>
    internal static class ReferenceCancellationPolicy
    {
        public static PcmCancellationOutcome Subtract(
            byte[] mixture,
            byte[] reference,
            out PcmCancellationDiagnostics diagnostics,
            bool residualPass,
            int? blockFrames = null,
            int maxLagFrames = 12000,
            bool detectClean = false,
            double? calibratedLagFrames = null)
        {
            var floor = residualPass ? 0.001 : 0.005;
            return PcmAudio.CancelCorrelated(
                mixture,
                reference,
                out diagnostics,
                muteUnverifiedBlocks: false,
                maxLagFrames: maxLagFrames,
                minimumGain: floor,
                maximumGain: 20,
                blockGainFloor: floor,
                keepBlockSuppressionDb: 10,
                cancellationBlockFrames: blockFrames ?? mixture.Length / PcmAudio.BlockAlign,
                // The residual ceiling exists to catch a reference that is not this signal. A
                // chime-file caller's reference is definitionally this signal, and the ceiling was
                // observed discarding a verified 20+ dB removal because the leftovers of an
                // earlier pass still correlated with the file.
                maximumResidualCorrelation: detectClean ? double.MaxValue : 0.35,
                commitVerifiedBlocksOnWeakPass: true,
                minimumCorrelation: residualPass ? 0.03 : 0.15,
                // A chime-file caller wants "the reference does not project" reported as
                // CleanNoGameDetected — proof of absence — rather than attempted anyway.
                attemptVerifiedBlocksWhenGloballyClean: !detectClean,
                verificationLagRadiusFrames: 128,
                independentChannelGains: true,
                gainCrossfadeFrames: 0,
                fractionalLagSteps: 32,
                calibratedLagFrames: calibratedLagFrames);
        }
    }
}
