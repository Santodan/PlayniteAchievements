using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class ArcadeFilenameHasher : IRaHasher
    {
        private const int NeoHeaderSize = 4096;
        private static readonly byte[] NeoMagic = { (byte)'N', (byte)'E', (byte)'O', 1 };

        public string Name => "Arcade (MD5 of filename)";

        public bool SupportsForwardOnlyInput => true;

        public Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;

            // .neo files (Geolith Neo Geo carts) hold the ROM data and are content-hashed (hash.c:883-886).
            if (string.Equals(Path.GetExtension(filePath), ".neo", StringComparison.OrdinalIgnoreCase))
            {
                return HashNeoCartAsync(source, cancel);
            }

            var hash = HashUtils.ComputeMd5Hex(BuildHashInput(filePath));
            return Task.FromResult<IReadOnlyList<string>>(new[] { hash });
        }

        /// <summary>
        /// rc_hash_arcade (hash_rom.c:32-143): the file name without its extension, prefixed with
        /// "folder_" for fbneo subsystem folders when the result fits the 128-byte buffer.
        /// A name with no extension (an extracted romset folder) is hashed whole.
        /// </summary>
        internal static byte[] BuildHashInput(string filePath)
        {
            var filename = GetFileName(filePath);
            var dot = filename.LastIndexOf('.');
            if (dot < 0)
            {
                return Encoding.UTF8.GetBytes(filename);
            }

            var nameBytes = Encoding.UTF8.GetBytes(filename.Substring(0, dot));
            var folderEnd = filePath.Length - filename.Length - 1;
            if (folderEnd > 0)
            {
                var folderStart = folderEnd;
                while (folderStart > 0 && filePath[folderStart - 1] != '/' && filePath[folderStart - 1] != '\\')
                {
                    folderStart--;
                }

                var folder = filePath.Substring(folderStart, folderEnd - folderStart);
                if (folder.Length < 16 && ShouldIncludeFolder(folder.ToLowerInvariant()))
                {
                    var folderBytes = Encoding.UTF8.GetBytes(folder.ToLowerInvariant());
                    if (folderBytes.Length + nameBytes.Length + 1 < 128)
                    {
                        var combined = new byte[folderBytes.Length + 1 + nameBytes.Length];
                        Buffer.BlockCopy(folderBytes, 0, combined, 0, folderBytes.Length);
                        combined[folderBytes.Length] = (byte)'_';
                        Buffer.BlockCopy(nameBytes, 0, combined, folderBytes.Length + 1, nameBytes.Length);
                        return combined;
                    }
                }
            }

            return nameBytes;
        }

        // rc_path_get_filename (hash.c:309-320).
        private static string GetFileName(string path)
        {
            var cut = path.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        /// <summary>
        /// rc_hash_neogeo_cart (hash_rom.c:311-395): the ROM data after the 4096-byte header,
        /// capped at 64 MB. No hash without the "NEO\1" magic or with no data after the header.
        /// </summary>
        private static async Task<IReadOnlyList<string>> HashNeoCartAsync(RaHashSource source, CancellationToken cancel)
        {
            using (var stream = source.Open())
            {
                var magic = new byte[NeoMagic.Length];
                if (HashUtils.ReadFull(stream, magic, 0, magic.Length) != magic.Length ||
                    !HashUtils.StartsWith(magic, NeoMagic) ||
                    source.Length <= NeoHeaderSize)
                {
                    return Array.Empty<string>();
                }

                HashUtils.Skip(stream, NeoHeaderSize - magic.Length);
                using (var md5 = MD5.Create())
                {
                    await HashUtils.AppendStreamAsync(md5, stream, source.Length - NeoHeaderSize, cancel).ConfigureAwait(false);
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new[] { HashUtils.ToHexLower(md5.Hash) };
                }
            }
        }

        private static bool ShouldIncludeFolder(string folder)
        {
            switch (folder.Length)
            {
                case 3:
                    return folder == "nes" || folder == "fds" || folder == "sms" || folder == "msx" ||
                           folder == "ngp" || folder == "pce" || folder == "chf" || folder == "sgx";
                case 4:
                    return folder == "tg16" || folder == "msx1";
                case 5:
                    return folder == "neocd";
                case 6:
                    return folder == "coleco" || folder == "sg1000";
                case 7:
                    return folder == "genesis";
                case 8:
                    return folder == "gamegear" || folder == "megadriv" || folder == "pcengine" || folder == "channelf" || folder == "spectrum";
                case 9:
                    return folder == "megadrive";
                case 10:
                    return folder == "supergrafx" || folder == "zxspectrum";
                case 12:
                    return folder == "mastersystem" || folder == "colecovision";
                default:
                    return false;
            }
        }
    }
}
