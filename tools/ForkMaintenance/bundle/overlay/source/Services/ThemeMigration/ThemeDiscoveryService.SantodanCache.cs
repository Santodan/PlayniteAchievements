using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAchievements.Services.ThemeMigration
{
    public sealed partial class ThemeDiscoveryService
    {
        // Settings creates multiple discovery services. Share only bounded content hashes
        // and scan results, never theme text, paths, or UI objects. Reading each file still
        // detects edits even when its timestamp, length, and theme version stay unchanged.
        private static readonly object ContentScanCacheLock = new object();
        private static readonly Dictionary<string, (bool migratable, bool native)> ContentScanCache =
            new Dictionary<string, (bool migratable, bool native)>();
        private static readonly Queue<string> ContentScanInsertionOrder = new Queue<string>();
        // A normal installation can exceed 600 scanned files. Leave room for larger
        // libraries and evict one old result at a time instead of flushing a scan's work.
        private const int ContentScanCacheLimit = 4096;
        internal static long ContentScanMisses { get; private set; }

        internal static (bool migratable, bool native) AnalyzeThemeContent(string content)
        {
            string hash;
            using (var sha = SHA256.Create())
            {
                hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty)));
            }

            lock (ContentScanCacheLock)
            {
                if (ContentScanCache.TryGetValue(hash, out var cached))
                {
                    return cached;
                }

                var result = (ContainsMigratableReference(content), ContainsNativePlayniteAchievementsSupport(content));
                ContentScanMisses++;
                if (ContentScanCache.Count >= ContentScanCacheLimit)
                {
                    ContentScanCache.Remove(ContentScanInsertionOrder.Dequeue());
                }
                ContentScanCache[hash] = result;
                ContentScanInsertionOrder.Enqueue(hash);
                return result;
            }
        }

        private (bool migratable, bool native) AnalyzeThemeContentForCaller(string content)
        {
            return _useForkContentCache?.Invoke() == true
                ? AnalyzeThemeContent(content)
                : (ContainsMigratableReference(content), ContainsNativePlayniteAchievementsSupport(content));
        }
    }
}
