using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Describes which timestamp best represents the gameplay moment for an unlock observed by an
    /// in-game progress source. ProviderReported is appropriate when the provider exposes an
    /// authoritative event time. SourceObservation is for persisted-state sources whose embedded
    /// timestamp is not guaranteed to share the recorder's local clock/correlation point. Steam's
    /// persisted achievement epoch versus the local StoreStats file change is the canonical example.
    ///
    /// A registration that states nothing is resolved from its mechanism by
    /// <see cref="InGameUnlockAnchorSelector.ResolvePolicy"/>, so a remote source cannot silently
    /// inherit the provider-reported default.
    /// </summary>
    internal enum InGameUnlockAnchorPolicy
    {
        /// <summary>
        /// Unstated: resolved from the registration's mechanism rather than declared. This is the
        /// default so that "never considered" stays distinguishable from a deliberate
        /// <see cref="ProviderReported"/>.
        /// </summary>
        Auto = 0,
        ProviderReported = 1,
        SourceObservation = 2
    }

    internal static class InGameUnlockAnchorSelector
    {
        /// <summary>
        /// Resolves the effective anchor policy for a registration. An explicitly declared policy
        /// always wins; an unstated one is derived from the registration's mechanism. A remote
        /// source's unlock stamp is produced by the provider's server clock, which shares no
        /// correlation point with the capture timeline, so it can only anchor on the local
        /// observation.
        ///
        /// <see cref="InGameProgressRegistration.IsRemote"/> means "read over the network", which
        /// is not literally "foreign clock", but the two coincide for every source here and both
        /// ways they could diverge are safe: a remote source reporting a client-produced stamp is
        /// conservatively observation-anchored, costing one poll interval of precision, and the one
        /// local source holding a foreign stamp -- Steam's persisted epoch -- declares
        /// <see cref="InGameUnlockAnchorPolicy.SourceObservation"/> explicitly.
        /// </summary>
        public static InGameUnlockAnchorPolicy ResolvePolicy(InGameProgressRegistration registration)
        {
            var declared = registration?.UnlockAnchorPolicy ?? InGameUnlockAnchorPolicy.Auto;
            if (declared != InGameUnlockAnchorPolicy.Auto)
            {
                return declared;
            }

            return registration?.IsRemote == true
                ? InGameUnlockAnchorPolicy.SourceObservation
                : InGameUnlockAnchorPolicy.ProviderReported;
        }

        public static (DateTime? Utc, UnlockVideoAnchorSource Source) Select(
            InGameUnlockAnchorPolicy policy,
            DateTime? providerReportedUtc,
            DateTime observedUtc,
            TimeSpan reportedBias = default(TimeSpan))
        {
            var useObservation = policy == InGameUnlockAnchorPolicy.SourceObservation ||
                !providerReportedUtc.HasValue;
            if (useObservation)
            {
                // Discarding a stamp we cannot place on our own clock is distinct from never
                // having had one. Both anchor on observation, but only the first says the
                // provider's reported time is unusable for seeking the capture buffer.
                var source = providerReportedUtc.HasValue
                    ? UnlockVideoAnchorSource.SourceObservationForeignStamp
                    : UnlockVideoAnchorSource.SourceObservation;
                return (observedUtc, source);
            }

            // The bias compensates a provider whose reported stamp systematically precedes the
            // on-screen moment. It can never push the anchor past the observation itself: the
            // unlock was already visible by then.
            var anchor = providerReportedUtc.Value + reportedBias;
            if (reportedBias > TimeSpan.Zero && anchor > observedUtc)
            {
                anchor = observedUtc;
            }

            return (anchor, UnlockVideoAnchorSource.ProviderReported);
        }
    }

    /// <summary>
    /// Optional provider capability for reading only live user progress. Implementations must
    /// reuse the supplied cached schema and must not perform definition, icon, rarity, or other
    /// metadata work.
    /// </summary>
    internal interface IInGameProgressSource
    {
        InGameProgressRegistration TryRegister(Game game, GameAchievementData cachedSchema);

        Task<IReadOnlyList<InGameProgressQueryResult>> QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken);
    }

    internal sealed class InGameProgressRegistration
    {
        /// <summary>
        /// Safety re-read cadence for a local file-watched source. The FileSystemWatcher is the primary
        /// detection signal; this backstop re-reads the watched file directly on this cadence so a change
        /// event that is delayed or coalesced by the OS/sync engine (for example on a OneDrive-synced
        /// folder) cannot stall detection. Every local file source should use this rather than a longer
        /// interval; remote (polled) sources set their own <see cref="PollInterval"/> instead.
        /// </summary>
        public static readonly TimeSpan FileWatchSafetyPollInterval = TimeSpan.FromMilliseconds(500);

        public string ProviderKey { get; set; }

        public IReadOnlyList<string> WatchTargets { get; set; } = Array.Empty<string>();

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

        public bool IsRemote { get; set; }

        public InGameUnlockAnchorPolicy UnlockAnchorPolicy { get; set; } =
            InGameUnlockAnchorPolicy.Auto;

        /// <summary>
        /// Correction added to the provider-reported timestamp when it anchors video capture, for
        /// a provider whose stamp systematically precedes the on-screen moment (second-truncated
        /// timestamps, or a stamp taken at the game's unlock call rather than its visible payoff).
        /// Ignored under <see cref="InGameUnlockAnchorPolicy.SourceObservation"/>.
        /// </summary>
        public TimeSpan UnlockAnchorBias { get; set; }

        /// <summary>
        /// Opaque provider-owned state resolved once at game start (for example a title id or
        /// exact progress-file mapping). It prevents repeated schema/path discovery on every read.
        /// </summary>
        public object State { get; set; }
    }

    internal sealed class InGameTrackingContext
    {
        public Game Game { get; set; }

        public GameAchievementData CachedSchema { get; set; }

        public InGameProgressRegistration Registration { get; set; }

        public DateTime SessionStartUtc { get; set; }
    }

    internal sealed class AchievementProgressObservation
    {
        public string ApiName { get; set; }

        public bool Unlocked { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        /// <summary>
        /// Optional derived unlock-mode token. Currently used by RetroAchievements for
        /// Softcore/Hardcore without replacing Base/Subset classification.
        /// </summary>
        public string UnlockMode { get; set; }
    }

    internal sealed class InGameProgressQueryResult
    {
        public Guid GameId { get; set; }

        public bool Success { get; set; }

        /// <summary>
        /// True for an event/feed delta; false for a complete positive progress snapshot.
        /// Cache application is monotonic in either case.
        /// </summary>
        public bool IsDelta { get; set; }

        public IReadOnlyList<AchievementProgressObservation> Achievements { get; set; } =
            Array.Empty<AchievementProgressObservation>();

        public string FailureReason { get; set; }

        public static InGameProgressQueryResult Failed(Guid gameId, string reason)
        {
            return new InGameProgressQueryResult
            {
                GameId = gameId,
                Success = false,
                FailureReason = reason
            };
        }

        public static InGameProgressQueryResult Succeeded(
            Guid gameId,
            IReadOnlyList<AchievementProgressObservation> achievements,
            bool isDelta = false)
        {
            return new InGameProgressQueryResult
            {
                GameId = gameId,
                Success = true,
                IsDelta = isDelta,
                Achievements = achievements ?? Array.Empty<AchievementProgressObservation>()
            };
        }
    }
}
