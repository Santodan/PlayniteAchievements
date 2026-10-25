using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// Developer-only check of where Data for Azeroth serves its dynamic index once the site's human
    /// check is cleared. Requests the index on the apex and the www host with the browser's cookies,
    /// without following redirects, and loads both in an offscreen browser for comparison. Writes
    /// report.txt under the output folder. Cookie values are never written.
    /// </summary>
    internal static class DataForAzerothProbe
    {
        /// <summary>Hardcoded gate for the hidden probe menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        internal const string CheckUrl = "https://www.dataforazeroth.com/";

        private const string LogPrefix = "[DFA/Probe]";
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly string[] IndexUrls =
        {
            "https://dataforazeroth.com/dynamic/index.json",
            "https://www.dataforazeroth.com/dynamic/index.json"
        };

        /// <summary>
        /// Opens the site in a dialog so the human check can be ticked. Must run on the UI thread;
        /// returns when the window is closed.
        /// </summary>
        public static void ShowCheckWindow(IPlayniteAPI api)
        {
            using (var view = api.WebViews.CreateView(1000, 800))
            {
                view.Navigate(CheckUrl);
                view.OpenDialog();
            }
        }

        public static async Task RunAsync(IPlayniteAPI api, ILogger logger, string folder, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            var report = new StringBuilder();
            report.AppendLine($"Data for Azeroth probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            report.AppendLine();

            var views = new OffscreenViewLeaseSource(api, logger);
            var cookies = CefCookieReader.Filter(
                await CefCookieReader.ReadAsync(api, views, DataForAzerothClient.SiteDomain, logger, LogPrefix, ct).ConfigureAwait(false),
                DataForAzerothClient.SiteDomain);
            var header = CefCookieReader.BuildCookieHeader(cookies);
            var hasGate = cookies.Exists(c => string.Equals(c.Name, DataForAzerothClient.GateCookieName, StringComparison.OrdinalIgnoreCase));
            report.AppendLine($"cookies: count={cookies.Count}, names={CefCookieReader.DescribeNames(cookies)}, gateCookie={hasGate}");
            foreach (var cookie in cookies)
            {
                report.AppendLine($"  {cookie.Name}: domain={cookie.Domain}, path={cookie.Path}");
            }

            report.AppendLine();

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                AllowAutoRedirect = false
            };

            using (var http = new HttpClient(handler))
            {
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                // Round 2: both hosts answer the old index path with the app shell, so save the
                // shell and every script it loads, and list the data paths the scripts name.
                report.AppendLine($"== shell {CheckUrl}");
                var shell = await GetAsync(http, CheckUrl, header, report, ct).ConfigureAwait(false) ?? string.Empty;
                File.WriteAllText(Path.Combine(folder, "shell.html"), shell, Encoding.UTF8);
                report.AppendLine("shell: " + shell.Replace("\r", " ").Replace("\n", " "));
                report.AppendLine();

                var scripts = Regex.Matches(shell, "src=\"([^\"]+\\.js)\"")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                foreach (var src in scripts)
                {
                    var scriptUrl = new Uri(new Uri(CheckUrl), src).ToString();
                    report.AppendLine($"== script {scriptUrl}");
                    var js = await GetAsync(http, scriptUrl, header, report, ct).ConfigureAwait(false) ?? string.Empty;
                    var file = Regex.Replace(Path.GetFileName(new Uri(scriptUrl).AbsolutePath), @"[^A-Za-z0-9_.\-]", "_");
                    File.WriteAllText(Path.Combine(folder, file), js, Encoding.UTF8);
                    report.AppendLine($"saved: {file}");

                    var paths = Regex.Matches(js, "[\"'`]([^\"'`\\s]{0,120}(?:\\.json|/api/|/dynamic|rarity)[^\"'`\\s]{0,120})[\"'`]", RegexOptions.IgnoreCase)
                        .Cast<Match>()
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .Take(80);
                    foreach (var found in paths)
                    {
                        report.AppendLine("  path: " + found);
                    }

                    report.AppendLine();
                }
            }

            // Round 3: the production client end to end, from the retail index to the rarity map.
            report.AppendLine($"== client {DataForAzerothClient.IndexUrl}");
            using (var client = new DataForAzerothClient(api, logger))
            {
                var result = await client.LoadRarityAsync(ct).ConfigureAwait(false);
                report.AppendLine($"status={result.Status}, entries={result.Rarity.Count}");
                foreach (var id in new[] { "6", "157", "2144" })
                {
                    report.AppendLine($"  {id}: {(result.Rarity.TryGetValue(id, out var percent) ? percent.ToString("0.####") : "<absent>")}");
                }
            }

            report.AppendLine();

            report.AppendLine("Done.");
            var path = Path.Combine(folder, "report.txt");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            logger?.Info($"{LogPrefix} Report written to {path}{Environment.NewLine}{report}");
        }

        private static async Task<string> GetAsync(HttpClient http, string url, string cookieHeader, StringBuilder report, CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    }

                    using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        report.AppendLine(
                            $"status={(int)response.StatusCode}, type={response.Content?.Headers?.ContentType?.MediaType ?? "<none>"}, " +
                            $"location={response.Headers.Location?.ToString() ?? "<none>"}, server={string.Join(",", response.Headers.Server)}");
                        DescribeBody(body, report);
                        return body;
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void DescribeBody(string body, StringBuilder report)
        {
            body = body ?? string.Empty;
            var head = body.Length > 300 ? body.Substring(0, 300) : body;
            report.AppendLine(
                $"length={body.Length}, botCheck={DataForAzerothClient.LooksLikeBotCheck(body)}, " +
                $"appShell={body.IndexOf("id=\"root\"", StringComparison.OrdinalIgnoreCase) >= 0}, " +
                $"hasRarityKey={body.IndexOf("achievementsrarity", StringComparison.OrdinalIgnoreCase) >= 0}");
            report.AppendLine("head: " + head.Replace("\r", " ").Replace("\n", " "));
        }

        private static async Task ReportRarityAsync(HttpClient http, string indexUrl, string body, string cookieHeader, StringBuilder report, CancellationToken ct)
        {
            string path;
            try
            {
                path = JObject.Parse(body ?? string.Empty).Value<string>("achievementsrarity");
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                report.AppendLine("index parsed but has no achievementsrarity");
                return;
            }

            var rarityUrl = new Uri(new Uri(indexUrl), path).ToString();
            report.AppendLine($"-- rarity {rarityUrl}");
            var rarity = await GetAsync(http, rarityUrl, cookieHeader, report, ct).ConfigureAwait(false);
            try
            {
                var achievements = JObject.Parse(rarity ?? string.Empty)["achievements"] as JObject;
                report.AppendLine($"rarity entries={achievements?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"rarity parse failed: {ex.Message}");
            }
        }
    }
}
