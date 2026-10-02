using System;
using System.Collections.Generic;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal enum RaHashMethodKind
    {
        Md5WholeFile,
        MultiDiskMd5,
        HeaderMagicSkip,
        HeaderSizeModSkip,
        N64EndianSwap,
        ArcadeFilename,
        ArduboyHexNormalize,
        NintendoDs,
        Psx,
        Ps2,
        Psp,
        PceCd,
        PcFxCd,
        Dreamcast,
        SegaCdSaturn,
        GameCube,
        NeoGeoCd,
        AtariJaguarCd,
        ThreeDo
    }

    internal sealed class RaConsoleHashingSpecEntry
    {
        public RaHashMethodKind Kind { get; set; }

        public IReadOnlyList<byte[]> MagicPrefixes { get; set; }
        public int SkipBytes { get; set; }
        public int MagicOffset { get; set; }

        public int? SizeModuloBytes { get; set; }
        public int? SizeRemainderBytes { get; set; }
        public int? SizeBitFlagBytes { get; set; }
    }

    internal static class RaConsoleHashingSpec
    {
        private static readonly byte[] NesMagic = Encoding.ASCII.GetBytes("NES\x1a");
        private static readonly byte[] FdsMagic = Encoding.ASCII.GetBytes("FDS\x1a");
        private static readonly byte[] LynxMagic = Encoding.ASCII.GetBytes("LYNX\0");
        private static readonly byte[] Atari7800Magic = Encoding.ASCII.GetBytes("ATARI7800");
        private static readonly byte[] ScvMagic = Encoding.ASCII.GetBytes("EmuSCV");

        private static readonly Dictionary<int, RaConsoleHashingSpecEntry> Spec =
            new Dictionary<int, RaConsoleHashingSpecEntry>
            {
                // Special cases per RetroAchievements hashing rules.
                [2] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.N64EndianSwap },
                [7] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderMagicSkip, MagicPrefixes = new[] { NesMagic, FdsMagic }, SkipBytes = 16 },
                [3] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderSizeModSkip, SkipBytes = 512, SizeModuloBytes = 0x2000, SizeRemainderBytes = 512 },
                [8] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderSizeModSkip, SkipBytes = 512, SizeBitFlagBytes = 512 },
                [13] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderMagicSkip, MagicPrefixes = new[] { LynxMagic }, SkipBytes = 64 },
                [51] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderMagicSkip, MagicPrefixes = new[] { Atari7800Magic }, SkipBytes = 128, MagicOffset = 1 }, // hash_rom.c:24, byte 0 is not checked
                [55] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderMagicSkip, MagicPrefixes = new[] { ScvMagic }, SkipBytes = 32 }, // hash_rom.c:505

                [27] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.ArcadeFilename },
                [71] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.ArduboyHexNormalize },
                [18] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.NintendoDs },
                [78] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.NintendoDs },

                [12] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.Psx },
                [21] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.Ps2 },
                [41] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.Psp },

                [9] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.SegaCdSaturn },
                [39] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.SegaCdSaturn },
                [40] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.Dreamcast },
                [16] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.GameCube },
                [76] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.PceCd },
                [49] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.PcFxCd },
                [43] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.ThreeDo },
                [56] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.NeoGeoCd },
                [77] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.AtariJaguarCd },

                // Multi-disk (hash each disk)
                [29] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.MultiDiskMd5 }, // MSX / MSX2
                [37] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.MultiDiskMd5 }, // Amstrad CPC
                [38] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.MultiDiskMd5 }, // Apple II
                [47] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.MultiDiskMd5 }, // PC-8001 / PC-8801

                // Famicom Disk System (separate console id)
                [81] = new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.HeaderMagicSkip, MagicPrefixes = new[] { NesMagic, FdsMagic }, SkipBytes = 16 }
            };

        public static RaConsoleHashingSpecEntry Get(int consoleId)
        {
            return Spec.TryGetValue(consoleId, out var spec)
                ? spec
                : new RaConsoleHashingSpecEntry { Kind = RaHashMethodKind.Md5WholeFile };
        }
    }
}

