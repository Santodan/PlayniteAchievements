using System;
using System.Collections.Generic;
using System.Net;

namespace PlayniteAchievements.Providers.Steam.Models
{
    public enum SteamScrapeDetail
    {
        None = 0,
        NoSteamSession,
        TooManyRequests,
        CookiesBadAfterRefresh,
        RedirectOffStats,
        ProfilePrivate,
        RequiresLoginForStats,
        ProfileNotFound,
        NoAchievements,
        NoRowsUnknown,
        Scraped,
        Unavailable,
        AllHidden,
        UnlockedMarkerButParseFailed,
        RowsMarkerButParseFailed
    }

    internal static class SteamScrapeDetailInfo
    {
        public static string ToLegacyString(SteamScrapeDetail detail)
        {
            switch (detail)
            {
                case SteamScrapeDetail.NoSteamSession:
                    return "no_steam_session";
                case SteamScrapeDetail.TooManyRequests:
                    return "429";
                case SteamScrapeDetail.CookiesBadAfterRefresh:
                    return "cookies_bad_after_refresh";
                case SteamScrapeDetail.RedirectOffStats:
                    return "redirect_off_stats";
                case SteamScrapeDetail.ProfilePrivate:
                    return "profile_private";
                case SteamScrapeDetail.RequiresLoginForStats:
                    return "requires_login_for_stats";
                case SteamScrapeDetail.ProfileNotFound:
                    return "profile_not_found";
                case SteamScrapeDetail.NoAchievements:
                    return "no_achievements";
                case SteamScrapeDetail.NoRowsUnknown:
                    return "no_rows_unknown";
                case SteamScrapeDetail.Scraped:
                    return "scraped";
                case SteamScrapeDetail.Unavailable:
                    return "unavailable";
                case SteamScrapeDetail.AllHidden:
                    return "all_hidden";
                case SteamScrapeDetail.UnlockedMarkerButParseFailed:
                    return "unlocked_marker_but_parse_failed";
                case SteamScrapeDetail.RowsMarkerButParseFailed:
                    return "rows_marker_but_parse_failed";
                default:
                    return null;
            }
        }
    }
    public class SteamPageResult
    {
        public string RequestedUrl { get; set; }
        public string FinalUrl { get; set; }
        public HttpStatusCode StatusCode { get; set; }
        public string Html { get; set; }
        public bool WasRedirected { get; set; }
    }

    /// <summary>
    /// Represents a scraped achievement row from Steam's HTML.
    /// Extracted from SteamClient.cs for better organization.
    /// </summary>
    public class ScrapedAchievement
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string IconUrl { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public bool IsUnlocked { get; set; }

        /// <summary>
        /// Current progress value for achievements with partial completion (e.g., 25 out of 100).
        /// Null when the achievement doesn't have a progress bar.
        /// </summary>
        public int? ProgressNum { get; set; }

        /// <summary>
        /// Total required value for progress tracking (e.g., 100 for "Kill 100 enemies").
        /// Null when the achievement doesn't have a progress bar.
        /// </summary>
        public int? ProgressDenom { get; set; }
    }

    /// <summary>
    /// Result of steam achievement health check.
    /// Moved from Services layer for better organization.
    /// </summary>
    public class AchievementsScrapeResponse
    {
        public List<ScrapedAchievement> Rows { get; set; } = new List<ScrapedAchievement>();

        public bool TransientFailure { get; set; }
        public bool StatsUnavailable { get; set; }
        public string Detail { get; set; }
        public SteamScrapeDetail DetailCode { get; set; }

        /// <summary>
        /// Count parsed from the "N hidden achievements remaining" box when DetailCode is AllHidden.
        /// Null when the box is missing or its digits could not be read.
        /// </summary>
        public int? HiddenRemainingCount { get; set; }

        public string RequestedUrl { get; set; }
        public string FinalUrl { get; set; }
        public int StatusCode { get; set; }
        public string StatusDescription { get; set; }
        public string ContentBlurb { get; set; }

        public bool HasRows => Rows?.Count > 0;
        public bool SuccessWithRows => HasRows && !TransientFailure && !StatsUnavailable;

        public void SetDetail(SteamScrapeDetail detail)
        {
            DetailCode = detail;
            Detail = SteamScrapeDetailInfo.ToLegacyString(detail);
        }
    }
}
