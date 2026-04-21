using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.Steam.Models
{
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

        [DataMember(Name = "type")]
        public string AppType { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int? PlaytimeForever { get; set; }

        [DataMember(Name = "rtime_last_played")]
        public long? RTimeLastPlayed { get; set; }

        public string LibrarySourceName { get; set; }
    }

    [DataContract]
    internal sealed class SteamAsyncConfigResponse
    {
        [DataMember(Name = "success")]
        public int? Success { get; set; }

        [DataMember(Name = "data")]
        public SteamAsyncConfigData Data { get; set; }
    }

    [DataContract]
    internal sealed class SteamAsyncConfigData
    {
        [DataMember(Name = "webapi_token")]
        public string WebApiToken { get; set; }
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
