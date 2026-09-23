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
        public IconOverridesSavedEventArgs(IReadOnlyCollection<string> changedApiNames)
        {
            ChangedApiNames = changedApiNames ?? Array.Empty<string>();
        }

        public IReadOnlyCollection<string> ChangedApiNames { get; }
    }
}
