using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    // Access is serialized by the monitor's state lock. Observed unlocks survive cache relocks.
    internal sealed class InGameSessionUnlocks
    {
        private readonly HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Remember(IEnumerable<string> unlockedKeys)
        {
            if (unlockedKeys == null) return;
            foreach (var key in unlockedKeys)
            {
                if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
            }
        }

        public bool Add(string key) => !string.IsNullOrWhiteSpace(key) && keys.Add(key);
        public bool Contains(string key) => keys.Contains(key);
    }
}
