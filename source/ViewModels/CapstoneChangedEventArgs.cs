using System;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Reports which achievement became a capstone, so the shell can refresh what depends on it.
    /// </summary>
    public sealed class CapstoneChangedEventArgs : EventArgs
    {
        public CapstoneChangedEventArgs(string apiName, string displayName)
        {
            ApiName = apiName;
            DisplayName = displayName;
        }

        public string ApiName { get; }

        public string DisplayName { get; }
    }
}
