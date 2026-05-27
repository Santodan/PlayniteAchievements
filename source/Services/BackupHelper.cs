using System;
using System.IO;

namespace PlayniteAchievements.Services
{
    internal static class BackupHelper
    {
        private const string BackupRootFolderName = "migration_backups";

        // ----------------------------------------------------------------
        // Settings backups
        // - <source>.pre.bak : pre-write safety backup (previous state)
        // - <source>.bak     : post-write mirror of latest successful config
        // ----------------------------------------------------------------

        /// <summary>
        /// Copies <paramref name="sourceFilePath"/> to
        /// <c><paramref name="sourceFilePath"/>.pre.bak</c> (overwriting any existing
        /// pre-write backup). Call this immediately before each write so a crash
        /// mid-write still leaves the previous good state available.
        /// </summary>
        public static void WritePreWriteBackup(string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return;

            try
            {
                File.Copy(sourceFilePath, sourceFilePath + ".pre.bak", overwrite: true);
            }
            catch
            {
                // Best-effort; never fail the calling operation
            }
        }

        /// <summary>
        /// Copies <paramref name="sourceFilePath"/> to
        /// <c><paramref name="sourceFilePath"/>.bak</c> after a successful write.
        /// This keeps .bak as an exact mirror of the latest known-good config,
        /// useful for diagnosing what changed/corrupted.
        /// </summary>
        public static void WritePostWriteMirror(string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return;

            try
            {
                File.Copy(sourceFilePath, sourceFilePath + ".bak", overwrite: true);
            }
            catch
            {
                // Best-effort; never fail the calling operation
            }
        }

        public static string CreateBackup(string root, string backupNamePrefix, params string[] filesToBackup)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Unable to create migration backup: plugin data directory is unknown.");
            }

            var backupRoot = Path.Combine(root, BackupRootFolderName);
            Directory.CreateDirectory(backupRoot);

            var normalizedPrefix = string.IsNullOrWhiteSpace(backupNamePrefix)
                ? "migration"
                : backupNamePrefix.Trim();

            var backupPath = Path.Combine(
                backupRoot,
                $"{normalizedPrefix}-{DateTime.UtcNow:yyyyMMdd_HHmmssfff}");
            Directory.CreateDirectory(backupPath);

            if (filesToBackup == null)
            {
                return backupPath;
            }

            for (var i = 0; i < filesToBackup.Length; i++)
            {
                var sourcePath = filesToBackup[i];
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(backupPath, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            return backupPath;
        }
    }
}
