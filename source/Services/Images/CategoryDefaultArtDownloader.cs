using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Downloads category art from image addresses into a game's default category images, the
    /// provider-default convention <see cref="CategoryDefaultImageResolver"/> reads: existing art is
    /// kept and user overrides win over defaults. Best-effort: failures never fail the caller, and
    /// existing targets are skipped so repeat refreshes cost nothing.
    /// </summary>
    internal static class CategoryDefaultArtDownloader
    {
        public static async Task DownloadAsync(
            DiskImageService diskImageService,
            Guid? playniteGameId,
            IReadOnlyList<(string Label, string ImageUrl)> entries,
            ILogger logger,
            CancellationToken cancel)
        {
            if (diskImageService == null || entries == null || entries.Count == 0 ||
                playniteGameId == null || playniteGameId.Value == Guid.Empty)
            {
                return;
            }

            var gameIdText = playniteGameId.Value.ToString("D");
            var seenLabels = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var entry in entries)
            {
                cancel.ThrowIfCancellationRequested();
                var label = AchievementCategoryHelper.NormalizeCategoryOrDefault(entry.Label);
                if (string.IsNullOrWhiteSpace(entry.ImageUrl) ||
                    string.Equals(
                        label,
                        AchievementCategoryHelper.DefaultCategoryLabel,
                        StringComparison.OrdinalIgnoreCase) ||
                    !seenLabels.Add(label))
                {
                    continue;
                }

                try
                {
                    var artTarget = diskImageService.GetDefaultCategoryImagePath(gameIdText, label);
                    // decodeSize 0 stores the original bytes: no square crop, original aspect.
                    await diskImageService.GetOrDownloadIconToPathAsync(entry.ImageUrl, artTarget, decodeSize: 0, cancel)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, $"Default category image download failed for category '{entry.Label}'.");
                }
            }
        }
    }
}
