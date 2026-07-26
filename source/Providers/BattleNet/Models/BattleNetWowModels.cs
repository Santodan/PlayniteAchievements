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

    [DataContract]
    public sealed class WowAccountProfileResponse
    {
        [DataMember(Name = "wow_accounts")]
        public List<WowAccountProfileAccount> WowAccounts { get; set; }
    }

    [DataContract]
    public sealed class WowAccountProfileAccount
    {
        [DataMember(Name = "id")]
        public long Id { get; set; }

        [DataMember(Name = "characters")]
        public List<WowAccountProfileCharacterEntry> Characters { get; set; }
    }

    [DataContract]
    public sealed class WowAccountProfileCharacterEntry
    {
        [DataMember(Name = "character")]
        public WowAccountProfileCharacter Character { get; set; }

        [DataMember(Name = "protected_character")]
        public WowApiKey ProtectedCharacter { get; set; }
    }

    [DataContract]
    public sealed class WowAccountProfileCharacter
    {
        [DataMember(Name = "id")]
        public long Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "level")]
        public int Level { get; set; }

        [DataMember(Name = "realm")]
        public WowAccountProfileRealm Realm { get; set; }
    }

    [DataContract]
    public sealed class WowAccountProfileRealm
    {
        [DataMember(Name = "id")]
        public long Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }
    }

    [DataContract]
    public sealed class WowCharacterAchievementsResponse
    {
        [DataMember(Name = "achievements")]
        public List<WowOfficialAchievementProgress> Achievements { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementIndexResponse
    {
        [DataMember(Name = "achievements")]
        public List<WowOfficialAchievementReference> Achievements { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementProgress
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "achievement")]
        public WowOfficialAchievementReference Achievement { get; set; }

        [DataMember(Name = "criteria")]
        public WowOfficialAchievementCriteria Criteria { get; set; }

        [DataMember(Name = "completed_timestamp")]
        public long? CompletedTimestamp { get; set; }

        public int AchievementId => Id > 0 ? Id : Achievement?.Id ?? 0;
    }

    [DataContract]
    public sealed class WowOfficialAchievementReference
    {
        [DataMember(Name = "key")]
        public WowApiKey Key { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "points")]
        public int? Points { get; set; }

        [DataMember(Name = "is_hidden")]
        public bool IsHidden { get; set; }

        [DataMember(Name = "is_obtainable")]
        public bool? IsObtainable { get; set; }

        [DataMember(Name = "is_obtainable_in_game")]
        public bool? IsObtainableInGame { get; set; }

        [DataMember(Name = "category")]
        public WowOfficialAchievementCategory Category { get; set; }

        [DataMember(Name = "media")]
        public WowApiKey Media { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementCriteria
    {
        [DataMember(Name = "id")]
        public long Id { get; set; }

        [DataMember(Name = "is_completed")]
        public bool IsCompleted { get; set; }

        [DataMember(Name = "child_criteria")]
        public List<WowOfficialAchievementCriteria> ChildCriteria { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementDefinition
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "points")]
        public int Points { get; set; }

        [DataMember(Name = "is_account_wide")]
        public bool IsAccountWide { get; set; }

        [DataMember(Name = "is_hidden")]
        public bool IsHidden { get; set; }

        [DataMember(Name = "is_obtainable")]
        public bool? IsObtainable { get; set; }

        [DataMember(Name = "is_obtainable_in_game")]
        public bool? IsObtainableInGame { get; set; }

        [DataMember(Name = "category")]
        public WowOfficialAchievementCategory Category { get; set; }

        [DataMember(Name = "media")]
        public WowApiKey Media { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementCategory
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    [DataContract]
    public sealed class WowAchievementCategoryReference
    {
        [DataMember(Name = "key")]
        public WowApiKey Key { get; set; }

        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    [DataContract]
    public sealed class WowAchievementCategoryIndexResponse
    {
        [DataMember(Name = "guild_categories")]
        public List<WowAchievementCategoryReference> GuildCategories { get; set; }
    }

    [DataContract]
    public sealed class WowAchievementCategoryResource
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "subcategories")]
        public List<WowAchievementCategoryReference> Subcategories { get; set; }
    }

    [DataContract]
    public sealed class WowOfficialAchievementMediaResponse
    {
        [DataMember(Name = "assets")]
        public List<WowOfficialAchievementMediaAsset> Assets { get; set; }

        public string GetIconUrl()
        {
            if (Assets == null)
            {
                return null;
            }

            foreach (var asset in Assets)
            {
                if (asset != null && string.Equals(asset.Key, "icon", StringComparison.OrdinalIgnoreCase))
                {
                    return asset.Value;
                }
            }

            return Assets.Count > 0 ? Assets[0]?.Value : null;
        }
    }

    [DataContract]
    public sealed class WowOfficialAchievementMediaAsset
    {
        [DataMember(Name = "key")]
        public string Key { get; set; }

        [DataMember(Name = "value")]
        public string Value { get; set; }
    }

    [DataContract]
    public sealed class WowApiKey
    {
        [DataMember(Name = "href")]
        public string Href { get; set; }
    }

    [DataContract]
    public sealed class DataForAzerothDynamicIndex
    {
        [DataMember(Name = "achievementsrarity")]
        public string AchievementsRarity { get; set; }
    }

    [DataContract]
    public sealed class DataForAzerothAchievementRarityResponse
    {
        [DataMember(Name = "achievements")]
        public Dictionary<string, double> Achievements { get; set; }
    }
}
