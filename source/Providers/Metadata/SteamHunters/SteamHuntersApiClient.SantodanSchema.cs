using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    internal sealed partial class SteamHuntersApiClient
    {
        // Public SteamHunters definitions: no Steam token, session, or schema request.
        internal async Task<List<AchievementDetail>> GetSchemaAsync(int appId, CancellationToken ct)
        {
            if (appId <= 0) return null;
            try
            {
                var items = await FetchJsonAsync<JArray>(BaseUrl + "/apps/" +
                    appId.ToString(CultureInfo.InvariantCulture) + "/achievements", ct).ConfigureAwait(false);
                return ParseSchema(appId, items);
            }
            catch (SteamHuntersUnusableResponseException ex)
            {
                _logger?.Warn($"[SteamHunters] Schema request failed for appId={appId}: {ex.Message}");
                return null;
            }
        }

        internal static List<AchievementDetail> ParseSchema(int appId, JArray items)
        {
            string Icon(string value) => string.IsNullOrWhiteSpace(value) ? null :
                Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.ToString() :
                SteamApiClient.BuildAchievementIconUrl(appId, value);

            return (items ?? new JArray()).OfType<JObject>()
                .Where(item => !string.IsNullOrWhiteSpace(item["apiName"]?.Value<string>()))
                .GroupBy(item => item["apiName"].Value<string>().Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var item = group.First();
                    var percent = item["steamPercentage"]?.Value<double?>() ?? item["estimatedSteamPercentage"]?.Value<double?>();
                    return new AchievementDetail
                    {
                        ApiName = group.Key,
                        DisplayName = WebUtility.HtmlDecode(item["name"]?.Value<string>() ?? group.Key),
                        Description = WebUtility.HtmlDecode(item["description"]?.Value<string>() ?? string.Empty),
                        Hidden = item["hidden"]?.Value<bool?>() == true,
                        UnlockedIconPath = Icon(item["icon"]?.Value<string>()),
                        LockedIconPath = Icon(item["iconGray"]?.Value<string>()),
                        GlobalPercentUnlocked = percent.HasValue && !double.IsNaN(percent.Value) && !double.IsInfinity(percent.Value)
                            ? (double?)Math.Max(0, Math.Min(100, percent.Value)) : null,
                        Points = item["points"]?.Value<int?>(),
                        Unlocked = false,
                        UnlockTimeUtc = null
                    };
                }).ToList();
        }
    }
}
