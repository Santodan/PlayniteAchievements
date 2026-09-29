using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    /// <summary>
    /// rc_hash_generate_from_playlist (hash.c:713-803): an .m3u file on disk hashes as its first
    /// entry, through the console's own hasher. Every other input goes to that hasher unchanged.
    /// </summary>
    internal sealed class M3uPlaylistHasher : IRaHasher
    {
        private const int PlaylistReadBytes = 1023;

        private readonly IRaHasher _inner;

        public M3uPlaylistHasher(IRaHasher inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public string Name => _inner.Name;

        public bool SupportsForwardOnlyInput => _inner.SupportsForwardOnlyInput;

        public static bool IsM3uPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), ".m3u", StringComparison.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            if (source == null || !source.IsFile || !IsM3uPath(source.Path))
            {
                return _inner.ComputeHashesAsync(source, cancel);
            }

            var entry = GetFirstEntry(source.Path);
            if (entry == null || !File.Exists(entry))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            return _inner.ComputeHashesAsync(RaHashSource.FromFile(entry), cancel);
        }

        /// <summary>
        /// rc_hash_get_first_item_from_playlist (hash.c:713-781): the first line of the first 1023
        /// bytes that is neither empty, whitespace, nor a '#' comment, with trailing whitespace
        /// removed, resolved against the playlist's folder unless it is absolute.
        /// </summary>
        internal static string GetFirstEntry(string m3uPath)
        {
            byte[] data;
            using (var stream = new FileStream(m3uPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                data = new byte[PlaylistReadBytes];
                var read = HashUtils.ReadFull(stream, data, 0, data.Length);
                Array.Resize(ref data, read);
            }

            var text = Encoding.UTF8.GetString(data);
            var nul = text.IndexOf('\0');
            if (nul >= 0)
            {
                text = text.Substring(0, nul);
            }

            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text.Substring(1);
            }

            var ptr = 0;
            while (true)
            {
                // Skip comment and empty lines.
                while (ptr < text.Length && (text[ptr] == '#' || text[ptr] == '\r' || text[ptr] == '\n'))
                {
                    while (ptr < text.Length && text[ptr] != '\n')
                    {
                        ptr++;
                    }

                    if (ptr < text.Length)
                    {
                        ptr++;
                    }
                }

                var start = ptr;
                while (ptr < text.Length && text[ptr] != '\n')
                {
                    ptr++;
                }

                var next = ptr;
                while (ptr > start && IsSpace(text[ptr - 1]))
                {
                    ptr--;
                }

                if (ptr > start)
                {
                    return Resolve(m3uPath, text.Substring(start, ptr - start));
                }

                if (next >= text.Length)
                {
                    return null;
                }

                // The line held only whitespace.
                ptr = next + 1;
            }
        }

        private static string Resolve(string m3uPath, string entry)
        {
            if (IsAbsolute(entry))
            {
                return entry;
            }

            // rc_path_get_filename: everything through the last separator of the playlist path.
            var cut = m3uPath.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? m3uPath.Substring(0, cut + 1) + entry : entry;
        }

        // rc_hash_path_is_absolute (hash.c:689-711).
        private static bool IsAbsolute(string path)
        {
            if (path.Length == 0)
            {
                return false;
            }

            if (path[0] == '/' || path[0] == '\\')
            {
                return true;
            }

            if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
            {
                return true;
            }

            return path.IndexOf(":/", StringComparison.Ordinal) >= 0;
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || (c >= '\t' && c <= '\r');
        }
    }
}
