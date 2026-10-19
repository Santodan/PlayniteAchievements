using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Metadata
{
    public enum MetadataProbeOutcome
    {
        Skipped,
        Native,
        SourceUnavailable,
        NotFound,
        FetchFailed,
        Rejected,
        Matched
    }

    /// <summary>What matching one game against one metadata source would do.</summary>
    public sealed class MetadataProbeResult
    {
        public MetadataProbeOutcome Outcome { get; set; }

        public string LinkId { get; set; }

        public string Label { get; set; }

        /// <summary>The link was already saved for the game, so no search ran.</summary>
        public bool FromSavedLink { get; set; }

        public bool FoundByName { get; set; }

        public bool ByAchievementId { get; set; }

        public int Matched { get; set; }

        public int Total { get; set; }
    }

    /// <summary>Totals of a match test, for the summary shown when it ends.</summary>
    public sealed class MetadataMatchTestSummary
    {
        public int Tested { get; set; }

        public int Matched { get; set; }

        public int Rejected { get; set; }

        public int NotFound { get; set; }

        public int Failed { get; set; }

        public int AchievementsMatched { get; set; }

        public int AchievementsTotal { get; set; }

        public bool SourceUnavailable { get; set; }

        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Matches every cached game against one metadata source the way a refresh would, without
    /// storing links or changing any data, and writes one CSV row per game as it goes so a
    /// cancelled test still leaves its results.
    /// </summary>
    internal static class MetadataMatchTest
    {
        public static async Task<MetadataMatchTestSummary> RunAsync(
            MetadataOverlay overlay,
            IReadOnlyList<(Game Game, GameAchievementData Data)> games,
            string sourceKey,
            string csvPath,
            Action<int, int, string> progress,
            CancellationToken ct)
        {
            var summary = new MetadataMatchTestSummary();
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath));
            using (var run = overlay.BeginRun())
            using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Game,Platform,Result,Matched,Total,Share,Set,Saved link,Found by name,By achievement id");
                for (var i = 0; i < games.Count; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        summary.Cancelled = true;
                        break;
                    }

                    var (game, data) = games[i];
                    var name = game?.Name ?? data?.GameName ?? string.Empty;
                    progress?.Invoke(i, games.Count, name);

                    MetadataProbeResult result;
                    try
                    {
                        result = await run.ProbeAsync(game, data, sourceKey, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        summary.Cancelled = true;
                        break;
                    }

                    if (result.Outcome == MetadataProbeOutcome.Skipped || result.Outcome == MetadataProbeOutcome.Native)
                    {
                        continue;
                    }

                    if (result.Outcome == MetadataProbeOutcome.SourceUnavailable)
                    {
                        summary.SourceUnavailable = true;
                        break;
                    }

                    Count(summary, result);
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(name),
                        Csv(data?.ProviderKey),
                        result.Outcome.ToString(),
                        result.Matched.ToString(CultureInfo.InvariantCulture),
                        result.Total.ToString(CultureInfo.InvariantCulture),
                        result.Total == 0 ? string.Empty : ((double)result.Matched / result.Total).ToString("0%", CultureInfo.InvariantCulture),
                        Csv(string.IsNullOrWhiteSpace(result.Label) ? result.LinkId : $"{result.Label} ({result.LinkId})"),
                        result.FromSavedLink ? "yes" : string.Empty,
                        result.FoundByName ? "yes" : string.Empty,
                        result.ByAchievementId ? "yes" : string.Empty
                    }));
                    writer.Flush();
                }
            }

            progress?.Invoke(games.Count, games.Count, null);
            return summary;
        }

        private static void Count(MetadataMatchTestSummary summary, MetadataProbeResult result)
        {
            summary.Tested++;
            switch (result.Outcome)
            {
                case MetadataProbeOutcome.Matched:
                    summary.Matched++;
                    summary.AchievementsMatched += result.Matched;
                    summary.AchievementsTotal += result.Total;
                    break;
                case MetadataProbeOutcome.Rejected:
                    summary.Rejected++;
                    break;
                case MetadataProbeOutcome.FetchFailed:
                    summary.Failed++;
                    break;
                default:
                    summary.NotFound++;
                    break;
            }
        }

        private static string Csv(string value)
        {
            var text = value ?? string.Empty;
            return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
                ? text
                : "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
