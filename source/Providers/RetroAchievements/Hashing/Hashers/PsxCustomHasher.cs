using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    /// <summary>rc_hash_psx (hash_disc.c:928-979).</summary>
    internal sealed class PsxCustomHasher : DiscBasedHasher
    {
        private const string FallbackExecutable = "PSX.EXE";

        public PsxCustomHasher(ILogger logger) : base(logger) { }

        public override string Name => "PlayStation (SYSTEM.CNF BOOT + executable MD5)";

        protected override async Task<IReadOnlyList<string>> ComputeHashesInternalAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            using (var iso = new DiscUtilsFacade(source))
            {
                // Fall back to PSX.EXE when SYSTEM.CNF is missing, has no BOOT line, or names a missing file.
                var exeName = FindBootExecutableName(iso, "BOOT", "cdrom:");
                long start = 0;
                long length = 0;
                if (exeName == null || !iso.TryGetFileExtent(exeName, out start, out length))
                {
                    exeName = FallbackExecutable;
                    if (!iso.TryGetFileExtent(exeName, out start, out length))
                    {
                        WarnOnce($"[RA] {Name}: Could not locate primary executable: {filePath}");
                        return Array.Empty<string>();
                    }
                }

                var header = new byte[32];
                if (iso.ReadAt(start, header, header.Length) < header.Length)
                {
                    return Array.Empty<string>();
                }

                // The PS-X EXE header stores the executable size at offset 28, excluding the 2048-byte header.
                var size = (uint)length;
                if (HashUtils.MatchesAt(header, 0, Encoding.ASCII.GetBytes("PS-X EX")))
                {
                    size = unchecked(HashUtils.ReadUInt32LE(header, 28) + 2048u);
                }

                using (var md5 = MD5.Create())
                {
                    // Some games share one engine and differ only by the boot file name, so it is hashed too.
                    var exeNameBytes = Encoding.ASCII.GetBytes(exeName);
                    md5.TransformBlock(exeNameBytes, 0, exeNameBytes.Length, null, 0);

                    if (!await iso.AppendFileAsync(md5, start, size, cancel).ConfigureAwait(false))
                    {
                        return Array.Empty<string>();
                    }

                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }

        /// <summary>
        /// rc_hash_find_playstation_executable (hash_disc.c:866-926): the boot file named by the
        /// first line of SYSTEM.CNF that starts with <paramref name="bootKey"/> followed by '=',
        /// with <paramref name="cdromPrefix"/> and leading backslashes removed and anything from
        /// ';' or whitespace on dropped. Null when SYSTEM.CNF or the line is missing.
        /// </summary>
        internal static string FindBootExecutableName(DiscUtilsFacade iso, string bootKey, string cdromPrefix)
        {
            if (!iso.TryGetFileExtent("SYSTEM.CNF", out var start, out _))
            {
                return null;
            }

            // rcheevos reads the first sector of SYSTEM.CNF, less one byte for a terminator.
            var buffer = new byte[DiscTrack.CookedSectorSize - 1];
            var length = iso.ReadAt(start, buffer, buffer.Length);

            var key = Encoding.ASCII.GetBytes(bootKey);
            var prefix = Encoding.ASCII.GetBytes(cdromPrefix);
            var end = Array.IndexOf(buffer, (byte)0, 0, length);
            if (end < 0)
            {
                end = length;
            }

            var ptr = 0;
            while (ptr < end)
            {
                if (MatchesAt(buffer, ptr, end, key))
                {
                    ptr += key.Length;
                    while (ptr < end && IsSpace(buffer[ptr]))
                    {
                        ptr++;
                    }

                    if (ptr < end && buffer[ptr] == '=')
                    {
                        ptr++;
                        while (ptr < end && IsSpace(buffer[ptr]))
                        {
                            ptr++;
                        }

                        if (MatchesAt(buffer, ptr, end, prefix))
                        {
                            ptr += prefix.Length;
                        }

                        while (ptr < end && buffer[ptr] == '\\')
                        {
                            ptr++;
                        }

                        var nameStart = ptr;
                        while (ptr < end && !IsSpace(buffer[ptr]) && buffer[ptr] != ';')
                        {
                            ptr++;
                        }

                        // exe_name is a 64-byte buffer.
                        var nameLength = Math.Min(ptr - nameStart, 63);
                        return nameLength > 0 ? Encoding.ASCII.GetString(buffer, nameStart, nameLength) : null;
                    }
                }

                // Advance to the start of the next line.
                while (ptr < end && buffer[ptr] != '\n')
                {
                    ptr++;
                }

                ptr++;
            }

            return null;
        }

        private static bool MatchesAt(byte[] buffer, int offset, int end, byte[] expected)
        {
            if (offset + expected.Length > end)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (buffer[offset + i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSpace(byte b)
        {
            return b == ' ' || (b >= '\t' && b <= '\r');
        }
    }
}
