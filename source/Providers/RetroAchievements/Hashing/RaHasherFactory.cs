using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal static class RaHasherFactory
    {
        // Consoles whose .m3u playlists hash as their first entry (hash.c:845-977). The multi-disk
        // consoles (29, 37, 38, 47) hash every entry in MultiDiskMd5Hasher instead.
        private static readonly HashSet<int> FirstEntryPlaylistConsoles = new HashSet<int>
        {
            1,  // Mega Drive
            9,  // Sega CD
            12, // PlayStation
            21, // PlayStation 2
            30, // Commodore 64
            39, // Saturn
            40, // Dreamcast
            43, // 3DO
            49, // PC-FX
            76  // PC Engine CD
        };

        public static IRaHasher Create(int consoleId, PlayniteAchievementsSettings settings, ILogger logger)
        {
            var hasher = CreateForConsole(consoleId, settings, logger);
            return hasher != null && FirstEntryPlaylistConsoles.Contains(consoleId)
                ? new Hashers.M3uPlaylistHasher(hasher)
                : hasher;
        }

        private static IRaHasher CreateForConsole(int consoleId, PlayniteAchievementsSettings settings, ILogger logger)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var raSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            var spec = RaConsoleHashingSpec.Get(consoleId);

            switch (spec.Kind)
            {
                case RaHashMethodKind.Md5WholeFile:
                    return new Hashers.Md5FullFileHasher();
                case RaHashMethodKind.MultiDiskMd5:
                    return new Hashers.MultiDiskMd5Hasher();
                case RaHashMethodKind.HeaderMagicSkip:
                    return new Hashers.HeaderMagicSkipHasher(spec.MagicPrefixes, spec.SkipBytes, spec.MagicOffset);
                case RaHashMethodKind.HeaderSizeModSkip:
                    return new Hashers.HeaderSizeModSkipHasher(spec.SkipBytes, spec.SizeModuloBytes, spec.SizeRemainderBytes, spec.SizeBitFlagBytes);
                case RaHashMethodKind.N64EndianSwap:
                    return new Hashers.N64EndianSwapHasher();
                case RaHashMethodKind.ArcadeFilename:
                    return new Hashers.ArcadeFilenameHasher();
                case RaHashMethodKind.ArduboyHexNormalize:
                    return new Hashers.ArduboyHexNormalizeHasher();
                case RaHashMethodKind.NintendoDs:
                    return new Hashers.NdsCustomHasher();
                case RaHashMethodKind.Psx:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.PsxCustomHasher(logger) : null;
                case RaHashMethodKind.Ps2:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.Ps2CustomHasher(logger) : null;
                case RaHashMethodKind.Psp:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.PspCustomHasher(logger) : null;
                case RaHashMethodKind.PceCd:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.PceCdCustomHasher(logger) : null;
                case RaHashMethodKind.PcFxCd:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.PcFxCustomHasher(logger) : null;
                case RaHashMethodKind.Dreamcast:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.DreamcastCustomHasher(logger) : null;
                case RaHashMethodKind.SegaCdSaturn:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.SegaCdSaturnCustomHasher(logger) : null;
                case RaHashMethodKind.GameCube:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.GameCubeCustomHasher(logger) : null;
                case RaHashMethodKind.NeoGeoCd:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.NeoGeoCdCustomHasher(logger) : null;
                case RaHashMethodKind.AtariJaguarCd:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.AtariJaguarCdCustomHasher(logger) : null;
                case RaHashMethodKind.ThreeDo:
                    return raSettings.EnableDiscHashing ? (IRaHasher)new Hashers.ThreeDoCustomHasher(logger) : null;
                default:
                    return null;
            }
        }
    }
}






