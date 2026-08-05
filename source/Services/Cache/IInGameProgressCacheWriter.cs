using PlayniteAchievements.Providers;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Cache
{
    /// <summary>
    /// Optional cache fast path used during a running game. It updates user progress against
    /// definitions that already exist and never mutates the achievement schema.
    /// </summary>
    internal interface IInGameProgressCacheWriter
    {
        InGameProgressWriteResult ApplyInGameProgress(
            string cacheKey,
            string providerKey,
            IReadOnlyList<AchievementProgressObservation> observations);
    }

    internal sealed class InGameProgressWriteResult
    {
        public bool Success { get; set; }

        public bool Changed { get; set; }

        public string ErrorCode { get; set; }

        public IReadOnlyList<string> MatchedKeys { get; set; } = new List<string>();

        public IReadOnlyList<string> UnmatchedKeys { get; set; } = new List<string>();

        public IReadOnlyList<string> NewlyUnlockedKeys { get; set; } = new List<string>();

        public static InGameProgressWriteResult Failed(string errorCode)
        {
            return new InGameProgressWriteResult
            {
                Success = false,
                ErrorCode = errorCode
            };
        }
    }
}
