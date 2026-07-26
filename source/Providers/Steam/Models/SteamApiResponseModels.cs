using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.Steam.Models
{
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

    [DataContract]
    internal sealed class SteamOwnedGame
    {
        [DataMember(Name = "appid")]
        public int AppId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "playtime_forever")]
        public int PlaytimeForever { get; set; }

        [DataMember(Name = "playtime_2weeks")]
        public int? Playtime2Weeks { get; set; }

        [DataMember(Name = "rtime_last_played")]
        public long? LastPlayedUnixSeconds { get; set; }

        // Earned/total achievement counts parsed from the community games page ("11/17").
        // Not part of the Web API payload; feeds FriendGameOwnership.AchievementUnlocksHint so
        // the refresh gate can skip provider-only games the friend has not unlocked anything in.
        public int? AchievementsEarned { get; set; }

        public int? AchievementsTotal { get; set; }
    }

    [DataContract]
    internal sealed class GetOwnedGamesRoot
    {
        [DataMember(Name = "response")]
        public GetOwnedGamesResponse Response { get; set; }
    }

    [DataContract]
    internal sealed class GetOwnedGamesResponse
    {
        [DataMember(Name = "game_count")]
        public int? GameCount { get; set; }

        [DataMember(Name = "games")]
        public List<SteamOwnedGame> Games { get; set; }
    }
}
