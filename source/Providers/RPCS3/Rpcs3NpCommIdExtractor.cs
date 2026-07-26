using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.RPCS3
{
    internal static class Rpcs3NpCommIdExtractor
    {
        private static readonly Regex NpCommIdElementPattern =
            new Regex(@"<npcommid>\s*(NPWR\d{5}_\d{2})\s*</npcommid>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<string> ExtractNpCommIdsFromRawFile(
            string filePath,
            ILogger logger = null,
            long maxSearchBytes = 100L * 1024 * 1024)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return results;
            }

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ScanStream(stream, results, seen, maxSearchBytes);
                }
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"[RPCS3] Error scanning '{filePath}' for NPWR IDs");
            }

            return results;
        }

        public static string ExtractFirstNpCommIdFromRawFile(string filePath, ILogger logger = null)
        {
            var ids = ExtractNpCommIdsFromRawFile(filePath, logger);
            return ids.Count > 0 ? ids[0] : null;
        }

        /// <summary>
        /// Scans an already-open stream for the first NPWR id.
        /// Tolerates binary content (e.g. a TROPHY.TRP container read from a disc image).
        /// </summary>
        public static string ExtractFirstNpCommIdFromStream(
            Stream stream,
            ILogger logger = null,
            long maxSearchBytes = 8L * 1024 * 1024)
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (stream == null)
            {
                return null;
            }

            try
            {
                ScanStream(stream, results, seen, maxSearchBytes, stopAfterFirst: true);
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "[RPCS3] Error scanning stream for NPWR IDs");
            }

            return results.Count > 0 ? results[0] : null;
        }

        private static void ScanStream(
            Stream stream,
            List<string> results,
            HashSet<string> seen,
            long maxSearchBytes,
            bool stopAfterFirst = false)
        {
            var searchLimit = stream.CanSeek ? Math.Min(stream.Length, maxSearchBytes) : maxSearchBytes;
            var buffer = new byte[64 * 1024];
            var overlap = string.Empty;
            long position = 0;

            while (position < searchLimit)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, searchLimit - position);
                var bytesRead = stream.Read(buffer, 0, bytesToRead);
                if (bytesRead <= 0)
                {
                    break;
                }

                var chunk = overlap + Encoding.ASCII.GetString(buffer, 0, bytesRead);
                AddMatches(chunk, results, seen);
                if (stopAfterFirst && results.Count > 0)
                {
                    return;
                }

                overlap = chunk.Length > 512 ? chunk.Substring(chunk.Length - 512) : chunk;

                position += bytesRead;
            }
        }

        private static void AddMatches(string value, List<string> results, HashSet<string> seen)
        {
            foreach (Match match in NpCommIdElementPattern.Matches(value ?? string.Empty))
            {
                var normalized = Rpcs3MatchIdHelper.Normalize(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    results.Add(normalized);
                }
            }
        }
    }
}
