namespace PlayniteAchievements.Services.Recording
{
    /// <summary>Which process scope the clip track recorded; decides whether a clip may carry a composited chime.</summary>
    internal enum ClipTrackKind
    {
        /// <summary>The default render endpoint's whole mix. The live unlock sound is in it.</summary>
        EndpointMix,

        /// <summary>Every process except the sound host's tree, at 4 channels. The unlock sound is never in it.</summary>
        ExcludeSoundHost,

        /// <summary>The game's process tree only, at 4 channels (Game Only). The unlock sound is never in it.</summary>
        IncludeGame,
    }

    /// <summary>Why a clip does or does not receive the one composited copy of its wave's unlock sound.</summary>
    internal enum ChimeCompositeVerdict
    {
        /// <summary>The session recorded no audio, so there is no live sound to double.</summary>
        NoRecordedAudio,

        /// <summary>The clip track never held the live sound, so the composited copy is the only chime.</summary>
        HostExcluded,

        /// <summary>The clip track is the endpoint mix: the live sound is in it at its live time.</summary>
        HostNotExcluded,

        /// <summary>
        /// The exclusion keyed off a sound-host pid that has since changed; sounds after the change
        /// were played by a process the running capture does not exclude.
        /// </summary>
        HostChanged,
    }

    /// <summary>
    /// The composite decision, in one place and free of I/O. Exactly one chime per clip: the
    /// composited copy when the clip track structurally excluded the sound host, the live one
    /// otherwise. A doubt about which of the two the clip holds resolves to no composite, because
    /// two chimes are worse than the live one at the wrong time.
    /// </summary>
    internal static class ChimeCompositeDecision
    {
        /// <param name="audioRecorded">Whether the session recorded any audio track.</param>
        /// <param name="clipTrack">What the session's clip track recorded.</param>
        /// <param name="usedFallbackTrack">
        /// Game Only only: the clip's audio came from the exclude-host fallback because the game
        /// tree carried no signal over the window.
        /// </param>
        /// <param name="excludedHostProcessId">The host pid the exclusion keyed off at session start.</param>
        /// <param name="currentHostProcessId">The host pid at export, or null when the host is down.</param>
        public static ChimeCompositeVerdict Decide(
            bool audioRecorded,
            ClipTrackKind clipTrack,
            bool usedFallbackTrack,
            int? excludedHostProcessId,
            int? currentHostProcessId)
        {
            if (!audioRecorded)
            {
                return ChimeCompositeVerdict.NoRecordedAudio;
            }

            switch (clipTrack)
            {
                case ClipTrackKind.IncludeGame when !usedFallbackTrack:
                    // The host is never inside the game's tree, whatever its pid did since.
                    return ChimeCompositeVerdict.HostExcluded;

                case ClipTrackKind.IncludeGame:
                case ClipTrackKind.ExcludeSoundHost:
                    return excludedHostProcessId.HasValue && currentHostProcessId == excludedHostProcessId
                        ? ChimeCompositeVerdict.HostExcluded
                        : ChimeCompositeVerdict.HostChanged;

                default:
                    return ChimeCompositeVerdict.HostNotExcluded;
            }
        }

        public static bool AllowsComposite(ChimeCompositeVerdict verdict)
        {
            return verdict == ChimeCompositeVerdict.NoRecordedAudio ||
                verdict == ChimeCompositeVerdict.HostExcluded;
        }
    }
}
