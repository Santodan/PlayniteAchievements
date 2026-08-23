using System;
using System.IO;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests.Captures
{
    /// <summary>
    /// On-disk capture library fixture shared by the capture tests: a unique temp root wired
    /// into <see cref="PersistedSettings"/> as both capture directories, plus the writer-shaped
    /// file drop. Dispose deletes the root; a leftover temp folder never fails a test run.
    /// </summary>
    internal sealed class CaptureTestDirectory : IDisposable
    {
        public CaptureTestDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "PlayAchCaptureTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new PersistedSettings
            {
                UnlockScreenshotDirectory = Root,
                UnlockRecordingDirectory = Root
            };
        }

        public string Root { get; }

        public PersistedSettings Settings { get; }

        public CaptureLibraryService CreateService() =>
            new CaptureLibraryService(() => Settings, null);

        /// <summary>Drops a capture file into the game's sanitized folder, as the writers do, and returns its path.</summary>
        public string WriteCapture(string gameName, string fileName)
        {
            var folder = Path.Combine(Root, UnlockScreenshotService.SanitizeCaptureGameName(gameName));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, "x");
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder must never fail a test run.
            }
        }
    }
}
