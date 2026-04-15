using System;
using System.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Logging
{
    internal static class PluginLogger
    {
        public static ILogger GetLogger(string name) => new NullLogger();

        private sealed class NullLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}

namespace PlayniteAchievements.Services.ProgressReporting
{
    internal sealed class IconDownloadProgress
    {
        private readonly int _total;
        private int _downloaded;

        public IconDownloadProgress(int total)
        {
            _total = Math.Max(0, total);
        }

        public bool HasWork => _total > 0;

        public (int Downloaded, int Total) AdvanceAndGetSnapshot()
        {
            if (_total <= 0)
            {
                return (0, 0);
            }

            var downloaded = Interlocked.Increment(ref _downloaded);
            if (downloaded > _total)
            {
                downloaded = _total;
            }

            return (downloaded, _total);
        }
    }
}
