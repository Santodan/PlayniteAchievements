using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class Ps2CustomHasher : DiscBasedHasher
    {
        public Ps2CustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PlayStation 2 (SYSTEM.CNF BOOT2 + executable MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            using (var iso = new DiscUtilsFacade(source))
            {
                var bootInfo = await FindBootExecutableAsync(iso, bootKey: "BOOT2", cdromPrefix: "cdrom0:", cancel).ConfigureAwait(false);
                if (bootInfo == null)
                {
                    WarnOnce($"[RA] {Name}: Could not locate primary executable via SYSTEM.CNF: {filePath}");
                    return Array.Empty<string>();
                }

                var executablePathCandidates = BuildExecutablePathCandidates(bootInfo).ToList();

                Stream exeStream = null;
                string openedExecutablePath = null;

                foreach (var candidate in executablePathCandidates)
                {
                    exeStream = iso.OpenFileOrNull(candidate);
                    if (exeStream != null)
                    {
                        openedExecutablePath = candidate;
                        break;
                    }
                }

                if (exeStream == null)
                {
                    WarnOnce($"[RA] {Name}: Could not locate primary executable '{bootInfo.CanonicalPath}': {filePath}");
                    return Array.Empty<string>();
                }

                using (exeStream)
                {
                    var maxBytesToHash = Math.Min((long)HashUtils.MaxHashBytes, exeStream.Length);
                    var bytesToHash = (int)Math.Max(0, maxBytesToHash);
                    var executableBytes = new byte[bytesToHash];

                    var read = await HashUtils.ReadExactlyAsync(exeStream, executableBytes, 0, bytesToHash, cancel).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        WarnOnce($"[RA] {Name}: Executable '{openedExecutablePath}' is empty: {filePath}");
                        return Array.Empty<string>();
                    }

                    var hashTitleCandidates = BuildHashTitleCandidates(bootInfo).ToList();
                    var hashes = new List<string>(hashTitleCandidates.Count);

                    foreach (var hashTitle in hashTitleCandidates)
                    {
                        using (var md5 = MD5.Create())
                        {
                            var titleBytes = Encoding.ASCII.GetBytes(hashTitle);
                            md5.TransformBlock(titleBytes, 0, titleBytes.Length, null, 0);
                            md5.TransformFinalBlock(executableBytes, 0, read);

                            var hash = HashUtils.ToHexLower(md5.Hash);
                            if (!string.IsNullOrWhiteSpace(hash))
                            {
                                hashes.Add(hash);
                            }
                        }
                    }

                    if (hashes.Count == 0)
                    {
                        return Array.Empty<string>();
                    }

                    return hashes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }

        private sealed class BootExecutableInfo
        {
            public string CanonicalPath { get; set; }
            public string CanonicalPathWithVersion { get; set; }
            public string FileNameOnly { get; set; }
            public string FileNameOnlyWithVersion { get; set; }
        }

        private static IEnumerable<string> BuildExecutablePathCandidates(BootExecutableInfo info)
        {
            if (info == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in new[]
            {
                info.CanonicalPath,
                info.CanonicalPathWithVersion,
                info.FileNameOnly,
                info.FileNameOnlyWithVersion
            })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var normalized = candidate.Trim().TrimStart('\\');
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static IEnumerable<string> BuildHashTitleCandidates(BootExecutableInfo info)
        {
            if (info == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in new[]
            {
                info.CanonicalPath,
                info.CanonicalPathWithVersion,
                info.FileNameOnly,
                info.FileNameOnlyWithVersion
            })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var normalized = candidate.Trim().TrimStart('\\');
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static async Task<BootExecutableInfo> FindBootExecutableAsync(DiscUtilsFacade iso, string bootKey, string cdromPrefix, CancellationToken cancel)
        {
            using (var cnfStream = iso.OpenFileOrNull("SYSTEM.CNF"))
            {
                if (cnfStream == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(cnfStream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
                {
                    var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();

                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var raw in lines)
                    {
                        var line = raw.Trim();
                        if (!line.StartsWith(bootKey, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var rest = line.Substring(bootKey.Length).TrimStart();
                        if (!rest.StartsWith("=", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        rest = rest.Substring(1).TrimStart();
                        var end = rest.IndexOfAny(new[] { ' ', '\t' });
                        var bootValue = end >= 0 ? rest.Substring(0, end) : rest;
                        if (string.IsNullOrWhiteSpace(bootValue))
                        {
                            continue;
                        }

                        var bootPath = bootValue.Trim();
                        if (bootPath.StartsWith(cdromPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            bootPath = bootPath.Substring(cdromPrefix.Length);
                        }

                        while (bootPath.StartsWith("\\", StringComparison.Ordinal))
                        {
                            bootPath = bootPath.Substring(1);
                        }

                        var bootPathWithVersion = bootPath;
                        var semicolonIndex = bootPath.IndexOf(';');
                        if (semicolonIndex >= 0)
                        {
                            bootPath = bootPath.Substring(0, semicolonIndex);
                        }

                        if (string.IsNullOrWhiteSpace(bootPath))
                        {
                            continue;
                        }

                        var fileNameOnly = bootPath.Replace('\\', '/');
                        var slashIndex = fileNameOnly.LastIndexOf('/');
                        if (slashIndex >= 0 && slashIndex + 1 < fileNameOnly.Length)
                        {
                            fileNameOnly = fileNameOnly.Substring(slashIndex + 1);
                        }

                        var fileNameOnlyWithVersion = bootPathWithVersion.Replace('\\', '/');
                        slashIndex = fileNameOnlyWithVersion.LastIndexOf('/');
                        if (slashIndex >= 0 && slashIndex + 1 < fileNameOnlyWithVersion.Length)
                        {
                            fileNameOnlyWithVersion = fileNameOnlyWithVersion.Substring(slashIndex + 1);
                        }

                        return new BootExecutableInfo
                        {
                            CanonicalPath = bootPath,
                            CanonicalPathWithVersion = string.IsNullOrWhiteSpace(bootPathWithVersion) ? null : bootPathWithVersion,
                            FileNameOnly = string.IsNullOrWhiteSpace(fileNameOnly) ? bootPath : fileNameOnly,
                            FileNameOnlyWithVersion = string.IsNullOrWhiteSpace(fileNameOnlyWithVersion) ? bootPathWithVersion : fileNameOnlyWithVersion
                        };
                    }
                }
            }

            return null;
        }
    }
}
