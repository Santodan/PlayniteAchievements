using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Which games have a Manage Achievements editor open.
    /// </summary>
    /// <remarks>
    /// The editor saves a game's authored achievements and category assignments as whole lists
    /// built from its rows, and records every write to the game into its undo history. A background
    /// writer touching the same game while it is open would either be overwritten by the editor's
    /// next save or become a step of the user's undo, so background capstone writes wait for the
    /// editor to close instead.
    /// </remarks>
    public static class OpenEditorRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, int> OpenCounts = new Dictionary<Guid, int>();

        /// <summary>Raised when the last editor open on a game closes.</summary>
        public static event Action<Guid> Closed;

        public static void Open(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            lock (Sync)
            {
                OpenCounts.TryGetValue(gameId, out var count);
                OpenCounts[gameId] = count + 1;
            }
        }

        public static void Close(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            lock (Sync)
            {
                if (!OpenCounts.TryGetValue(gameId, out var count))
                {
                    return;
                }

                if (count > 1)
                {
                    OpenCounts[gameId] = count - 1;
                    return;
                }

                OpenCounts.Remove(gameId);
            }

            Closed?.Invoke(gameId);
        }

        public static bool IsOpen(Guid gameId)
        {
            lock (Sync)
            {
                return OpenCounts.ContainsKey(gameId);
            }
        }
    }
}
