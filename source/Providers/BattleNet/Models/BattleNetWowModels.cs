using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.BattleNet.Models
{
    [DataContract]
    public sealed class WowRegionResult
    {
        [DataMember(Name = "data")]
        public WowData Data { get; set; }
    }

    [DataContract]
    public sealed class WowData
    {
        [DataMember(Name = "Realms")]
        public List<WowRealm> Realms { get; set; }
    }

    [DataContract]
    public sealed class WowRealm
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }
    }

    [DataContract]
    public sealed class WowAchievementsData
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "category")]
        public string Category { get; set; }

        [DataMember(Name = "achievementsList")]
        public List<WowAchievement> AchievementsList { get; set; }

        [DataMember(Name = "subcategories")]
        public object Subcategories { get; set; }
    }

    [DataContract]
    public sealed class WowSubcategory
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "achievements")]
        public List<WowAchievement> Achievements { get; set; }
    }

    [DataContract]
    public sealed class WowAchievement
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "icon")]
        public WowIcon Icon { get; set; }

        [DataMember(Name = "point")]
        public int Point { get; set; }

        [DataMember(Name = "time")]
        public DateTime? Time { get; set; }

        [DataMember(Name = "accountWide")]
        public bool AccountWide { get; set; }
    }

    [DataContract]
    public sealed class WowIcon
    {
        [DataMember(Name = "url")]
        public string Url { get; set; }
    }
}
