using Newtonsoft.Json;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Riot account-v1 response. Resolves a Riot ID (gameName#tagLine) to the PUUID that
    /// every other per-player endpoint is keyed by.
    /// </summary>
    internal sealed class RiotAccountDto
    {
        [JsonProperty("puuid")]
        public string Puuid { get; set; }

        [JsonProperty("gameName")]
        public string GameName { get; set; }

        [JsonProperty("tagLine")]
        public string TagLine { get; set; }
    }

    /// <summary>
    /// challenges-v1 <c>player-data</c> response.
    /// </summary>
    internal sealed class RiotPlayerInfoDto
    {
        [JsonProperty("totalPoints")]
        public RiotChallengePointsDto TotalPoints { get; set; }

        [JsonProperty("categoryPoints")]
        public Dictionary<string, RiotChallengePointsDto> CategoryPoints { get; set; }

        [JsonProperty("challenges")]
        public List<RiotChallengeInfoDto> Challenges { get; set; }
    }

    internal sealed class RiotChallengePointsDto
    {
        [JsonProperty("level")]
        public string Level { get; set; }

        [JsonProperty("current")]
        public double Current { get; set; }

        [JsonProperty("max")]
        public double Max { get; set; }

        [JsonProperty("percentile")]
        public double? Percentile { get; set; }
    }

    /// <summary>
    /// One challenge's state for the queried player. <see cref="AchievedTime"/> is the moment the
    /// player reached <see cref="Level"/> — Riot reports only the current tier's timestamp, not one
    /// per tier.
    /// </summary>
    internal sealed class RiotChallengeInfoDto
    {
        [JsonProperty("challengeId")]
        public long ChallengeId { get; set; }

        [JsonProperty("percentile")]
        public double? Percentile { get; set; }

        [JsonProperty("level")]
        public string Level { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }

        [JsonProperty("achievedTime")]
        public long? AchievedTime { get; set; }
    }

    /// <summary>
    /// CommunityDragon <c>v1/challenges.json</c>. The top level is an object, not an array, and the
    /// challenge map is keyed by id string — entries carry no id field of their own.
    /// </summary>
    internal sealed class CDragonChallengeFile
    {
        [JsonProperty("challenges")]
        public Dictionary<string, CDragonChallenge> Challenges { get; set; }
    }

    internal sealed class CDragonChallenge
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("descriptionShort")]
        public string DescriptionShort { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        /// <summary>
        /// Free-form string map. Known keys: <c>isCapstone</c> ("Y"), <c>isCategory</c> ("true"),
        /// <c>parent</c> (the id of the owning capstone or category).
        /// </summary>
        [JsonProperty("tags")]
        public Dictionary<string, string> Tags { get; set; }

        [JsonProperty("queueIds")]
        public List<int> QueueIds { get; set; }

        /// <summary>Epoch milliseconds after which the challenge can no longer be progressed; 0 when open-ended.</summary>
        [JsonProperty("endTimestamp")]
        public long EndTimestamp { get; set; }

        /// <summary>Level name to a <c>/lol-game-data/assets/...</c> path for that tier's token art.</summary>
        [JsonProperty("levelToIconPath")]
        public Dictionary<string, string> LevelToIconPath { get; set; }

        [JsonProperty("thresholds")]
        public Dictionary<string, CDragonThreshold> Thresholds { get; set; }

        [JsonProperty("leaderboard")]
        public bool Leaderboard { get; set; }

        /// <summary>True when a lower value is better, so tier thresholds descend rather than ascend.</summary>
        [JsonProperty("reverseDirection")]
        public bool ReverseDirection { get; set; }
    }

    internal sealed class CDragonThreshold
    {
        [JsonProperty("value")]
        public double Value { get; set; }
    }

    /// <summary>
    /// Everything a challenge source must produce for the mapper: the player's per-challenge state
    /// plus the level-to-percentile table that gives locked challenges a rarity.
    /// </summary>
    internal sealed class RiotPlayerChallengeState
    {
        /// <summary>Stable identity of the queried player (the PUUID for the web API).</summary>
        public string PlayerKey { get; set; }

        public IReadOnlyList<RiotChallengeInfoDto> Challenges { get; set; }

        /// <summary>challengeId to level name to percentile. Empty when the source cannot supply it.</summary>
        public IReadOnlyDictionary<long, IReadOnlyDictionary<string, double>> LevelPercentiles { get; set; }
    }
}
