using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PlayniteAchievements.Providers.BattleNet.Models
{
    [DataContract]
    public sealed class BattleNetApiTokenResponse
    {
        [DataMember(Name = "access_token")]
        public string AccessToken { get; set; }

        [DataMember(Name = "expires_in")]
        public int ExpiresIn { get; set; }
    }

    [DataContract]
    public sealed class Sc2ProfileResponse
    {
        [DataMember(Name = "summary")]
        public Sc2Summary Summary { get; set; }

        [DataMember(Name = "earnedAchievements")]
        public List<Sc2EarnedAchievement> EarnedAchievements { get; set; }

        [DataMember(Name = "categoryPointProgress")]
        public List<Sc2CategoryPointProgress> CategoryPointProgress { get; set; }
    }

    [DataContract]
    public sealed class Sc2Summary
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "totalAchievementPoints")]
        public int TotalAchievementPoints { get; set; }
    }

    [DataContract]
    public sealed class Sc2EarnedAchievement
    {
        [DataMember(Name = "achievementId")]
        public string AchievementId { get; set; }

        [DataMember(Name = "completionDate")]
        public string CompletionDate { get; set; }

        [DataMember(Name = "isComplete")]
        public bool IsComplete { get; set; }

        [DataMember(Name = "inProgress")]
        public bool InProgress { get; set; }
    }

    [DataContract]
    public sealed class Sc2CategoryPointProgress
    {
        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "pointsEarned")]
        public int PointsEarned { get; set; }
    }

    [DataContract]
    public sealed class Sc2AchievementDefinitionsResponse
    {
        [DataMember(Name = "achievements")]
        public List<Sc2AchievementDefinition> Achievements { get; set; }

        [DataMember(Name = "categories")]
        public List<Sc2Category> Categories { get; set; }
    }

    [DataContract]
    public sealed class Sc2AchievementDefinition
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "imageUrl")]
        public string ImageUrl { get; set; }

        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "points")]
        public int Points { get; set; }
    }

    [DataContract]
    public sealed class Sc2Category
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "parentCategoryId")]
        public string ParentCategoryId { get; set; }
    }
}
