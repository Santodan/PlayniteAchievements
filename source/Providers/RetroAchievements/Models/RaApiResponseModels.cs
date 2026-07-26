using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.RetroAchievements.Models
{
    internal sealed class RaHashIndexCacheFile
    {
        [JsonProperty("FormatVersion")]
        public int FormatVersion { get; set; }

        [JsonProperty("UpdatedUtc")]
        public DateTime UpdatedUtc { get; set; }

        [JsonProperty("HashToGameId")]
        public Dictionary<string, int> HashToGameId { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        [JsonProperty("BaseGameToSubsets")]
        public Dictionary<int, List<RaSubsetEntry>> BaseGameToSubsets { get; set; } = new Dictionary<int, List<RaSubsetEntry>>();

        [JsonProperty("BaseGameTitles")]
        public Dictionary<int, string> BaseGameTitles { get; set; } = new Dictionary<int, string>();
    }

    internal sealed class RaSubsetEntry
    {
        [JsonProperty("Id")]
        public int Id { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }
    }

    internal sealed class RaSubsetBaseMapping
    {
        public int BaseGameId { get; set; }

        public string BaseGameTitle { get; set; }

        public RaSubsetEntry Subset { get; set; }
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

        [JsonProperty("GameID")]
        public int AlternateGameId { get; set; }

        [JsonProperty("Title")]
        public string GameTitle { get; set; }

        [JsonProperty("ConsoleID")]
        public int ConsoleId { get; set; }

        [JsonProperty("ConsoleName")]
        public string ConsoleName { get; set; }

        [JsonProperty("ParentGameID")]
        public int? ParentGameId { get; set; }

        [JsonProperty("ImageIcon")]
        public string ImageIcon { get; set; }

        [JsonProperty("ImageTitle")]
        public string ImageTitle { get; set; }

        [JsonProperty("ImageIngame")]
        public string ImageIngame { get; set; }

        [JsonProperty("ImageBoxArt")]
        public string ImageBoxArt { get; set; }

        [JsonProperty("NumDistinctPlayers")]
        public int NumDistinctPlayers { get; set; }

        [JsonProperty("NumDistinctPlayersCasual")]
        public int NumDistinctPlayersCasual { get; set; }

        [JsonProperty("NumDistinctPlayersHardcore")]
        public int NumDistinctPlayersHardcore { get; set; }

        [JsonProperty("Achievements")]
        public Dictionary<string, RaAchievement> Achievements { get; set; }
    }

    internal sealed class RaUsersIFollowResponse
    {
        [JsonProperty("Count")]
        public int Count { get; set; }

        [JsonProperty("Total")]
        public int Total { get; set; }

        [JsonProperty("Results")]
        public List<RaFollowedUser> Results { get; set; }
    }

    internal sealed class RaFollowedUser
    {
        [JsonProperty("User")]
        public string User { get; set; }

        [JsonProperty("ULID")]
        public string ULID { get; set; }

        [JsonProperty("Points")]
        public int Points { get; set; }

        [JsonProperty("PointsSoftcore")]
        public int PointsSoftcore { get; set; }

        [JsonProperty("IsFollowingMe")]
        public bool IsFollowingMe { get; set; }
    }

    internal sealed class RaUserCompletionProgressResponse
    {
        [JsonProperty("Count")]
        public int Count { get; set; }

        [JsonProperty("Total")]
        public int Total { get; set; }

        [JsonProperty("Results")]
        public List<RaUserCompletionProgressItem> Results { get; set; }
    }

    internal sealed class RaUserCompletionProgressItem
    {
        [JsonProperty("GameID")]
        public int GameID { get; set; }

        [JsonProperty("Title")]
        public string Title { get; set; }

        [JsonProperty("ConsoleID")]
        public int ConsoleID { get; set; }

        [JsonProperty("ConsoleName")]
        public string ConsoleName { get; set; }

        [JsonProperty("ImageIcon")]
        public string ImageIcon { get; set; }

        [JsonProperty("ImageTitle")]
        public string ImageTitle { get; set; }

        [JsonProperty("ImageIngame")]
        public string ImageIngame { get; set; }

        [JsonProperty("ImageBoxArt")]
        public string ImageBoxArt { get; set; }

        [JsonProperty("MaxPossible")]
        public int MaxPossible { get; set; }

        [JsonProperty("NumAwarded")]
        public int NumAwarded { get; set; }

        [JsonProperty("NumAwardedHardcore")]
        public int NumAwardedHardcore { get; set; }

        [JsonProperty("MostRecentAwardedDate")]
        public string MostRecentAwardedDate { get; set; }

        [JsonProperty("HighestAwardKind")]
        public string HighestAwardKind { get; set; }

        [JsonProperty("HighestAwardDate")]
        public string HighestAwardDate { get; set; }
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
