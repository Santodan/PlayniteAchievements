using SqlNado;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.ThemeMigration
{
    public sealed class StartPageCompatibilityResult
    {
        public bool Success { get; set; }
        public bool StartPageInstalled { get; set; }
        public long LocalRowsUpdated { get; set; }
        public long LocalRowsAlreadyCompatible { get; set; }
        public string BackupPath { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Applies a compatibility migration so older StartPage builds can read Local-provider
    /// achievements from the PlayniteAchievements cache database.
    /// </summary>
    public sealed class StartPageCompatibilityService
    {
        private const string StartPageExtensionFolderName = "felixkmh_StartPage_Plugin";
        private const string StartPagePluginAssembly = "StartPagePlugin.dll";
        private const string CacheFileName = "achievement_cache.db";
        private const string GuidGlob = "????????-????-????-????-????????????";

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly IPlayniteAPI _api;

        public StartPageCompatibilityService(ILogger logger, PlayniteAchievementsPlugin plugin, IPlayniteAPI api)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public bool IsStartPageInstalled() => !string.IsNullOrWhiteSpace(GetStartPagePluginPath());

        public Task<StartPageCompatibilityResult> ApplyAsync()
        {
            return Task.Run(ApplyInternal);
        }

        private StartPageCompatibilityResult ApplyInternal()
        {
            var startPagePluginPath = GetStartPagePluginPath();
            if (string.IsNullOrWhiteSpace(startPagePluginPath) || !File.Exists(startPagePluginPath))
            {
                return new StartPageCompatibilityResult
                {
                    Success = false,
                    StartPageInstalled = false,
                    Message = "StartPage is not installed."
                };
            }

            var cacheDbPath = Path.Combine(_plugin.GetPluginUserDataPath(), CacheFileName);
            if (!File.Exists(cacheDbPath))
            {
                return new StartPageCompatibilityResult
                {
                    Success = false,
                    StartPageInstalled = true,
                    Message = $"Cache database not found: {cacheDbPath}"
                };
            }

            try
            {
                // Create timestamped backup directory for both DB and DLL.
                var backupDir = CreateBackupDir();
                var dbBackupPath = Path.Combine(backupDir, CacheFileName);
                File.Copy(cacheDbPath, dbBackupPath, overwrite: false);

                // Back up the currently installed DLL, then stage the patched version.
                // The patched DLL cannot be copied while Playnite has it loaded; instead
                // it is written to a pending location and applied by the restart handler.
                bool dllStaged = false;
                var dllBackupPath = Path.Combine(backupDir, StartPagePluginAssembly);
                try
                {
                    File.Copy(startPagePluginPath, dllBackupPath, overwrite: false);
                    var bundledDll = GetBundledStartPageDllPath();
                    if (!string.IsNullOrWhiteSpace(bundledDll) && File.Exists(bundledDll))
                    {
                        File.Copy(bundledDll, GetPendingDllPath(), overwrite: true);
                        dllStaged = true;
                        _logger.Info("Staged patched StartPage DLL for install on next restart.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Could not stage StartPage DLL update: {ex.Message}");
                }

                long updatedRows;
                long alreadyCompatibleRows;

                using (var db = new SQLiteDatabase(
                    cacheDbPath,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE | SQLiteOpenOptions.SQLITE_OPEN_CREATE))
                {
                    db.ExecuteNonQuery("PRAGMA busy_timeout = 4000;");

                    // Fix 1: Backfill PlayniteGameId for Local rows where it is missing.
                    db.ExecuteNonQuery(@"
UPDATE Games
SET PlayniteGameId = (
    SELECT ugp.CacheKey
    FROM UserGameProgress ugp
    WHERE ugp.GameId = Games.Id
      AND ugp.CacheKey GLOB ?
    ORDER BY COALESCE(ugp.LastUpdatedUtc, '') DESC
    LIMIT 1
)
WHERE ProviderKey = 'Local'
  AND (PlayniteGameId IS NULL OR trim(PlayniteGameId) = '' OR PlayniteGameId NOT GLOB ?)
  AND EXISTS (
      SELECT 1
      FROM UserGameProgress ugp
      WHERE ugp.GameId = Games.Id
        AND ugp.CacheKey GLOB ?
  );", GuidGlob, GuidGlob, GuidGlob);

                    long gameIdRows = db.ExecuteScalar<long>("SELECT changes();");

                    // Never infer an achievement's unlock time from the game's cache update
                    // time. A library import or provider refresh is not an achievement unlock.
                    updatedRows = gameIdRows;

                    alreadyCompatibleRows = db.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM Games g
WHERE g.ProviderKey = 'Local'
  AND g.PlayniteGameId GLOB ?;", GuidGlob);
                }

                var message = updatedRows > 0
                    ? $"StartPage compatibility applied. Fixed {updatedRows} Local provider game ID row(s) in the achievements cache."
                    : "StartPage compatibility is already up to date. No Local cache rows needed changes.";

                if (dllStaged)
                    message += $"{Environment.NewLine}{Environment.NewLine}The patched StartPage plugin is ready — click Restart Playnite to apply it.";

                _logger.Info($"StartPage compatibility migration complete. UpdatedRows={updatedRows}, AlreadyCompatible={alreadyCompatibleRows}, DllStaged={dllStaged}");

                return new StartPageCompatibilityResult
                {
                    Success = true,
                    StartPageInstalled = true,
                    LocalRowsUpdated = updatedRows,
                    LocalRowsAlreadyCompatible = alreadyCompatibleRows,
                    BackupPath = dbBackupPath,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to apply StartPage compatibility migration.");
                return new StartPageCompatibilityResult
                {
                    Success = false,
                    StartPageInstalled = true,
                    Message = $"StartPage compatibility migration failed: {ex.Message}"
                };
            }
        }

        private string GetStartPagePluginPath()
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Extensions", StartPageExtensionFolderName, StartPagePluginAssembly),
                Path.Combine(GetPlayniteRootPath(), "Extensions", StartPageExtensionFolderName, StartPagePluginAssembly)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var extensionsRoot = Path.Combine(GetPlayniteRootPath(), "Extensions");
            if (Directory.Exists(extensionsRoot))
            {
                var discovered = Directory
                    .EnumerateFiles(extensionsRoot, StartPagePluginAssembly, SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.IndexOf(StartPageExtensionFolderName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    return discovered;
                }
            }

            return null;
        }

        private string GetPlayniteRootPath()
        {
            var extensionsDataPath = _api?.Paths?.ExtensionsDataPath;
            if (string.IsNullOrWhiteSpace(extensionsDataPath))
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }

            try
            {
                var parent = Directory.GetParent(extensionsDataPath);
                return parent?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public bool HasBackup()
        {
            return !string.IsNullOrWhiteSpace(GetLatestBackupPath());
        }

        public string GetLatestBackupPath()
        {
            var backupRoot = Path.Combine(_plugin.GetPluginUserDataPath(), "migration_backups");
            if (!Directory.Exists(backupRoot))
            {
                return null;
            }

            return Directory
                .EnumerateFiles(backupRoot, CacheFileName, SearchOption.AllDirectories)
                .OrderByDescending(f => f)
                .FirstOrDefault();
        }

        public Task<StartPageCompatibilityResult> RevertAsync()
        {
            return Task.Run(RevertInternal);
        }

        private StartPageCompatibilityResult RevertInternal()
        {
            var backupPath = GetLatestBackupPath();
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            {
                return new StartPageCompatibilityResult
                {
                    Success = false,
                    StartPageInstalled = IsStartPageInstalled(),
                    Message = "No backup found to revert."
                };
            }

            var cacheDbPath = Path.Combine(_plugin.GetPluginUserDataPath(), CacheFileName);
            try
            {
                // Restore database.
                File.Copy(backupPath, cacheDbPath, overwrite: true);
                _logger.Info($"StartPage compatibility DB reverted from backup: {backupPath}");

                // Stage the original DLL for restoration on next restart.
                bool dllStaged = false;
                var backupDir = Path.GetDirectoryName(backupPath);
                var dllBackupPath = Path.Combine(backupDir ?? string.Empty, StartPagePluginAssembly);
                if (File.Exists(dllBackupPath))
                {
                    try
                    {
                        File.Copy(dllBackupPath, GetPendingDllPath(), overwrite: true);
                        dllStaged = true;
                        _logger.Info("Staged original StartPage DLL for restore on next restart.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Could not stage DLL revert: {ex.Message}");
                    }
                }

                var msg = dllStaged
                    ? $"Achievement cache restored and original StartPage plugin queued for restore.{Environment.NewLine}Click Restart Playnite to complete the revert.{Environment.NewLine}Backup used: {backupPath}"
                    : $"The achievement cache was restored from backup.{Environment.NewLine}Backup used: {backupPath}";

                return new StartPageCompatibilityResult
                {
                    Success = true,
                    StartPageInstalled = IsStartPageInstalled(),
                    BackupPath = backupPath,
                    Message = msg
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to revert StartPage compatibility migration.");
                return new StartPageCompatibilityResult
                {
                    Success = false,
                    StartPageInstalled = IsStartPageInstalled(),
                    Message = $"Revert failed: {ex.Message}"
                };
            }
        }

        public string GetPendingDllPath() =>
            Path.Combine(_plugin.GetPluginUserDataPath(), "StartPagePlugin.pending.dll");

        public bool HasPendingDllUpdate() => File.Exists(GetPendingDllPath());

        public string GetInstalledDllPath() => GetStartPagePluginPath();

        private string GetBundledStartPageDllPath()
        {
            var assemblyDir = Path.GetDirectoryName(typeof(StartPageCompatibilityService).Assembly.Location);
            return Path.Combine(assemblyDir ?? string.Empty, "Resources", "StartPage", StartPagePluginAssembly);
        }

        private string CreateBackupDir()
        {
            var backupRoot = Path.Combine(_plugin.GetPluginUserDataPath(), "migration_backups");
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            var backupDir = Path.Combine(backupRoot, $"startpage-compat-{stamp}");
            Directory.CreateDirectory(backupDir);
            return backupDir;
        }
    }
}
