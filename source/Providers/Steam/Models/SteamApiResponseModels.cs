using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.Steam.Models
{
    /// <summary>
    /// Shared Steam Web API response models used by both SteamHttpClient and SteamApiClient.
    /// Consolidates duplicate model definitions to reduce code duplication.
    /// </summary>
    [DataContract]
    internal sealed class OwnedGamesEnvelope
    {
        [DataMember(Name = "response")]
        public OwnedGamesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGamesResponse
    {
        [DataMember(Name = "games")]
        public List<OwnedGame> Games { get; set; }
    }

    [DataContract]
    internal sealed class OwnedGame
    {
        [DataMember(Name = "appid")]
        public int? AppId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int? PlaytimeForever { get; set; }

        [DataMember(Name = "rtime_last_played")]
        public long? RTimeLastPlayed { get; set; }

        public string LibrarySourceName { get; set; }
    }

    [DataContract]
    internal sealed class SchemaRoot
    {
        [DataMember(Name = "response")]
        public SchemaResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class SchemaResponse
    {
        [DataMember(Name = "game")]
        public SchemaGame Game { get; set; }
    }

    [DataContract]
    internal sealed class SchemaGame
    {
        [DataMember(Name = "availableGameStats")]
        public SchemaAvailableGameStats AvailableGameStats { get; set; }
    }

    [DataContract]
    internal sealed class SchemaAvailableGameStats
    {
        [DataMember(Name = "achievements")]
        public SchemaAchievement[] Achievements { get; set; }
    }

    [DataContract]
    internal sealed class SchemaAchievement
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "icon")]
        public string Icon { get; set; }

        [DataMember(Name = "icongray")]
        public string IconGray { get; set; }

        [DataMember(Name = "hidden")]
        public int Hidden { get; set; }

        [DataMember(Name = "globalPercent")]
        public double? GlobalPercent { get; set; }
    }

    [DataContract]
    internal sealed class PlayerSummariesRoot
    {
        [DataMember(Name = "response")]
        public PlayerSummariesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class PlayerSummariesResponse
    {
        [DataMember(Name = "players")]
        public List<PlayerSummaryDto> Players { get; set; }
    }

    [DataContract]
    internal sealed class PlayerSummaryDto
    {
        [DataMember(Name = "steamid")]
        public string SteamId { get; set; }

        [DataMember(Name = "personaname")]
        public string PersonaName { get; set; }

        [DataMember(Name = "avatar")]
        public string Avatar { get; set; }

        [DataMember(Name = "avatarmedium")]
        public string AvatarMedium { get; set; }

        [DataMember(Name = "avatarfull")]
        public string AvatarFull { get; set; }
    }

    [DataContract]
    internal sealed class GetGameAchievementsRoot
    {
        [DataMember(Name = "response")]
        public GetGameAchievementsResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetGameAchievementsResponse
    {
        [DataMember(Name = "achievements")]
        public List<Achievement> Achievements { get; set; }
    }

    [DataContract]
    internal sealed class Achievement
    {
        [DataMember(Name = "internal_name")]
        public string InternalName { get; set; }

        [DataMember(Name = "localized_name")]
        public string LocalizedName { get; set; }

        [DataMember(Name = "localized_desc")]
        public string LocalizedDesc { get; set; }

        [DataMember(Name = "icon")]
        public string Icon { get; set; }

        [DataMember(Name = "icon_gray")]
        public string IconGray { get; set; }

        [DataMember(Name = "hidden")]
        public bool Hidden { get; set; }

        [DataMember(Name = "player_percent_unlocked")]
        public string PlayerPercentUnlocked { get; set; }
    }

    internal class SchemaAndPercentages
    {
        public List<SchemaAchievement> Achievements { get; set; }
        public Dictionary<string, double> GlobalPercentages { get; set; }
    }
}
