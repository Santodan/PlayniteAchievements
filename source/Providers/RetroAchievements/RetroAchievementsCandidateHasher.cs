using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// Turns one candidate file into RA hashes, unpacking containers (CSO, RVZ, archives) as needed.
    /// Reuses hashes recorded for an unchanged file instead of reading it again, and stops at
    /// the first hash the caller accepts. Temporary files are released before this returns.
    /// </summary>
    internal sealed class RetroAchievementsCandidateHasher
    {
        /// <summary>
        /// Bump when any hasher's output can change for the same bytes, so recorded misses and
        /// partial records are recomputed. 1: rcheevos cdreader port and hasher parity fixes.
        /// </summary>
        internal const int HashRulesVersion = 1;

        private readonly ILogger _logger;

        public RetroAchievementsCandidateHasher(ILogger logger)
        {
            _logger = logger;
        }

        internal sealed class CandidateResult
        {
            public RaHashCacheCandidate Record { get; set; }
            public string MatchedHash { get; set; }
            public bool FromCache { get; set; }
        }

        /// <summary>
        /// Returns the recorded hashes for <paramref name="candidate"/> when the file is unchanged,
        /// otherwise hashes it. <paramref name="isMatch"/> is consulted as hashes are produced so
        /// archive scanning stops at the first matching entry.
        /// Returns null when the candidate does not exist or hit a transient read failure,
        /// in which case nothing should be recorded for it.
        /// </summary>
        public async Task<CandidateResult> HashAsync(
            string candidate,
            IRaHasher hasher,
            RetroAchievementsSettings raSettings,
            RaHashCacheEntry cachedEntry,
            Func<string, bool> isMatch,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(candidate) || hasher == null)
            {
                return null;
            }

            var cachedRecord = FindReusableRecord(cachedEntry, candidate, hasher.Name);
            if (cachedRecord != null)
            {
                var hit = cachedRecord.Hashes?.FirstOrDefault(h => isMatch(h));
                // A partial record without a match must be re-read: the unread entries may match.
                if (hit != null || cachedRecord.Complete)
                {
                    return new CandidateResult { Record = cachedRecord, MatchedHash = hit, FromCache = true };
                }
            }

            if (!File.Exists(candidate))
            {
                return null;
            }

            var record = new RaHashCacheCandidate
            {
                Path = candidate,
                HasherName = hasher.Name,
                Dependencies = RetroAchievementsHashCacheStore.CaptureDependencySnapshot(candidate),
                Hashes = new List<string>(),
                Complete = true,
                RulesVersion = HashRulesVersion
            };

            string matched;
            try
            {
                matched = await HashContainerAsync(candidate, hasher, raSettings, record, isMatch, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (HashUtils.IsTransientReadFailure(ex))
            {
                // Locked or unreadable right now; retry on the next refresh instead of recording a miss.
                _logger?.Warn(ex, $"[RA] Could not read '{candidate}': {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                // Deterministic for unchanged bytes (unsupported or malformed content): record the miss.
                _logger?.Warn(ex, $"[RA] Hashing failed for '{candidate}': {ex.Message}");
                matched = null;
            }

            return new CandidateResult { Record = record, MatchedHash = matched };
        }

        private static RaHashCacheCandidate FindReusableRecord(RaHashCacheEntry entry, string candidate, string hasherName)
        {
            var record = entry?.Candidates?.FirstOrDefault(c =>
                string.Equals(c?.Path, candidate, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c?.HasherName, hasherName, StringComparison.Ordinal));

            if (record == null || record.Hashes == null || record.RulesVersion != HashRulesVersion)
            {
                return null;
            }

            return RetroAchievementsHashCacheStore.ValidateDependencySnapshot(record.Dependencies) ? record : null;
        }

        private async Task<string> HashContainerAsync(
            string candidate,
            IRaHasher hasher,
            RetroAchievementsSettings raSettings,
            RaHashCacheCandidate record,
            Func<string, bool> isMatch,
            CancellationToken cancel)
        {
            var archiveScanning = raSettings?.EnableArchiveScanning == true;

            // CSO and RVZ images are read in place: only the blocks the hasher touches are decoded.
            if (ArchiveUtils.IsCsoPath(candidate) && archiveScanning)
            {
                using (var source = RaHashSource.FromSeekableStream(candidate, CsoUtils.OpenStream(candidate)))
                {
                    var hashes = await hasher.ComputeHashesAsync(source, cancel).ConfigureAwait(false);
                    return Record(record, hashes, isMatch, $"CSO file '{candidate}'");
                }
            }

            if (ArchiveUtils.IsRvzPath(candidate) && archiveScanning)
            {
                using (var source = RaHashSource.FromSeekableStream(candidate, RvzUtils.OpenStream(candidate)))
                {
                    var hashes = await hasher.ComputeHashesAsync(source, cancel).ConfigureAwait(false);
                    return Record(record, hashes, isMatch, $"RVZ file '{candidate}'");
                }
            }

            // Standard archive handling (zip, 7z, rar)
            if (ArchiveUtils.IsArchivePath(candidate) && archiveScanning)
            {
                // Arcade hashing is based on filename; no need to inspect entries.
                if (hasher is Hashing.Hashers.ArcadeFilenameHasher)
                {
                    var hashes = await hasher.ComputeHashesAsync(candidate, cancel).ConfigureAwait(false);
                    return Record(record, hashes, isMatch, $"Archive '{candidate}'");
                }

                // Entries are produced lazily; stopping at a match skips the rest of the archive.
                foreach (var input in ArchiveUtils.EnumerateHashInputs(candidate, hasher.SupportsForwardOnlyInput))
                {
                    cancel.ThrowIfCancellationRequested();

                    using (input)
                    {
                        var hashes = await hasher.ComputeHashesAsync(input.Source, cancel).ConfigureAwait(false);
                        var matched = Record(record, hashes, isMatch, $"ArchiveEntry '{input.EntryKey}'");
                        if (matched != null)
                        {
                            // Later entries were never read, so the recorded hashes may be partial.
                            record.Complete = false;
                            return matched;
                        }
                    }
                }

                return null;
            }

            var fileHashes = await hasher.ComputeHashesAsync(candidate, cancel).ConfigureAwait(false);
            return Record(record, fileHashes, isMatch, $"File '{candidate}'");
        }

        private string Record(RaHashCacheCandidate record, IReadOnlyList<string> hashes, Func<string, bool> isMatch, string label)
        {
            string matched = null;
            if (hashes != null)
            {
                foreach (var hash in hashes)
                {
                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        continue;
                    }

                    var key = hash.Trim().ToLowerInvariant();
                    record.Hashes.Add(key);
                    if (matched == null && isMatch(key))
                    {
                        matched = key;
                    }
                }
            }

            _logger?.Info($"[RA] {label} hashes={FormatHashesForLog(hashes)} matched={matched != null}");
            return matched;
        }

        private static string FormatHashesForLog(IReadOnlyList<string> hashes)
        {
            if (hashes == null || hashes.Count == 0) return "(none)";
            return string.Join(",", hashes.Select(h => string.IsNullOrWhiteSpace(h) ? "?" : h));
        }
    }
}
