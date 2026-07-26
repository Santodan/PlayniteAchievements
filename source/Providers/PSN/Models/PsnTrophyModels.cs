using Newtonsoft.Json;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.PSN.Models
{
    internal sealed class PsnTrophyTitleLookup
    {
        [JsonProperty("titles")]
        public List<PsnTitleContainer> Titles { get; set; }
    }

    internal sealed class PsnTitleContainer
    {
        [JsonProperty("trophyTitles")]
        public List<PsnTrophyTitleEntry> TrophyTitles { get; set; }
    }

    internal sealed class PsnTrophyTitleEntry
    {
        [JsonProperty("npCommunicationId")]
        public string NpCommunicationId { get; set; }
    }

    /// <summary>
    /// Response from /users/me/trophyTitles - lists all games user has earned trophies in.
    /// </summary>
    internal sealed class PsnAllTrophyTitlesResponse
    {
        [JsonProperty("trophyTitles")]
        public List<PsnTrophyTitle> TrophyTitles { get; set; }
    }

    /// <summary>
    /// Individual trophy title entry from user's trophy list.
    /// </summary>
    internal sealed class PsnTrophyTitle
    {
        [JsonProperty("npCommunicationId")]
        public string NpCommunicationId { get; set; }

        [JsonProperty("trophyTitleName")]
        public string TrophyTitleName { get; set; }
    }

    internal sealed class PsnTrophiesUserResponse
    {
        [JsonProperty("trophies")]
        public List<PsnUserTrophy> Trophies { get; set; }
    }

    internal sealed class PsnUserTrophy
    {
        [JsonProperty("trophyGroupId")]
        public string TrophyGroupId { get; set; }

        [JsonProperty("trophyId")]
        public int TrophyId { get; set; }

        [JsonProperty("trophyEarnedRate")]
        public double? TrophyEarnedRate { get; set; }

        [JsonProperty("earned")]
        public bool Earned { get; set; }

        [JsonProperty("earnedDateTime")]
        public string EarnedDateTime { get; set; }
    }

    internal sealed class PsnTrophiesDetailResponse
    {
        [JsonProperty("trophies")]
        public List<PsnTrophyDetail> Trophies { get; set; }
    }

    /// <summary>
    /// Response from /npCommunicationIds/{id}/trophyGroups - the trophy group metadata
    /// (base group plus each DLC group) carrying human-readable group titles.
    /// </summary>
    internal sealed class PsnTrophyGroupsResponse
    {
        [JsonProperty("trophyGroups")]
        public List<PsnTrophyGroup> TrophyGroups { get; set; }
    }

    internal sealed class PsnTrophyGroup
    {
        [JsonProperty("trophyGroupId")]
        public string TrophyGroupId { get; set; }

        [JsonProperty("trophyGroupName")]
        public string TrophyGroupName { get; set; }
    }

    internal sealed class PsnTrophyDetail
    {
        [JsonProperty("trophyGroupId")]
        public string TrophyGroupId { get; set; }

        [JsonProperty("trophyId")]
        public int TrophyId { get; set; }

        [JsonProperty("trophyName")]
        public string TrophyName { get; set; }

        [JsonProperty("trophyDetail")]
        public string TrophyDetail { get; set; }

        [JsonProperty("trophyType")]
        public string TrophyType { get; set; }

        [JsonProperty("trophyIconUrl")]
        public string TrophyIconUrl { get; set; }

        [JsonProperty("hidden")]
        public bool Hidden { get; set; }
    }
}
