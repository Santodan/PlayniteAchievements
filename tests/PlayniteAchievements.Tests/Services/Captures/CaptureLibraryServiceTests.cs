using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Captures;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests.Captures
{
    /// <summary>
    /// Covers the notification and cache-maintenance contract the grids depend on: a capture saved
    /// while a window is open has to reach the open grid, and doing so must not force a full
    /// re-enumeration of every game folder.
    /// </summary>
    [TestClass]
    public class CaptureLibraryServiceTests
    {
        private string _root;
        private PersistedSettings _settings;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "PlayAchCaptureTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _settings = new PersistedSettings
            {
                UnlockScreenshotDirectory = _root,
                UnlockRecordingDirectory = _root
            };
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder must never fail a test run.
            }
        }

        private CaptureLibraryService CreateService() =>
            new CaptureLibraryService(() => _settings, null);

        /// <summary>Drops a capture file into the game's folder, as the writers do.</summary>
        private void WriteCapture(string gameName, string fileName)
        {
            var folder = Path.Combine(_root, UnlockScreenshotService.SanitizeCaptureGameName(gameName));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fileName), "x");
        }

        [TestMethod]
        public void Invalidate_RaisesCapturesChanged_WithTheSanitizedFolder()
        {
            var service = CreateService();
            CapturesChangedEventArgs seen = null;
            service.CapturesChanged += (_, e) => seen = e;

            service.Invalidate("Half-Life 2");

            Assert.IsNotNull(seen, "Writers rely on Invalidate to notify open grids.");
            Assert.AreEqual("Half-Life 2", seen.GameName);
            Assert.AreEqual(
                UnlockScreenshotService.SanitizeCaptureGameName("Half-Life 2"),
                seen.FolderName);
        }

        [TestMethod]
        public void InvalidateAll_RaisesCapturesChanged_WithNoGame()
        {
            var service = CreateService();
            CapturesChangedEventArgs seen = null;
            var raised = 0;
            service.CapturesChanged += (_, e) => { seen = e; raised++; };

            service.Invalidate();

            Assert.AreEqual(1, raised);
            Assert.IsNull(seen.GameName);
            Assert.IsNull(seen.FolderName);
        }

        [TestMethod]
        public void RefreshGame_DoesNotRaise()
        {
            WriteCapture("Portal", "001_Cake.png");
            var service = CreateService();
            var raised = 0;
            service.CapturesChanged += (_, __) => raised++;

            var set = service.RefreshGame("Portal");

            Assert.IsTrue(set.HasAny);
            Assert.AreEqual(0, raised, "Opening the gallery is a read and must not re-stamp open grids.");
        }

        [TestMethod]
        public void Invalidate_AddsTheNewFolderToTheMembershipSet()
        {
            var service = CreateService();
            // Materialize the set while the game has nothing, as an open grid would.
            Assert.IsFalse(service.GameFolderHasCaptures("Portal"));

            WriteCapture("Portal", "001_Cake.png");
            service.Invalidate("Portal");

            Assert.IsTrue(
                service.GameFolderHasCaptures("Portal"),
                "A capture saved while a grid is open must become visible without a rebuild.");
        }

        [TestMethod]
        public void Invalidate_KeepsTheMembershipSetForOtherGames()
        {
            WriteCapture("Portal", "001_Cake.png");
            WriteCapture("Braid", "001_Time.png");
            var service = CreateService();
            Assert.IsTrue(service.GameFolderHasCaptures("Portal"));

            // Delete Braid's captures behind the service's back. A targeted invalidate of Portal
            // must not re-enumerate (and therefore must not notice) the untouched game.
            Directory.Delete(
                Path.Combine(_root, UnlockScreenshotService.SanitizeCaptureGameName("Braid")),
                recursive: true);
            service.Invalidate("Portal");

            Assert.IsTrue(
                service.GameFolderHasCaptures("Braid"),
                "Invalidating one game should probe only that folder, not rebuild the whole set.");
        }

        [TestMethod]
        public void Invalidate_RemovesTheFolderWhenItsCapturesAreGone()
        {
            WriteCapture("Portal", "001_Cake.png");
            var service = CreateService();
            Assert.IsTrue(service.GameFolderHasCaptures("Portal"));

            Directory.Delete(
                Path.Combine(_root, UnlockScreenshotService.SanitizeCaptureGameName("Portal")),
                recursive: true);
            service.Invalidate("Portal");

            Assert.IsFalse(
                service.GameFolderHasCaptures("Portal"),
                "The probe re-reads reality, so a future delete path gets correct behavior.");
        }

        [TestMethod]
        public void InvalidateAll_ForcesAFullRescan()
        {
            WriteCapture("Portal", "001_Cake.png");
            var service = CreateService();
            Assert.IsTrue(service.GameFolderHasCaptures("Portal"));

            WriteCapture("Braid", "001_Time.png");
            service.Invalidate();

            Assert.IsTrue(
                service.GetGameFoldersWithCaptures().Contains(
                    UnlockScreenshotService.SanitizeCaptureGameName("Braid")),
                "The parameterless overload means 'I know nothing' and must recompute everything.");
        }

        [TestMethod]
        public void Invalidate_DropsTheParsedSetForThatGame()
        {
            WriteCapture("Portal", "001_Cake.png");
            var service = CreateService();
            Assert.AreEqual(1, service.ScanGame("Portal").Groups.Count);

            WriteCapture("Portal", "002_Companion.png");
            service.Invalidate("Portal");

            Assert.AreEqual(2, service.ScanGame("Portal").Groups.Count);
        }
    }
}
