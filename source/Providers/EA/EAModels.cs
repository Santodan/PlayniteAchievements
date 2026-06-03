using Newtonsoft.Json;
using System;

namespace PlayniteAchievements.Providers.EA
{
    public sealed class EaOwnedGame
    {
        public string OriginOfferId { get; set; }
        public string GameSlug { get; set; }
        public string ProductName { get; set; }
    }

    public sealed class EaAchievementItem
    {
        public string AchievementId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public bool IsUnlocked { get; set; }
    }

    internal sealed class EaAchievement
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("awardCount")]
        public int AwardCount { get; set; }

        [JsonProperty("date")]
        public DateTime Date { get; set; }
    }
}
