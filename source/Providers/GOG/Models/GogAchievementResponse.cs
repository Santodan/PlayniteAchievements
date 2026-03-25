using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Playnite.SDK.Data;

namespace PlayniteAchievements.Providers.GOG.Models
{
    /// <summary>
    /// Response from GOG achievements API.
    /// API: https://gameplay.gog.com/clients/{client_id}/users/{user_id}/achievements
    /// </summary>
    [DataContract]
    public sealed class GogAchievementResponse
    {
        // CommonPluginsStores.Gog.Models.Achievements
        [DataMember(Name = "total_count")]
        public int TotalCount { get; set; }

        [DataMember(Name = "limit")]
        public int Limit { get; set; }

        [DataMember(Name = "page_token")]
        public string PageToken { get; set; }

        [DataMember(Name = "items")]
        public List<GogAchievementItem> Items { get; set; }

        [DataMember(Name = "achievements_mode")]
        public string AchievementsMode { get; set; }
    }

    [DataContract]
    public sealed class GogAchievementItem
    {
        // CommonPluginsStores.Gog.Models.AchItem
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "achievement_id")]
        public string AchievementId { get; set; }

        [DataMember(Name = "achievement_key")]
        public string AchievementKey { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "image_url_unlocked")]
        public string ImageUrlUnlocked { get; set; }

        [DataMember(Name = "imageUrlUnlocked")]
        public string ImageUrlUnlocked2 { get; set; }

        [DataMember(Name = "image_url_locked")]
        public string ImageUrlLocked { get; set; }

        [DataMember(Name = "imageUrlLocked")]
        public string ImageUrlLocked2 { get; set; }

        [DataMember(Name = "rarity")]
        public double? Rarity { get; set; }

        [DataMember(Name = "date_unlocked")]
        public DateTime? DateUnlocked { get; set; }

        [DataMember(Name = "rarity_level_description")]
        public string RarityLevelDescription { get; set; }

        [DataMember(Name = "rarity_level_slug")]
        public string RarityLevelSlug { get; set; }

        [DataMember(Name = "visible")]
        public bool Visible { get; set; }

        [IgnoreDataMember]
        public string ResolvedAchievementId =>
            FirstNonEmpty(AchievementKey, AchievementId, Id);

        [IgnoreDataMember]
        public string ResolvedTitle
        {
            get
            {
                var trimmedName = Name?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedName) && !LooksLikeNumericId(trimmedName))
                {
                    return trimmedName;
                }

                var trimmedKey = AchievementKey?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedKey) && !LooksLikeNumericId(trimmedKey))
                {
                    return trimmedKey;
                }

                return FirstNonEmpty(trimmedName, ResolvedAchievementId);
            }
        }

        [IgnoreDataMember]
        public string ResolvedDescription => Description;

        [IgnoreDataMember]
        public string ResolvedImageUrlUnlocked =>
            FirstNonEmpty(ImageUrlUnlocked, ImageUrlUnlocked2);

        [IgnoreDataMember]
        public string ResolvedImageUrlLocked =>
            FirstNonEmpty(ImageUrlLocked, ImageUrlLocked2, ResolvedImageUrlUnlocked);

        [IgnoreDataMember]
        public bool ResolvedVisible => Visible;

        [IgnoreDataMember]
        public double? ResolvedRarityPercent => ClampPercent(Rarity);

        /// <summary>
        /// date_unlocked is null for locked achievements.
        /// </summary>
        [IgnoreDataMember]
        public DateTime? UnlockTimeUtc
        {
            get
            {
                if (!DateUnlocked.HasValue || DateUnlocked.Value == default)
                    return null;

                var value = DateUnlocked.Value;
                if (value.Kind == DateTimeKind.Local)
                    return value.ToUniversalTime();
                if (value.Kind == DateTimeKind.Unspecified)
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
                return value;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static double? ClampPercent(double? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            var rawValue = value.Value;
            if (double.IsNaN(rawValue) || double.IsInfinity(rawValue))
            {
                return null;
            }

            if (rawValue < 0)
            {
                return 0;
            }

            if (rawValue > 100)
            {
                return 100;
            }

            return rawValue;
        }

        private static bool LooksLikeNumericId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
