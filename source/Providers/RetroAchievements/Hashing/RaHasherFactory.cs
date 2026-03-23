using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using System;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal static class RaHasherFactory
    {
        public static IRaHasher Create(int consoleId, PlayniteAchievementsSettings settings, ILogger logger)
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
                    return new Hashers.HeaderMagicSkipHasher(spec.MagicPrefixes, spec.SkipBytes);
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






