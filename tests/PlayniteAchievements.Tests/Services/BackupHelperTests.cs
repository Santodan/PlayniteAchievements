using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services;
using System;
using System.IO;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class BackupHelperTests
    {
        [TestMethod]
        public void CreateBackup_CopiesExistingFilesIntoMigrationBackupsFolder()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var configPath = Path.Combine(tempDir, "config.json");
                var sidecarPath = Path.Combine(tempDir, "config.json.baksource");
                var missingPath = Path.Combine(tempDir, "missing.json");

                File.WriteAllText(configPath, "{ \"legacy\": true }");
                File.WriteAllText(sidecarPath, "sidecar");

                var backupPath = BackupHelper.CreateBackup(
                    tempDir,
                    "config-migration",
                    configPath,
                    sidecarPath,
                    missingPath);

                Assert.IsTrue(Directory.Exists(backupPath));
                StringAssert.StartsWith(
                    backupPath,
                    Path.Combine(tempDir, "migration_backups") + Path.DirectorySeparatorChar);
                StringAssert.StartsWith(Path.GetFileName(backupPath), "config-migration-");

                Assert.AreEqual(
                    "{ \"legacy\": true }",
                    File.ReadAllText(Path.Combine(backupPath, "config.json")));
                Assert.AreEqual(
                    "sidecar",
                    File.ReadAllText(Path.Combine(backupPath, "config.json.baksource")));
                Assert.IsFalse(File.Exists(Path.Combine(backupPath, "missing.json")));
            }
            finally
            {
                DeleteDirectory(tempDir);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
