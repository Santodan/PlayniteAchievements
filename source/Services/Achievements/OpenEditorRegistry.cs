using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Which games have a Manage Achievements editor showing.
    /// </summary>
    /// <remarks>
    /// The editor saves a game's authored achievements and category assignments as whole lists
    /// built from its rows, and records every write to the game into its undo history. A background
    /// writer touching the same game while it shows would either be overwritten by the editor's
    /// next save or become a step of the user's undo, so background capstone writes wait through
    /// <see cref="DeferWhileOpen"/> and run when the editor tab hides or its window closes. The
    /// Manage window reloads the editor's rows when the tab shows again.
    /// </remarks>
    public static class OpenEditorRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Guid, int> OpenCounts = new Dictionary<Guid, int>();

        // Per game, one catch-up per writer: a writer deferred twice for the same game runs once.
        private static readonly Dictionary<Guid, Dictionary<object, Action>> Deferred =
            new Dictionary<Guid, Dictionary<object, Action>>();

        /// <summary>
        /// Raised after the last editor on any game closes, so surfaces that held back their
        /// updates while one showed can catch up.
        /// </summary>
        public static event Action AllClosed;

        /// <summary>
        /// Raised after a background writer changed a game's custom data, so an open Manage window
        /// on that game reloads: those writes come after the refresh that window already showed.
        /// </summary>
        public static event Action<Guid> BackgroundWritten;

        public static void NotifyBackgroundWrite(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            try
            {
                BackgroundWritten?.Invoke(gameId);
            }
            catch
            {
                // A listener's failure is its own and must not fail the writer.
            }
        }

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

            List<Action> catchUps;
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
                catchUps = Deferred.TryGetValue(gameId, out var byOwner)
                    ? byOwner.Values.ToList()
                    : new List<Action>();
                Deferred.Remove(gameId);
            }

            foreach (var catchUp in catchUps)
            {
                // Each writer handles its own failures; one that throws anyway must not keep the
                // others from catching up.
                try
                {
                    catchUp();
                }
                catch
                {
                }
            }

            if (!IsAnyOpen)
            {
                AllClosed?.Invoke();
            }
        }

        /// <summary>
        /// Holds a background write back while the game's editor shows: returns true and runs
        /// <paramref name="catchUp"/> once the editor closes, or false, doing nothing, when no
        /// editor shows and the caller can write now. <paramref name="owner"/> identifies the
        /// writer, so deferring the same game again replaces its catch-up instead of adding one.
        /// </summary>
        public static bool DeferWhileOpen(Guid gameId, object owner, Action catchUp)
        {
            if (gameId == Guid.Empty || owner == null || catchUp == null)
            {
                return false;
            }

            lock (Sync)
            {
                if (!OpenCounts.ContainsKey(gameId))
                {
                    return false;
                }

                if (!Deferred.TryGetValue(gameId, out var byOwner))
                {
                    byOwner = new Dictionary<object, Action>();
                    Deferred[gameId] = byOwner;
                }

                byOwner[owner] = catchUp;
                return true;
            }
        }

        public static bool IsOpen(Guid gameId)
        {
            lock (Sync)
            {
                return OpenCounts.ContainsKey(gameId);
            }
        }

        /// <summary>True while an editor shows on any game.</summary>
        public static bool IsAnyOpen
        {
            get
            {
                lock (Sync)
                {
                    return OpenCounts.Count > 0;
                }
            }
        }
    }
}
