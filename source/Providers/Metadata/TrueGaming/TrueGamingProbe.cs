using Playnite.SDK;
using PlayniteAchievements.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.TrueGaming
{
    /// <summary>
    /// Developer-only check of whether TrueAchievements, TrueTrophies and TrueSteamAchievements pages
    /// load in an offscreen view past Cloudflare, and what their achievement markup carries. Logs one
    /// line per page and saves each page source next to the log for parser fixtures.
    /// </summary>
    internal static class TrueGamingProbe
    {
        /// <summary>Hardcoded gate for the hidden "Probe TrueGaming" menu item. Off in shipped builds.</summary>
        internal static readonly bool Enabled = false;

        private const int Attempts = 3;
        private const int RetryDelayMs = 3000;
        private const int NavigationTimeoutMs = 30000;

        private static readonly string[] Urls =
        {
            // Elden Ring Nightreign on each site: the search (real slugs) and the list page the
            // lookup's slug opens, with the final address showing any redirect.
            "https://www.trueachievements.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://www.trueachievements.com/game/ELDEN-RING-NIGHTREIGN/achievements",
            "https://www.truetrophies.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://www.truetrophies.com/game/ELDEN-RING-NIGHTREIGN/trophies",
            "https://www.truetrophies.com/game/ELDEN-RING-NIGHTREIGN-PS5/trophies",
            "https://truesteamachievements.com/true-api/sitesearch/search?q=Elden%20Ring%20Nightreign&category=Games",
            "https://truesteamachievements.com/game/ELDEN-RING-NIGHTREIGN/achievements"
        };

        private static readonly Regex AchievementItem = new Regex(@"<li[^>]*\bid=""\d+_\d+_\d+""", RegexOptions.CultureInvariant);
        private static readonly Regex FlagClass = new Regex(@"class=""flg-([0-9a-fA-F]{8})""", RegexOptions.CultureInvariant);
        private static readonly Regex FlagFilter = new Regex(
            @"id=""chkFlag_(\d+)""[^>]*/>\s*<label[^>]*>\s*<i[^>]*></i>\s*<b>([\d,]+)</b>\s*([^<]+)</label>",
            RegexOptions.CultureInvariant);
        private static readonly Regex DlcHeader = new Regex(@"class=""pnl-hd[^""]*\bdlc\b", RegexOptions.CultureInvariant);
        private static readonly Regex Title = new Regex(@"<title>([^<]*)</title>", RegexOptions.CultureInvariant);

        public static async Task RunAsync(IPlayniteAPI api, ILogger logger, string outputDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDirectory);
            var views = new OffscreenViewLeaseSource(api, logger);
            using (views.BeginLease())
            {
                for (var i = 0; i < Urls.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var url = Urls[i];
                    try
                    {
                        await ProbeAsync(views, logger, url, Path.Combine(outputDirectory, $"{i:00}-{FileStem(url)}.html"), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, $"[TrueGamingProbe] {url} failed");
                    }
                }
            }

            logger?.Info($"[TrueGamingProbe] done; pages saved to {outputDirectory}");
        }

        private static async Task ProbeAsync(OffscreenViewLeaseSource views, ILogger logger, string url, string file, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            var (html, address, attempts) = await views.WithNavigableViewAsync(async view =>
            {
                await view.NavigateAndWaitAsync(url, NavigationTimeoutMs).ConfigureAwait(false);
                var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                var attempt = 1;
                while (IsChallenge(source) && attempt < Attempts)
                {
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    source = await view.GetPageSourceAsync().ConfigureAwait(false);
                    attempt++;
                }

                // A JSON endpoint renders as a text document; keep the text, not the wrapping markup.
                if (url.IndexOf("/true-api/", StringComparison.OrdinalIgnoreCase) >= 0 && !IsChallenge(source))
                {
                    source = await view.GetPageTextAsync().ConfigureAwait(false);
                }

                return (source ?? string.Empty, view.GetCurrentAddress(), attempt);
            }, ct).ConfigureAwait(false);

            File.WriteAllText(file, html, Encoding.UTF8);

            var masks = FlagClass.Matches(html).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
            var filters = FlagFilter.Matches(html).Cast<Match>()
                .Select(m => $"{m.Groups[1].Value}:{m.Groups[3].Value.Trim()}({m.Groups[2].Value})")
                .ToList();
            var title = Title.Match(html);
            var excerpt = html.Length > 2048 ? html.Substring(0, 2048) : html;

            logger?.Info(
                $"[TrueGamingProbe] {url} -> {address} | {watch.ElapsedMilliseconds} ms, attempts={attempts}, " +
                $"bytes={html.Length}, challenge={IsChallenge(html)}, title=\"{(title.Success ? title.Groups[1].Value.Trim() : "")}\", " +
                $"items={AchievementItem.Matches(html).Count}, flagged={masks.Count}, distinctMasks={masks.Distinct().Count()}, " +
                $"dlcHeaders={DlcHeader.Matches(html).Count}, filters=[{string.Join(", ", filters)}]");

            if (url.IndexOf("/true-api/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Info($"[TrueGamingProbe] api excerpt: {excerpt.Replace("\r", " ").Replace("\n", " ")}");
            }
        }

        private static bool IsChallenge(string html) =>
            string.IsNullOrEmpty(html) ||
            html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("cf-chl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            html.IndexOf("Attention Required", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FileStem(string url)
        {
            var uri = new Uri(url);
            var stem = (uri.Host.Replace("www.", "") + uri.AbsolutePath + uri.Query).Trim('/');
            return Regex.Replace(stem, @"[^A-Za-z0-9]+", "-").Trim('-');
        }
    }
}
