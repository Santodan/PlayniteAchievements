using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
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
        public static readonly TimeSpan FileWatchSafetyPollInterval = TimeSpan.FromSeconds(1);

        public string ProviderKey { get; set; }

        public IReadOnlyList<string> WatchTargets { get; set; } = Array.Empty<string>();

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

        public bool IsRemote { get; set; }

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
