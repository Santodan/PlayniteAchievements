using System;
using System.Collections.Generic;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Reports which achievements had their icon overrides written, so the shell can apply the
    /// changed icons to the cache once the editing burst settles.
    /// </summary>
    public sealed class IconOverridesSavedEventArgs : EventArgs
    {
        public IconOverridesSavedEventArgs(
            IReadOnlyCollection<string> changedApiNames,
            bool editorRowsStale = false,
            Guid gameId = default)
        {
            ChangedApiNames = changedApiNames ?? Array.Empty<string>();
            EditorRowsStale = editorRowsStale;
            GameId = gameId;
        }

        /// <summary>
        /// The game of the achievements, named by the library editor, whose rows span games;
        /// empty when the editor lists one game, which is then the one meant.
        /// </summary>
        public Guid GameId { get; }

        public IReadOnlyCollection<string> ChangedApiNames { get; }

        /// <summary>
        /// True when the editor's own rows do not show the change yet (a reset reloads them from
        /// the cache before the provider icons are written back), so the editor must reload once
        /// the icons are applied. An icon edit leaves it false: its rows already show the icon.
        /// </summary>
        public bool EditorRowsStale { get; }
    }
}
