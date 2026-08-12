using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// Sets the session-only <c>HasCaptures</c> flag on grid row items so the Captures column button
    /// only shows for games/achievements that actually have saved captures. Scanning happens on a
    /// background thread (the service caches per game) and the flags are applied on the UI dispatcher.
    /// Fire-and-forget: hosts call it after (re)building a collection and do not await it.
    /// Runs are globally serialized, so a run queued later always computes against state at least as
    /// fresh as an earlier one and always applies last; a stale scan can never overwrite a newer one.
    /// Pass <c>gameFolderFilter</c> to re-stamp only the rows of one game after a capture lands.
    /// </summary>
    internal static class CapturePresenceMarker
    {
        private static readonly object QueueGate = new object();
        private static Task _queue = Task.CompletedTask;

        public static void MarkSummaries(
            IReadOnlyList<GameSummaryItem> items,
            CaptureLibraryService service,
            string gameFolderFilter = null)
        {
            var snapshot = Snapshot(items, i => i.GameName, gameFolderFilter);
            if (snapshot == null || service == null)
            {
                return;
            }

            Run(
                () => snapshot.Select(i => service.GameFolderHasCaptures(i.GameName)).ToArray(),
                flags =>
                {
                    for (var i = 0; i < snapshot.Count; i++)
                    {
                        snapshot[i].HasCaptures = flags[i];
                    }
                });
        }

        public static void MarkAchievements(
            IReadOnlyList<AchievementDisplayItem> items,
            CaptureLibraryService service,
            string gameFolderFilter = null)
        {
            var snapshot = Snapshot(items, i => i.GameName, gameFolderFilter);
            if (snapshot == null || service == null)
            {
                return;
            }

            Run(
                () => snapshot.Select(i => service.AchievementHasCaptures(i.GameName, i.DisplayName)).ToArray(),
                flags =>
                {
                    for (var i = 0; i < snapshot.Count; i++)
                    {
                        snapshot[i].HasCaptures = flags[i];
                    }
                });
        }

        /// <summary>
        /// Copies the caller's list (it may be mutated once we hand off to the queue) and, when a
        /// filter is given, keeps only the rows whose game maps to that capture folder. Returns null
        /// when there is nothing to stamp. Runs on the caller's thread, which for the UI-bound
        /// collections is the dispatcher thread.
        /// </summary>
        private static List<T> Snapshot<T>(
            IReadOnlyList<T> items,
            Func<T, string> gameNameSelector,
            string gameFolderFilter)
            where T : class
        {
            if (items == null || items.Count == 0)
            {
                return null;
            }

            List<T> snapshot;
            if (string.IsNullOrEmpty(gameFolderFilter))
            {
                snapshot = items.Where(i => i != null).ToList();
            }
            else
            {
                snapshot = items
                    .Where(i => i != null && string.Equals(
                        UnlockScreenshotService.SanitizeCaptureGameName(gameNameSelector(i)),
                        gameFolderFilter,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return snapshot.Count == 0 ? null : snapshot;
        }

        private static void Run(Func<bool[]> compute, Action<bool[]> apply)
        {
            lock (QueueGate)
            {
                _queue = _queue.ContinueWith(
                    _ => Execute(compute, apply),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        private static void Execute(Func<bool[]> compute, Action<bool[]> apply)
        {
            bool[] flags;
            try
            {
                flags = compute();
            }
            catch
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                // Never let a failed apply fault the shared queue task.
                try
                {
                    apply(flags);
                }
                catch
                {
                    // Ignored: a stamping failure must not break later marks.
                }
            }
            else
            {
                // Same-priority dispatcher posts run FIFO, so applies land in compute order.
                dispatcher.BeginInvoke((Action)(() => apply(flags)));
            }
        }
    }
}
