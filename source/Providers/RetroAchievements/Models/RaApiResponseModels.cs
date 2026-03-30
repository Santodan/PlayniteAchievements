using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.RetroAchievements.Models
{
    internal sealed class RaHashIndexCacheFile
    {
        [JsonProperty("UpdatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonProperty("HashToGameId")]
        public Dictionary<string, int> HashToGameId { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        [JsonProperty("BaseGameToSubsets")]
        public Dictionary<int, List<RaSubsetEntry>> BaseGameToSubsets { get; set; } = new Dictionary<int, List<RaSubsetEntry>>();
    }

    internal sealed class RaSubsetEntry
    {
        [JsonProperty("Id")]
        public int Id { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }
    }

    internal sealed class RaGameListResponse
    {
        [JsonProperty("Results")]
        public List<RaGameListItem> Results { get; set; }

        [JsonProperty("GameList")]
        public List<RaGameListItem> GameList { get; set; }
    }

    internal sealed class RaGameListItem
    {
        [JsonProperty("ID")]
        public int ID { get; set; }

        [JsonProperty("GameID")]
        public int GameID { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }

        [JsonProperty("Hashes")]
        public object Hashes { get; set; }
    }

    internal sealed class RaGameListWithTitle
    {
        [JsonProperty("ID")]
        public int ID { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }
    }

    internal sealed class RaGameListWithTitleResponse
    {
        [JsonProperty("Results")]
        public List<RaGameListWithTitle> Results { get; set; }

        [JsonProperty("GameList")]
        public List<RaGameListWithTitle> GameList { get; set; }
    }

    internal sealed class RaGameInfoUserProgress
    {
        [JsonProperty("ID")]
        public int GameId { get; set; }

        [JsonProperty("Title")]
        public string GameTitle { get; set; }

        [JsonProperty("NumDistinctPlayers")]
        public int NumDistinctPlayers { get; set; }

        [JsonProperty("NumDistinctPlayersCasual")]
        public int NumDistinctPlayersCasual { get; set; }

        [JsonProperty("NumDistinctPlayersHardcore")]
        public int NumDistinctPlayersHardcore { get; set; }

        [JsonProperty("Achievements")]
        public Dictionary<string, RaAchievement> Achievements { get; set; }
    }

    internal sealed class RaAchievement
    {
        [JsonProperty("ID")]
        public int ID { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }

        [JsonProperty("Description")]
        public string Description { get; set; }

        [JsonProperty("BadgeName")]
        public string BadgeName { get; set; }

        [JsonProperty("DateEarnedHardcore")]
        public string DateEarnedHardcore { get; set; }

        [JsonProperty("DateEarned")]
        public string DateEarned { get; set; }

        [JsonProperty("NumAwarded")]
        public int NumAwarded { get; set; }

        [JsonProperty("NumAwardedHardcore")]
        public int NumAwardedHardcore { get; set; }

        [JsonProperty("Points")]
        public int Points { get; set; }

        [JsonProperty("TrueRatio")]
        public int TrueRatio { get; set; }

        [JsonProperty("Type")]
        public string Type { get; set; }

        [JsonProperty("DisplayOrder")]
        public int DisplayOrder { get; set; }

        [JsonProperty("Author")]
        public string Author { get; set; }

        [JsonProperty("AuthorULID")]
        public string AuthorULID { get; set; }

        [JsonProperty("DateCreated")]
        public string DateCreated { get; set; }

        [JsonProperty("DateModified")]
        public string DateModified { get; set; }

        [JsonProperty("MemAddr")]
        public string MemAddr { get; set; }
    }
}
