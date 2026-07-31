using System.Collections.Generic;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.GameJolt
{
    // DTOs for GameJolt's internal website JSON API (gamejolt.com/site-api/web/...).
    // Every response is wrapped in a top-level "payload" object.

    internal sealed class GameJoltProfileResponse
    {
        [JsonProperty("payload")]
        public GameJoltProfilePayload Payload { get; set; }
    }

    internal sealed class GameJoltProfilePayload
    {
        [JsonProperty("user")]
        public GameJoltUser User { get; set; }
    }

    internal sealed class GameJoltUser
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("img_avatar")]
        public string ImgAvatar { get; set; }
    }

    // GET /web/discover/games/trophies/{gameId} -> the game's trophy (achievement) definitions.
    internal sealed class GameJoltTrophiesResponse
    {
        [JsonProperty("payload")]
        public GameJoltTrophiesPayload Payload { get; set; }
    }

    internal sealed class GameJoltTrophiesPayload
    {
        [JsonProperty("trophies")]
        public List<GameJoltTrophyDefinition> Trophies { get; set; }

        // The current user's COMPLETE achieved list for this game (populated when the request carries the
        // user's session cookies). This is what the website's trophy page uses to mark unlocks, so it is
        // authoritative and unpaginated - unlike /web/profile/trophies/game which returns only a subset.
        [JsonProperty("trophiesAchieved")]
        public List<GameJoltAchievedRecord> TrophiesAchieved { get; set; }
    }

    internal sealed class GameJoltAchievedRecord
    {
        [JsonProperty("game_id")]
        public long GameId { get; set; }

        // Matches GameJoltTrophyDefinition.Id.
        [JsonProperty("game_trophy_id")]
        public long GameTrophyId { get; set; }

        // Unix epoch in MILLISECONDS.
        [JsonProperty("logged_on")]
        public long? LoggedOn { get; set; }
    }

    internal sealed class GameJoltTrophyDefinition
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("game_id")]
        public long GameId { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        // Trophy difficulty tier: 1=Bronze, 2=Silver, 3=Gold, 4=Platinum.
        [JsonProperty("difficulty")]
        public int Difficulty { get; set; }

        [JsonProperty("experience")]
        public int Experience { get; set; }

        [JsonProperty("secret")]
        public bool Secret { get; set; }

        [JsonProperty("img_thumbnail")]
        public string ImgThumbnail { get; set; }
    }

    // One entry of the in-page percentage batch result ({ "i": trophyId, "p": percentage }). The batch
    // reads /web/profile/trophies/game-trophy-percentage/{trophyId} (the value the website shows on
    // trophy open) for every trophy at once.
    internal sealed class GameJoltPercentageBatchEntry
    {
        [JsonProperty("i")]
        public long Id { get; set; }

        [JsonProperty("p")]
        public double? Percentage { get; set; }
    }
}
