using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// Sets the session-only <c>HasCaptures</c> flag on grid row items so the Captures column button
    /// only shows for games/achievements that actually have saved captures. Scanning happens on a
    /// background thread (the service caches per game) and the flags are applied on the UI dispatcher.
    /// Fire-and-forget: hosts call it after (re)building a collection and do not await it.
    /// </summary>
    internal static class CapturePresenceMarker
    {
        public static void MarkSummaries(
            IReadOnlyList<GameSummaryItem> items,
            CaptureLibraryService service)
        {
            if (items == null || items.Count == 0 || service == null)
            {
                return;
            }

            var snapshot = items.ToList();
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
            CaptureLibraryService service)
        {
            if (items == null || items.Count == 0 || service == null)
            {
                return;
            }

            var snapshot = items.ToList();
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

        private static void Run(Func<bool[]> compute, Action<bool[]> apply)
        {
            Task.Run(() =>
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
                    apply(flags);
                }
                else
                {
                    dispatcher.BeginInvoke((Action)(() => apply(flags)));
                }
            });
        }
    }
}
