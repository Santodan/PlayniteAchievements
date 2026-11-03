using System;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    // Session-only baseline. Failed/unknown reads never suppress a later refresh.
    internal sealed class LocalPollingFingerprintGate
    {
        private string _lastRefreshed;

        public bool NeedsRefresh(bool enabled, string fingerprint)
        {
            if (!enabled)
            {
                _lastRefreshed = null;
                return true;
            }
            return string.IsNullOrEmpty(fingerprint) ||
                !string.Equals(_lastRefreshed, fingerprint, StringComparison.Ordinal);
        }

        public void MarkRefreshed(string fingerprint) => _lastRefreshed = fingerprint;
    }
}
