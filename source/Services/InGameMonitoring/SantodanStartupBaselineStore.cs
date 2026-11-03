using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    internal sealed class SantodanStartupBaselineStore
    {
        private sealed class Receipt
        {
            public int Version { get; set; }
            public Guid GameId { get; set; }
            public int AppId { get; set; }
            public DateTime CacheUpdatedUtc { get; set; }
            public DateTime ValidatedUtc { get; set; }
            public string SourceHash { get; set; }
            public string ConfigurationHash { get; set; }
            public string AchievementFilePath { get; set; }
        }

        private readonly string _folder;
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
        public SantodanStartupBaselineStore(string userDataFolder) =>
            _folder = Path.Combine(userDataFolder, "SantodanPollingBaselines");

        public bool Matches(Guid gameId, int appId, DateTime cacheUpdatedUtc, string fingerprint,
            DateTime nowUtc, DateTime? sessionStartUtc = null)
        {
            if (gameId == Guid.Empty || cacheUpdatedUtc == default || string.IsNullOrEmpty(fingerprint))
                return false;
            try
            {
                var receipt = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(ReceiptPath(gameId)));
                return receipt != null && receipt.Version == 1 && receipt.GameId == gameId &&
                    receipt.AppId == appId && receipt.CacheUpdatedUtc == cacheUpdatedUtc &&
                    receipt.ValidatedUtc <= nowUtc && nowUtc - receipt.ValidatedUtc <= MaxAge &&
                    (!sessionStartUtc.HasValue || receipt.ValidatedUtc < sessionStartUtc.Value) &&
                    string.Equals(receipt.SourceHash, Hash(fingerprint), StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (JsonException) { return false; }
        }

        // This only avoids a presence-discovery scan. It never validates progress or
        // skips a refresh; normal full fingerprint checks still run at their deadline.
        public bool HasExistingAchievementFile(Guid gameId, int appId, DateTime cacheUpdatedUtc,
            string configuration, DateTime nowUtc, DateTime sessionStartUtc)
        {
            if (gameId == Guid.Empty || cacheUpdatedUtc == default || string.IsNullOrEmpty(configuration)) return false;
            try
            {
                var receipt = JsonConvert.DeserializeObject<Receipt>(File.ReadAllText(ReceiptPath(gameId)));
                return receipt != null && receipt.Version == 1 && receipt.GameId == gameId &&
                    receipt.AppId == appId && receipt.CacheUpdatedUtc == cacheUpdatedUtc &&
                    receipt.ValidatedUtc < sessionStartUtc && receipt.ValidatedUtc <= nowUtc &&
                    nowUtc - receipt.ValidatedUtc <= MaxAge &&
                    receipt.ConfigurationHash == Hash(configuration) &&
                    !string.IsNullOrEmpty(receipt.AchievementFilePath) && File.Exists(receipt.AchievementFilePath);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (JsonException) { return false; }
        }

        public void Save(Guid gameId, int appId, DateTime cacheUpdatedUtc, string fingerprint, DateTime nowUtc,
            string configuration = null, string achievementFilePath = null)
        {
            if (gameId == Guid.Empty || cacheUpdatedUtc == default || string.IsNullOrEmpty(fingerprint))
                return;
            Directory.CreateDirectory(_folder);
            var destination = ReceiptPath(gameId);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(new Receipt
                {
                    Version = 1, GameId = gameId, AppId = appId, CacheUpdatedUtc = cacheUpdatedUtc,
                    ValidatedUtc = nowUtc, SourceHash = Hash(fingerprint),
                    ConfigurationHash = configuration == null ? null : Hash(configuration),
                    AchievementFilePath = achievementFilePath
                }));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private string ReceiptPath(Guid gameId) => Path.Combine(_folder, gameId.ToString("N") + ".json");
        private static string Hash(string fingerprint)
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(fingerprint)));
        }
    }
}
