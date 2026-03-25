using System;
using System.IO;

namespace PlayniteAchievements.Services
{
    internal static class BackupHelper
    {
        private const string BackupRootFolderName = "migration_backups";

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
