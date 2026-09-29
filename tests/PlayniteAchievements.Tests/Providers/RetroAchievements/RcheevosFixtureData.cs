using System;
using System.Text;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// Byte-exact port of rcheevos test/rhash/data.c plus the local generators in
    /// test/rhash/test_hash_rom.c, so the golden MD5s from the rcheevos test suite apply unchanged.
    /// </summary>
    internal static class RcheevosFixtureData
    {
        // data.c: fill_image. size_t is unsigned 64-bit; the int seed keeps the low 32 bits.
        public static void FillImage(byte[] image, int offset, long size)
        {
            var remaining = (ulong)size;
            var seed = unchecked((int)(remaining ^ (remaining >> 8) ^ ((remaining - 1) * 25387)));
            var index = offset;

            while (remaining > 0)
            {
                int count;
                byte value;

                switch (seed & 0xFF)
                {
                    case 0:
                        count = (int)((ulong)((seed >> 8) & 0x3F) & ~(remaining & 0x0F));
                        if (count == 0)
                        {
                            count = 1;
                        }
                        value = 0;
                        break;
                    case 1:
                        count = ((seed >> 8) & 0x07) + 1;
                        value = (byte)((seed >> 16) & 0xFF);
                        break;
                    case 2:
                        count = ((seed >> 8) & 0x03) + 1;
                        value = (byte)(((seed >> 16) & 0xFF) ^ 0xFF);
                        break;
                    case 3:
                        count = ((seed >> 8) & 0x03) + 1;
                        value = (byte)(((seed >> 16) & 0xFF) ^ 0xA5);
                        break;
                    case 4:
                        count = ((seed >> 8) & 0x03) + 1;
                        value = (byte)(((seed >> 16) & 0xFF) ^ 0xC3);
                        break;
                    case 5:
                        count = ((seed >> 8) & 0x03) + 1;
                        value = (byte)(((seed >> 16) & 0xFF) ^ 0x96);
                        break;
                    case 6:
                    case 7:
                        count = ((seed >> 8) & 0x03) + 1;
                        value = (byte)(((seed >> 16) & 0xFF) ^ 0x78);
                        break;
                    default:
                        count = 1;
                        value = (byte)(((seed >> 8) ^ (seed >> 16)) & 0xFF);
                        break;
                }

                do
                {
                    image[index++] = value;
                    --remaining;
                } while (remaining != 0 && --count != 0);

                seed = unchecked(seed * 0x41C64E6D + 12345) & 0x7FFFFFFF;
            }
        }

        // data.c: generate_generic_file
        public static byte[] GenerateGenericFile(int size)
        {
            var image = new byte[size];
            FillImage(image, 0, size);
            return image;
        }

        // data.c: generate_gamecube_iso
        public static byte[] GenerateGameCubeIso(int mb)
        {
            var sizeNeeded = mb * 1024 * 1024;
            var image = new byte[sizeNeeded];
            const int apploaderSizesAddr = 0x2440 + 0x14;
            const int dolOffsetAddr = 0x420;
            const int dolSizesAddr = 0x3000;

            FillImage(image, 0, sizeNeeded);

            image[0x1c] = 0xC2;
            image[0x1d] = 0x33;
            image[0x1e] = 0x9F;
            image[0x1f] = 0x3D;

            for (var ix = 0; ix < 8; ix++)
            {
                image[apploaderSizesAddr + ix] = (byte)(ix % 4 == 3 ? 0xff : 0);
            }
            for (var ix = 0; ix < 4; ix++)
            {
                image[dolOffsetAddr + ix] = (byte)(ix % 4 == 2 ? 0x30 : 0);
            }
            for (var ix = 0; ix < 18 * 4; ix++)
            {
                image[dolSizesAddr + ix] = (byte)(ix % 4 == 2 ? 0x30 + 1 + ix / 4 : 0);
                image[dolSizesAddr + 0x90 + ix] = (byte)(ix % 8 == 3 ? 0xff : 0);
            }

            return image;
        }

        // data.c: generate_3do_bin
        public static byte[] Generate3doBin(int rootDirectorySectors, int binarySize)
        {
            var volumeHeader = new byte[]
            {
                0x01, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x01, 0x00,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                (byte)'C', (byte)'D', (byte)'-', (byte)'R', (byte)'O', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x2D, 0x79, 0x6E, 0x96,
                0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x05, 0x00,
                0x31, 0x5a, 0xf2, 0xe6,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x00, 0x06,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
            };

            var directoryData = new byte[]
            {
                0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0xA4,
                0x00, 0x00, 0x00, 0x14,

                0x00, 0x00, 0x00, 0x07,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                (byte)'f', (byte)'o', (byte)'l', (byte)'d', (byte)'e', (byte)'r', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,

                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                (byte)'L', (byte)'a', (byte)'u', (byte)'n', (byte)'c', (byte)'h', (byte)'M', (byte)'e', 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x02,
            };

            var sizeNeeded = (rootDirectorySectors + 1 + ((binarySize + 2047) / 2048)) * 2048;
            var image = new byte[sizeNeeded];
            var offset = 2048;

            Buffer.BlockCopy(volumeHeader, 0, image, 0, volumeHeader.Length);
            image[0x5B] = (byte)rootDirectorySectors;

            for (var i = 0; i < rootDirectorySectors; ++i)
            {
                Buffer.BlockCopy(directoryData, 0, image, offset, directoryData.Length);
                if (i < rootDirectorySectors - 1)
                {
                    image[offset + 0] = 0;
                    image[offset + 1] = 0;
                    image[offset + 2] = 0;
                    image[offset + 3] = (byte)(i + 1);

                    CopyAscii("filename", image, offset + 0x14 + 0x48 + 0x20);
                }
                else
                {
                    image[offset + 0x14 + 0x48 + 0x11] = (byte)((binarySize >> 16) & 0xFF);
                    image[offset + 0x14 + 0x48 + 0x12] = (byte)((binarySize >> 8) & 0xFF);
                    image[offset + 0x14 + 0x48 + 0x13] = (byte)(binarySize & 0xFF);

                    image[offset + 0x14 + 0x48 + 0x16] = (byte)((((binarySize + 2047) / 2048) >> 8) & 0xFF);
                    image[offset + 0x14 + 0x48 + 0x17] = (byte)(((binarySize + 2047) / 2048) & 0xFF);

                    image[offset + 0x14 + 0x48 + 0x47] = (byte)(i + 2);
                }

                if (i > 0)
                {
                    image[offset + 4] = 0;
                    image[offset + 5] = 0;
                    image[offset + 6] = 0;
                    image[offset + 7] = (byte)(i - 1);
                }

                offset += 2048;
            }

            FillImage(image, offset, binarySize);
            return image;
        }

        // data.c: generate_dreamcast_bin. The C string literal header includes its NUL (257 bytes).
        public static byte[] GenerateDreamcastBin(int trackFirstSector, int binarySize)
        {
            var volumeHeader = Encoding.ASCII.GetBytes(
                "SEGA SEGAKATANA " +
                "SEGA ENTERPRISES" +
                "5966 GD-ROM1/1  " +
                " U      918FA01 " +
                "X-1234N   V1.001" +
                "20200910        " +
                "1ST_READ.BIN    " +
                "RETROACHIEVEMENT" +
                "UNIT TEST       " +
                "                " +
                "                " +
                "                " +
                "                " +
                "                " +
                "                " +
                "                " +
                "\0");

            var directoryData = new byte[]
            {
                0x30,
                0x00,
                0xD9, 0xAF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x0E,
                (byte)'1', (byte)'S', (byte)'T', (byte)'_', (byte)'R', (byte)'E', (byte)'A', (byte)'D', (byte)'.', (byte)'B', (byte)'I', (byte)'N', (byte)';', (byte)'1',
            };

            var binarySectors = (binarySize + 2047) / 2048;
            var sizeNeeded = (binarySectors + 18) * 2048;
            var image = new byte[sizeNeeded];

            Buffer.BlockCopy(volumeHeader, 0, image, 0, volumeHeader.Length);

            CopyAscii("1CD001", image, 2048 * 16);
            image[2048 * 16 + 156 + 2] = 45017 & 0xFF;
            image[2048 * 16 + 156 + 3] = (45017 >> 8) & 0xFF;
            image[2048 * 16 + 156 + 4] = (45017 >> 16) & 0xFF;
            Buffer.BlockCopy(directoryData, 0, image, 2048 * 17, directoryData.Length);

            trackFirstSector += 18;
            image[2048 * 17 + 2] = (byte)(trackFirstSector & 0xFF);
            image[2048 * 17 + 3] = (byte)((trackFirstSector >> 8) & 0xFF);
            image[2048 * 17 + 4] = (byte)((trackFirstSector >> 16) & 0xFF);
            image[2048 * 17 + 10] = (byte)(binarySize & 0xFF);
            image[2048 * 17 + 11] = (byte)((binarySize >> 8) & 0xFF);
            image[2048 * 17 + 12] = (byte)((binarySize >> 16) & 0xFF);
            image[2048 * 17 + 13] = (byte)((binarySize >> 24) & 0xFF);

            FillImage(image, 2048 * 18, binarySectors * 2048);
            return image;
        }

        // data.c: generate_pce_cd_bin
        public static byte[] GeneratePceCdBin(int binarySectors)
        {
            var volumeHeader = new byte[]
            {
                0x00, 0x00, 0x02,
                0x14,
                0x00, 0x40,
                0x00, 0x40,
                0, 1, 2, 3, 4,
                0,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0,
            };
            var text = Encoding.ASCII.GetBytes(
                "PC Engine CD-ROM SYSTEM\0Copyright HUDSON SOFT / NEC Home Electronics,Ltd.\0GAMENAME" + new string(' ', 14));

            var sizeNeeded = (binarySectors + 2) * 2048;
            var image = new byte[sizeNeeded];

            Buffer.BlockCopy(volumeHeader, 0, image, 2048, volumeHeader.Length);
            Buffer.BlockCopy(text, 0, image, 2048 + volumeHeader.Length, text.Length);
            image[2048 + 0x03] = (byte)binarySectors;

            FillImage(image, 4096, binarySectors * 2048);
            return image;
        }

        // data.c: generate_pcfx_bin
        public static byte[] GeneratePcfxBin(int binarySectors)
        {
            var volumeHeader = new byte[]
            {
                (byte)'G', (byte)'A', (byte)'M', (byte)'E', (byte)'N', (byte)'A', (byte)'M', (byte)'E', 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x02, 0x00, 0x00, 0x00,
                0x14, 0x00, 0x00, 0x00,
                0x00, 0x80, 0x00, 0x00,
                0x00, 0x80, 0x00, 0x00,
                (byte)'N', (byte)'/', (byte)'A', 0,
                (byte)'r', (byte)'c', (byte)'h', (byte)'e', (byte)'e', (byte)'v', (byte)'o', (byte)'s', (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x01,
                0x01, 0x00,
                (byte)'2', (byte)'0', (byte)'2', (byte)'0', (byte)'X', (byte)'X', (byte)'X', (byte)'X',
            };

            var sizeNeeded = (binarySectors + 2) * 2048;
            var image = new byte[sizeNeeded];

            CopyAscii("PC-FX:Hu_CD-ROM\0", image, 0);
            Buffer.BlockCopy(volumeHeader, 0, image, 2048, volumeHeader.Length);
            image[2048 + 0x24] = (byte)binarySectors;

            FillImage(image, 4096, binarySectors * 2048);
            return image;
        }

        // data.c: generate_iso9660_bin. The big-endian sector count lands in sector 0 (image[84..87]), as in the C source.
        public static byte[] GenerateIso9660Bin(int numSectors, string volumeLabel)
        {
            var image = new byte[numSectors * 2048];
            const int vd = 16 * 2048;

            var identifier = new byte[] { 0x01, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01, 0x00 };
            Buffer.BlockCopy(identifier, 0, image, vd, identifier.Length);

            CopyAscii(volumeLabel, image, vd + 40);

            image[vd + 80] = image[87] = (byte)(numSectors & 0xFF);
            image[vd + 81] = image[86] = (byte)((numSectors >> 8) & 0xFF);
            image[vd + 82] = image[85] = (byte)((numSectors >> 16) & 0xFF);
            image[vd + 83] = image[84] = (byte)((numSectors >> 24) & 0xFF);

            image[vd + 128] = 2048 & 0xFF;
            image[vd + 129] = (2048 >> 8) & 0xFF;

            image[vd + 158] = 17;

            image[17 * 2048 - 4] = 18;

            return image;
        }

        // data.c: generate_iso9660_file. Returns the offset of the file contents so callers can patch them.
        public static int GenerateIso9660File(byte[] image, string filename, byte[] contents, int contentsSize)
        {
            const int rootDirectoryRecordOffset = 17 * 2048;
            var entry = rootDirectoryRecordOffset;
            var nextFreeSector = image[rootDirectoryRecordOffset - 4] |
                                 (image[rootDirectoryRecordOffset - 3] << 8) |
                                 (image[rootDirectoryRecordOffset - 2] << 16);

            var name = Encoding.ASCII.GetBytes(filename);
            var pos = 0;
            if (name.Length > 0 && name[0] == '\\')
            {
                ++pos;
            }

            int separator;
            while (true)
            {
                separator = pos;
                while (separator < name.Length && name[separator] != '\\')
                {
                    ++separator;
                }

                if (separator >= name.Length)
                {
                    break;
                }

                var dirNameLength = separator - pos;
                var found = false;
                while (image[entry] != 0)
                {
                    if (image[entry + 25] != 0 &&
                        image[entry + 33 + dirNameLength] == 0 &&
                        BytesEqual(image, entry + 33, name, pos, dirNameLength))
                    {
                        var directorySector = image[entry + 2];
                        entry = directorySector * 2048;
                        found = true;
                        break;
                    }

                    entry += image[entry];
                }

                if (!found)
                {
                    image[entry + 0] = (byte)((dirNameLength & 0xFF) + 48);
                    image[entry + 2] = (byte)(nextFreeSector & 0xFF);
                    image[entry + 3] = (byte)((nextFreeSector >> 8) & 0xFF);
                    image[entry + 25] = 1;
                    image[entry + 32] = (byte)(dirNameLength & 0xFF);
                    Buffer.BlockCopy(name, pos, image, entry + 33, dirNameLength);
                    image[entry + 33 + dirNameLength] = 0;

                    entry = nextFreeSector * 2048;
                    nextFreeSector++;
                }

                pos = separator + 1;
            }

            while (image[entry] != 0)
            {
                entry += image[entry];
            }

            var filenameLength = separator - pos;
            image[entry + 0] = (byte)((filenameLength & 0xFF) + 48);

            image[entry + 2] = (byte)(nextFreeSector & 0xFF);
            image[entry + 3] = (byte)((nextFreeSector >> 8) & 0xFF);

            image[entry + 10] = (byte)(contentsSize & 0xFF);
            image[entry + 11] = (byte)((contentsSize >> 8) & 0xFF);
            image[entry + 12] = (byte)((contentsSize >> 16) & 0xFF);

            image[entry + 32] = (byte)((filenameLength + 2) & 0xFF);
            Buffer.BlockCopy(name, pos, image, entry + 33, filenameLength);
            image[entry + 33 + filenameLength] = (byte)';';
            image[entry + 34 + filenameLength] = (byte)'1';

            var contentsOffset = nextFreeSector * 2048;
            if (contents != null)
            {
                Buffer.BlockCopy(contents, 0, image, contentsOffset, contentsSize);
            }
            else
            {
                FillImage(image, contentsOffset, contentsSize);
            }

            nextFreeSector += (contentsSize + 2047) / 2048;
            image[rootDirectoryRecordOffset - 4] = (byte)(nextFreeSector & 0xFF);
            image[rootDirectoryRecordOffset - 3] = (byte)((nextFreeSector >> 8) & 0xFF);
            image[rootDirectoryRecordOffset - 2] = (byte)((nextFreeSector >> 16) & 0xFF);

            return contentsOffset;
        }

        // data.c: generate_jaguarcd_bin
        public static byte[] GenerateJaguarCdBin(int headerOffset, int binarySize, bool byteswapped)
        {
            var sizeNeeded = (((binarySize + 64 + 32 + 8) + 2351) / 2352) * 2352;
            var image = new byte[sizeNeeded];

            for (var i = 0; i < 64; i += 4)
            {
                CopyAscii("ATRI", image, headerOffset + i);
            }
            CopyAscii("ATARI APPROVED DATA HEADER ATRI ", image, headerOffset + 64);
            image[headerOffset + 64 + 32 + 2] = 0xA0;
            image[headerOffset + 64 + 32 + 4 + 1] = (byte)(binarySize >> 16);
            image[headerOffset + 64 + 32 + 4 + 2] = (byte)((binarySize >> 8) & 0xFF);
            image[headerOffset + 64 + 32 + 4 + 3] = (byte)(binarySize & 0xFF);

            var dataStart = headerOffset + 64 + 32 + 8;
            FillImage(image, dataStart, sizeNeeded - dataStart);

            if (byteswapped)
            {
                for (var i = 0; i < sizeNeeded; i += 2)
                {
                    var tmp = image[i];
                    image[i] = image[i + 1];
                    image[i + 1] = tmp;
                }
            }

            return image;
        }

        // data.c: generate_psx_bin
        public static byte[] GeneratePsxBin(string binaryName, int binarySize)
        {
            var sectorsNeeded = ((binarySize + 2047) / 2048) + 20;
            var systemCnf = "BOOT=cdrom:\\" + binaryName + ";1\nTCB=4\nEVENT=10\nSTACK=801FFFF0\n";
            var cnfBytes = Encoding.ASCII.GetBytes(systemCnf);

            var image = GenerateIso9660Bin(sectorsNeeded, "TEST");
            GenerateIso9660File(image, "SYSTEM.CNF", cnfBytes, cnfBytes.Length);

            var exe = GenerateIso9660File(image, binaryName, null, binarySize);
            CopyAscii("PS-X EXE", image, exe);

            binarySize -= 2048;
            image[exe + 28] = (byte)(binarySize & 0xFF);
            image[exe + 29] = (byte)((binarySize >> 8) & 0xFF);
            image[exe + 30] = (byte)((binarySize >> 16) & 0xFF);
            image[exe + 31] = (byte)((binarySize >> 24) & 0xFF);

            return image;
        }

        // data.c: generate_ps2_bin
        public static byte[] GeneratePs2Bin(string binaryName, int binarySize)
        {
            var sectorsNeeded = ((binarySize + 2047) / 2048) + 20;
            var systemCnf = "BOOT2 = cdrom0:\\" + binaryName + ";1\nVER = 1.0\nVMODE = NTSC\n";
            var cnfBytes = Encoding.ASCII.GetBytes(systemCnf);

            var image = GenerateIso9660Bin(sectorsNeeded, "TEST");
            GenerateIso9660File(image, "SYSTEM.CNF", cnfBytes, cnfBytes.Length);

            var exe = GenerateIso9660File(image, binaryName, null, binarySize);
            image[exe + 0] = 0x7f;
            image[exe + 1] = 0x45;
            image[exe + 2] = 0x4c;
            image[exe + 3] = 0x46;

            return image;
        }

        // data.c: convert_to_2352. The C output is malloc'd, so the 288 trailing bytes per sector are
        // uninitialized there; they are zero here and are never part of the logical sector data.
        public static byte[] ConvertTo2352(byte[] input, int firstSector)
        {
            var numSectors = (input.Length + 2047) / 2048;
            var output = new byte[numSectors * 2352];
            var sync = new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

            firstSector += 150;
            var frames = firstSector % 75;
            firstSector /= 75;
            var seconds = firstSector % 60;
            var minutes = firstSector / 60;

            var inPos = 0;
            var outPos = 0;
            for (var i = 0; i < numSectors; i++)
            {
                Buffer.BlockCopy(sync, 0, output, outPos, 12);
                outPos += 12;
                output[outPos++] = (byte)(((minutes / 10) << 4) | (minutes % 10));
                output[outPos++] = (byte)(((seconds / 10) << 4) | (seconds % 10));
                output[outPos++] = (byte)(((frames / 10) << 4) | (frames % 10));
                if (++frames == 75)
                {
                    frames = 0;
                    if (++seconds == 60)
                    {
                        seconds = 0;
                        ++minutes;
                    }
                }
                output[outPos++] = 2;

                Buffer.BlockCopy(input, inPos, output, outPos, Math.Min(2048, input.Length - inPos));
                inPos += 2048;

                outPos += 2352 - 16;
            }

            return output;
        }

        // test_hash_rom.c: generate_neo_file
        public static byte[] GenerateNeoFile(int payloadSize, string name, string manufacturer)
        {
            const int headerSize = 4096;
            var image = new byte[headerSize + payloadSize];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'O';
            image[3] = 1;
            image[4] = (byte)(payloadSize & 0xFF);
            image[5] = (byte)((payloadSize >> 8) & 0xFF);
            image[6] = (byte)((payloadSize >> 16) & 0xFF);
            image[7] = (byte)((payloadSize >> 24) & 0xFF);
            CopyAscii(Truncate(name, 32), image, 44);
            CopyAscii(Truncate(manufacturer, 16), image, 77);

            FillImage(image, headerSize, payloadSize);
            return image;
        }

        // test_hash_rom.c: generate_atari_7800_file
        public static byte[] GenerateAtari7800File(int kb, bool withHeader)
        {
            var sizeNeeded = kb * 1024 + (withHeader ? 128 : 0);
            var image = new byte[sizeNeeded];
            if (withHeader)
            {
                var header = new byte[128];
                header[0] = 3;
                CopyAscii("ATARI7800", header, 1);
                CopyAscii("GameName", header, 17);
                var attributes = new byte[] { 0, 0, 2, 0, 0, 0, 3, 1, 1 };
                Buffer.BlockCopy(attributes, 0, header, 48, attributes.Length);
                CopyAscii("ACTUAL CARTDATA STARTS HERE", header, 100);

                Buffer.BlockCopy(header, 0, image, 0, header.Length);
                image[50] = (byte)(kb / 4);

                FillImage(image, 128, sizeNeeded - 128);
            }
            else
            {
                FillImage(image, 0, sizeNeeded);
            }

            return image;
        }

        // test_hash_rom.c: generate_nes_file
        public static byte[] GenerateNesFile(int kb, bool withHeader)
        {
            var sizeNeeded = kb * 1024 + (withHeader ? 16 : 0);
            var image = new byte[sizeNeeded];
            if (withHeader)
            {
                image[0] = (byte)'N';
                image[1] = (byte)'E';
                image[2] = (byte)'S';
                image[3] = 0x1A;
                image[4] = (byte)(kb / 16);

                FillImage(image, 16, sizeNeeded - 16);
            }
            else
            {
                FillImage(image, 0, sizeNeeded);
            }

            return image;
        }

        // test_hash_rom.c: generate_fds_file
        public static byte[] GenerateFdsFile(int sides, bool withHeader)
        {
            var sizeNeeded = sides * 65500 + (withHeader ? 16 : 0);
            var image = new byte[sizeNeeded];
            if (withHeader)
            {
                image[0] = (byte)'F';
                image[1] = (byte)'D';
                image[2] = (byte)'S';
                image[3] = 0x1A;
                image[4] = (byte)sides;

                FillImage(image, 16, sizeNeeded - 16);
            }
            else
            {
                FillImage(image, 0, sizeNeeded);
            }

            return image;
        }

        // test_hash_rom.c: generate_nds_file
        public static byte[] GenerateNdsFile(int mb, uint arm9Size, uint arm7Size)
        {
            var sizeNeeded = mb * 1024 * 1024;
            var image = new byte[sizeNeeded];
            const uint arm9Addr = 65536;
            var arm7Addr = arm9Addr + arm9Size;
            var iconAddr = arm7Addr + arm7Size;

            FillImage(image, 0, sizeNeeded);

            WriteUInt32LE(image, 0x20, arm9Addr);
            WriteUInt32LE(image, 0x2C, arm9Size);
            WriteUInt32LE(image, 0x30, arm7Addr);
            WriteUInt32LE(image, 0x3C, arm7Size);
            WriteUInt32LE(image, 0x68, iconAddr);

            return image;
        }

        // test_hash_rom.c: test_rom_z64 / test_rom_v64 / test_rom_n64 / test_rom_ndd
        public static readonly byte[] TestRomZ64 =
        {
            0x80, 0x37, 0x12, 0x40, 0x00, 0x00, 0x00, 0x0F, 0x80, 0x24, 0x60, 0x00, 0x00, 0x00, 0x14, 0x44,
            0x63, 0x5A, 0x2B, 0xFF, 0x8B, 0x02, 0x23, 0x26, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x53, 0x55, 0x50, 0x45, 0x52, 0x20, 0x4D, 0x41, 0x52, 0x49, 0x4F, 0x20, 0x36, 0x34, 0x20, 0x20,
            0x20, 0x20, 0x20, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4E, 0x53, 0x4D, 0x45, 0x00
        };

        public static readonly byte[] TestRomV64 =
        {
            0x37, 0x80, 0x40, 0x12, 0x00, 0x00, 0x0F, 0x00, 0x24, 0x80, 0x00, 0x60, 0x00, 0x00, 0x44, 0x14,
            0x5A, 0x63, 0xFF, 0x2B, 0x02, 0x8B, 0x26, 0x23, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x55, 0x53, 0x45, 0x50, 0x20, 0x52, 0x41, 0x4D, 0x49, 0x52, 0x20, 0x4F, 0x34, 0x36, 0x20, 0x20,
            0x20, 0x20, 0x20, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4E, 0x00, 0x4D, 0x53, 0x00, 0x45
        };

        public static readonly byte[] TestRomN64 =
        {
            0x40, 0x12, 0x37, 0x80, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x60, 0x24, 0x80, 0x44, 0x14, 0x00, 0x00,
            0xFF, 0x2B, 0x5A, 0x63, 0x26, 0x23, 0x02, 0x8B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x45, 0x50, 0x55, 0x53, 0x41, 0x4D, 0x20, 0x52, 0x20, 0x4F, 0x49, 0x52, 0x20, 0x20, 0x34, 0x36,
            0x20, 0x20, 0x20, 0x20, 0x00, 0x00, 0x00, 0x00, 0x4E, 0x00, 0x00, 0x00, 0x00, 0x45, 0x4D, 0x53
        };

        public static readonly byte[] TestRomNdd =
        {
            0xE8, 0x48, 0xD3, 0x16, 0x10, 0x13, 0x00, 0x45, 0x0C, 0x18, 0x24, 0x30, 0x3C, 0x48, 0x54, 0x60,
            0x6C, 0x78, 0x84, 0x90, 0x9C, 0xA8, 0xB4, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0x02, 0x5C, 0x00,
            0x10, 0x16, 0x1C, 0x22, 0x28, 0x2A, 0x31, 0x32, 0x3A, 0x40, 0x46, 0x4C, 0x04, 0x0C, 0x14, 0x1C,
            0x24, 0x2C, 0x34, 0x3C, 0x44, 0x4C, 0x54, 0x5C, 0x04, 0x0C, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C
        };

        public static void CopyAscii(string text, byte[] target, int offset)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, target, offset, bytes.Length);
        }

        private static void WriteUInt32LE(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static bool BytesEqual(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (a[aOffset + i] != b[bOffset + i])
                {
                    return false;
                }
            }

            return true;
        }

        // snprintf(buf, n, "%s", s) keeps at most n - 1 characters.
        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
