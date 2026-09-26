using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing.Hashers
{
    internal sealed class ArcadeFilenameHasher : IRaHasher
    {
        public string Name => "Arcade (MD5 of filename)";

        public bool SupportsForwardOnlyInput => true;

        public Task<IReadOnlyList<string>> ComputeHashesAsync(RaHashSource source, CancellationToken cancel)
        {
            var filePath = source.Path;
            var filename = Path.GetFileName(filePath);
            var nameNoExt = Path.GetFileNameWithoutExtension(filename);

            var dir = Path.GetDirectoryName(filePath);
            var parent = string.IsNullOrWhiteSpace(dir) ? string.Empty : Path.GetFileName(dir);

            string toHash;
            if (!string.IsNullOrWhiteSpace(parent) && ShouldIncludeFolder(parent))
            {
                toHash = $"{parent.ToLowerInvariant()}_{nameNoExt}";
            }
            else
            {
                toHash = nameNoExt;
            }

            var hash = HashUtils.ComputeMd5HexFromString(toHash);
            return Task.FromResult<IReadOnlyList<string>>(new[] { hash });
        }

        private static bool ShouldIncludeFolder(string folderLowerMaybe)
        {
            if (string.IsNullOrWhiteSpace(folderLowerMaybe)) return false;

            var folder = folderLowerMaybe.ToLowerInvariant();
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

