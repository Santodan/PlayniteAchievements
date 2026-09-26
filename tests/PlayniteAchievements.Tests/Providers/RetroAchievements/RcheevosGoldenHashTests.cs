using DiscUtils.Iso9660;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using static PlayniteAchievements.Tests.Providers.RetroAchievements.RcheevosFixtureData;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// Golden-hash parity with rcheevos test/rhash. Each test ports one rcheevos test case: the fixture
    /// comes from the ported data.c generators, and the expected MD5 is copied from the rcheevos source.
    /// rcheevos serves "game.cue" from a mock cdreader that maps it to "game.bin" as 2048-byte sectors;
    /// here that becomes a real single-track MODE1/2048 cue sheet.
    /// </summary>
    [TestClass]
    public class RcheevosGoldenHashTests
    {
        private const int Console3do = 43;
        private const int ConsoleAmstradPc = 37;
        private const int ConsoleAppleII = 38;
        private const int ConsoleArcade = 27;
        private const int ConsoleArcadia2001 = 73;
        private const int ConsoleArduboy = 71;
        private const int ConsoleAtari2600 = 25;
        private const int ConsoleAtari7800 = 51;
        private const int ConsoleAtariJaguar = 17;
        private const int ConsoleAtariJaguarCd = 77;
        private const int ConsoleColecovision = 44;
        private const int ConsoleCommodore64 = 30;
        private const int ConsoleDreamcast = 40;
        private const int ConsoleElektorTvGamesComputer = 75;
        private const int ConsoleFairchildChannelF = 57;
        private const int ConsoleGameboy = 4;
        private const int ConsoleGameboyColor = 6;
        private const int ConsoleGameGear = 15;
        private const int ConsoleGameCube = 16;
        private const int ConsoleIntellivision = 45;
        private const int ConsoleIntertonVc4000 = 74;
        private const int ConsoleMagnavoxOdyssey2 = 23;
        private const int ConsoleMasterSystem = 11;
        private const int ConsoleMegaDrive = 1;
        private const int ConsoleMegaDuck = 69;
        private const int ConsoleMsx = 29;
        private const int ConsoleNeoGeoCd = 56;
        private const int ConsoleNeoGeoPocket = 14;
        private const int ConsoleNintendo = 7;
        private const int ConsoleNintendo64 = 2;
        private const int ConsoleNintendoDs = 18;
        private const int ConsoleNintendoDsi = 78;
        private const int ConsoleOric = 32;
        private const int ConsolePc8800 = 47;
        private const int ConsolePcEngine = 8;
        private const int ConsolePcEngineCd = 76;
        private const int ConsolePcfx = 49;
        private const int ConsolePlayStation = 12;
        private const int ConsolePlayStation2 = 21;
        private const int ConsolePokemonMini = 24;
        private const int ConsolePsp = 41;
        private const int ConsoleSaturn = 39;
        private const int ConsoleSega32x = 10;
        private const int ConsoleSegaCd = 9;
        private const int ConsoleSg1000 = 33;
        private const int ConsoleSuperCassetteVision = 55;
        private const int ConsoleSuperNintendo = 3;
        private const int ConsoleSupervision = 63;
        private const int ConsoleTi83 = 79;
        private const int ConsoleTic80 = 65;
        private const int ConsoleUzebox = 80;
        private const int ConsoleWasm4 = 72;
        private const int ConsoleWonderSwan = 53;
        private const int ConsoleZxSpectrum = 59;

        private const string JaguarAudioTracksRejected =
            "rcheevos mismatch: plugin rejects AUDIO cue tracks; rcheevos hashes the first track of the second session";
        private const string DreamcastNoGdi =
            "rcheevos mismatch: plugin has no .gdi support";
        private const string DreamcastFirstDataTrack =
            "rcheevos mismatch: plugin reads the first data track with track-relative LBAs; rcheevos reads track 3 / last track at absolute LBAs";
        private const string CuePartialSector =
            "rcheevos mismatch: plugin cue reader requires whole 2048-byte sectors; the rcheevos fixture bin is 512 bytes";
        private const string FullFileM3u =
            "rcheevos mismatch: whole-file hasher hashes the .m3u text instead of expanding the playlist";
        private const string NeoContentHash =
            "rcheevos mismatch: plugin hashes the arcade filename for .neo; rcheevos hashes the ROM payload after the 4096-byte NEO header";
        private const string Atari7800HeaderByte0 =
            "rcheevos mismatch: plugin requires header byte0 == 0x01; rcheevos only checks \"ATARI7800\" at offset 1";
        private const string ScvHeaderNotSkipped =
            "rcheevos mismatch: plugin hashes Super Cassette Vision as whole file; rcheevos skips a 32-byte \"EmuSCV\" header";

        private string _dir;

        [TestInitialize]
        public void Initialize()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsRcheevosGolden_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch
            {
            }
        }

        // ===== 3DO (test_hash_disc.c) =====

        [TestMethod]
        public void ThreeDo_Bin()
        {
            var path = WriteFile("game.bin", Generate3doBin(1, 123456));
            AssertHash(Console3do, path, "9b2266b8f5abed9c12cce780750e88d6");
        }

        [TestMethod]
        public void ThreeDo_Cue()
        {
            WriteFile("game.bin", Generate3doBin(1, 9347));
            var path = WriteMockCue("game.cue", "game.bin");
            AssertHash(Console3do, path, "257d1d19365a864266b236214dbea29c");
        }

        [TestMethod]
        public void ThreeDo_Iso()
        {
            var path = WriteFile("game.iso", Generate3doBin(1, 9347));
            AssertHash(Console3do, path, "257d1d19365a864266b236214dbea29c");
        }

        [TestMethod]
        public void ThreeDo_InvalidHeader()
        {
            var image = Generate3doBin(1, 12);
            image[3] = 0x34;
            var path = WriteFile("game.bin", image);
            AssertNoHash(Console3do, path);
        }

        [TestMethod]
        public void ThreeDo_LaunchmeCaseInsensitive()
        {
            var image = Generate3doBin(1, 6543);
            CopyAscii("launchme", image, 2048 + 0x14 + 0x48 + 0x20);
            var path = WriteFile("game.bin", image);
            AssertHash(Console3do, path, "59622882e3261237e8a1e396825ae4f5");
        }

        [TestMethod]
        public void ThreeDo_NoLaunchme()
        {
            var image = Generate3doBin(1, 6543);
            CopyAscii("filename", image, 2048 + 0x14 + 0x48 + 0x20);
            var path = WriteFile("game.bin", image);
            AssertNoHash(Console3do, path);
        }

        [TestMethod]
        public void ThreeDo_LongDirectory()
        {
            var path = WriteFile("game.bin", Generate3doBin(3, 6543));
            AssertHash(Console3do, path, "8979e876ae502e0f79218f7ff7bd8c2a");
        }

        // ===== Atari Jaguar CD (test_hash_disc.c) =====

        private const string JaguarCueTwoSessions =
            "REM SESSION 01\n" +
            "FILE \"track01.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "REM SESSION 02\n" +
            "FILE \"track02.bin\" BINARY\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track03.bin\" BINARY\n" +
            "  TRACK 03 AUDIO\n" +
            "    INDEX 01 00:00:00\n";

        private const string JaguarCueSecondSessionTrack3 =
            "REM SESSION 01\n" +
            "FILE \"track01.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track02.bin\" BINARY\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "REM SESSION 02\n" +
            "FILE \"track03.bin\" BINARY\n" +
            "  TRACK 03 AUDIO\n" +
            "    INDEX 01 00:00:00\n";

        [TestMethod]
        [Ignore(JaguarAudioTracksRejected)]
        public void AtariJaguarCd()
        {
            var path = WriteText("game.cue", JaguarCueTwoSessions);
            WriteFile("track02.bin", GenerateJaguarCdBin(2, 60024, false));
            AssertHash(ConsoleAtariJaguarCd, path, "c324d95dc5831c2d5c470eefb18c346b");
        }

        [TestMethod]
        [Ignore(JaguarAudioTracksRejected)]
        public void AtariJaguarCd_Byteswapped()
        {
            var path = WriteText("game.cue", JaguarCueTwoSessions);
            WriteFile("track02.bin", GenerateJaguarCdBin(2, 60024, true));
            AssertHash(ConsoleAtariJaguarCd, path, "c324d95dc5831c2d5c470eefb18c346b");
        }

        [TestMethod]
        [Ignore(JaguarAudioTracksRejected)]
        public void AtariJaguarCd_Track3()
        {
            var path = WriteText("game.cue", JaguarCueSecondSessionTrack3);
            WriteFile("track03.bin", GenerateJaguarCdBin(1470, 99200, true));
            AssertHash(ConsoleAtariJaguarCd, path, "060e9d223c584b581cf7d7ce17c0e5dc");
        }

        [TestMethod]
        public void AtariJaguarCd_NoHeader()
        {
            var image = GenerateJaguarCdBin(2, 32768, true);
            image[2 + 64 + 12] = (byte)'B';
            var path = WriteText("game.cue", JaguarCueTwoSessions);
            WriteFile("track02.bin", image);
            AssertNoHash(ConsoleAtariJaguarCd, path);
        }

        [TestMethod]
        public void AtariJaguarCd_NoSessions()
        {
            var path = WriteText(
                "game.cue",
                "FILE \"track01.bin\" BINARY\n" +
                "  TRACK 01 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "FILE \"track02.bin\" BINARY\n" +
                "  TRACK 02 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "FILE \"track03.bin\" BINARY\n" +
                "  TRACK 03 AUDIO\n" +
                "    INDEX 01 00:00:00\n");
            WriteFile("track03.bin", GenerateJaguarCdBin(2, 99200, true));
            AssertNoHash(ConsoleAtariJaguarCd, path);
        }

        [TestMethod]
        [Ignore(JaguarAudioTracksRejected)]
        public void AtariJaguarCd_Homebrew()
        {
            // rcheevos also overrides _rc_hash_jaguar_cd_homebrew_hash with "4e4114b2675eff21bb77dd41e141ddd6";
            // the plugin's homebrew hash is a constant, so that override has no equivalent here.
            var image = GenerateJaguarCdBin(2, 45760, true);
            var image2 = GenerateJaguarCdBin(2, 986742, true);
            image2[0x60] = 0x21;
            Buffer.BlockCopy(image2, 0x62, image2, 0xA2, 8);
            CopyAscii("RTKARTKARTKARTKA", image2, 0x62);
            CopyAscii("RTKARTKARTKARTKA", image2, 0x72);
            CopyAscii("RTKARTKARTKARTKA", image2, 0x82);
            CopyAscii("RTKARTKARTKARTKA", image2, 0x92);

            var path = WriteText("game.cue", JaguarCueSecondSessionTrack3);
            WriteFile("track02.bin", image2);
            WriteFile("track03.bin", image);
            AssertHash(ConsoleAtariJaguarCd, path, "3fdf70e362c845524c9e447aacaed0a9");
        }

        // ===== Dreamcast (test_hash_disc.c) =====

        [TestMethod]
        [Ignore(DreamcastNoGdi)]
        public void Dreamcast_SingleBin()
        {
            // The mock cdreader serves track N of a .gdi as trackNN.bin with 2048-byte sectors.
            WriteFile("track03.bin", GenerateDreamcastBin(45000, 1458208));
            var path = WriteText("game.gdi", "3\r\n3 45000 4 2048 track03.bin 0\r\n");
            AssertHash(ConsoleDreamcast, path, "2a550500caee9f06e5d061fe10a46f6e");
        }

        [TestMethod]
        [Ignore(DreamcastNoGdi)]
        public void Dreamcast_SplitBin()
        {
            var image = GenerateDreamcastBin(548106, 1830912);
            WriteFile("track03.bin", image);
            WriteFile("track26.bin", image);
            var path = WriteText("game.gdi", "26\r\n3 45000 4 2048 track03.bin 0\r\n26 548106 4 2048 track26.bin 0\r\n");
            AssertHash(ConsoleDreamcast, path, "771e56aff169230ede4505013a4bcf9f");
        }

        private const string DreamcastCueFile =
            "FILE \"track01.bin\" BINARY\n" +
            "  TRACK 01 MODE1/2352\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track02.bin\" BINARY\n" +
            "  TRACK 02 AUDIO\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:02:00\n" +
            "FILE \"track03.bin\" BINARY\n" +
            "  TRACK 03 MODE1/2352\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track04.bin\" BINARY\n" +
            "  TRACK 04 AUDIO\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:02:00\n" +
            "FILE \"track05.bin\" BINARY\n" +
            "  TRACK 05 MODE1/2352\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:03:00\n";

        private string WriteDreamcastCueFixture()
        {
            var image = ConvertTo2352(GenerateDreamcastBin(45000, 1697028), 45000);
            WriteFile("track01.bin", Prefix(image, 1425312));
            WriteFile("track02.bin", Prefix(image, 1589952));
            WriteFile("track03.bin", image);
            WriteFile("track04.bin", Prefix(image, 1237152));
            WriteFile("track05.bin", image);
            return WriteText("game.cue", DreamcastCueFile);
        }

        [TestMethod]
        [Ignore(DreamcastFirstDataTrack)]
        public void Dreamcast_Cue()
        {
            AssertHash(ConsoleDreamcast, WriteDreamcastCueFixture(), "c952864c3364591d2a8793ce2cfbf3a0");
        }

        [TestMethod]
        [Ignore(DreamcastFirstDataTrack)]
        public void Dreamcast_CueBuffered()
        {
            // Differs from Dreamcast_Cue only in rcheevos's buffered iterator input; the file hash is identical.
            AssertHash(ConsoleDreamcast, WriteDreamcastCueFixture(), "c952864c3364591d2a8793ce2cfbf3a0");
        }

        // ===== GameCube (test_hash_disc.c) =====

        [TestMethod]
        public void GameCube()
        {
            var path = WriteFile("test.iso", GenerateGameCubeIso(32));
            AssertHash(ConsoleGameCube, path, "c7803b704fa43d22d8f6e55f4789cb45");
        }

        // ===== Neo Geo CD (test_hash_disc.c) =====

        // rcheevos's generate_iso9660_bin places the root directory in sector 17 with no volume descriptor set
        // terminator. rcheevos's reader tolerates that; DiscUtils rejects it ("Volume is not ISO-9660"). Each
        // ISO-based case therefore also runs as *_StandardIso: the same files, byte for byte, repacked by
        // DiscUtils CDBuilder. The golden MD5 depends only on file names and contents, so it still applies.
        private const string DiscUtilsRejectsFixture =
            "rcheevos mismatch: DiscUtils rejects rcheevos's minimal ISO9660 layout (no volume descriptor set terminator); see _StandardIso variant";

        private static byte[] NeoGeoCdImage()
        {
            var image = GenerateIso9660Bin(160, "TEST");
            AddIsoFile(image, "IPL.TXT", "FIXA.FIX,0,0\r\nPROG.PRG,0,0\r\nSOUND.PCM,0,0\r\n\x1a");
            AddIsoFile(image, "PROG.PRG", GenerateGenericFile(273470));
            return image;
        }

        private static byte[] NeoGeoCdMultiplePrgImage()
        {
            var image = GenerateIso9660Bin(160, "TEST");
            AddIsoFile(image, "IPL.TXT", "FIXA.FIX,0,0\r\nPROG1.PRG,0,0\r\nSOUND.PCM,0,0\r\nPROG2.PRG,0,44000\r\n\x1a");
            AddIsoFile(image, "PROG1.PRG", GenerateGenericFile(273470));
            AddIsoFile(image, "PROG2.PRG", GenerateGenericFile(13768));
            return image;
        }

        private static byte[] NeoGeoCdLowercaseIplImage()
        {
            var image = GenerateIso9660Bin(160, "TEST");
            AddIsoFile(image, "IPL.TXT", "fixa.fix,0,0\r\nprog.prg,0,0\r\nsound.pcm,0,0\r\n\x1a");
            AddIsoFile(image, "PROG.PRG", GenerateGenericFile(273470));
            return image;
        }

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void NeoGeoCd() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(NeoGeoCdImage()), "96f35b20c6cf902286da45e81a50b2a3");

        [TestMethod]
        public void NeoGeoCd_StandardIso() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(RepackAsStandardIso(NeoGeoCdImage())), "96f35b20c6cf902286da45e81a50b2a3");

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void NeoGeoCd_MultiplePrg() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(NeoGeoCdMultiplePrgImage()), "d62df483c4786d3c63f27b6c5f17eeca");

        [TestMethod]
        public void NeoGeoCd_MultiplePrg_StandardIso() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(RepackAsStandardIso(NeoGeoCdMultiplePrgImage())), "d62df483c4786d3c63f27b6c5f17eeca");

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void NeoGeoCd_LowercaseIplContents() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(NeoGeoCdLowercaseIplImage()), "96f35b20c6cf902286da45e81a50b2a3");

        [TestMethod]
        public void NeoGeoCd_LowercaseIplContents_StandardIso() => AssertHash(ConsoleNeoGeoCd, WriteMockCueImage(RepackAsStandardIso(NeoGeoCdLowercaseIplImage())), "96f35b20c6cf902286da45e81a50b2a3");

        // ===== PC Engine CD (test_hash_disc.c) =====

        [TestMethod]
        public void PceCd()
        {
            WriteFile("game.bin", GeneratePceCdBin(72));
            var path = WriteMockCue("game.cue", "game.bin");
            AssertHash(ConsolePcEngineCd, path, "6565819195a49323e080e7539b54f251");
        }

        [TestMethod]
        public void PceCd_InvalidHeader()
        {
            var image = GeneratePceCdBin(72);
            image[2048 + 0x24] = 0x34;
            WriteFile("game.bin", image);
            var path = WriteMockCue("game.cue", "game.bin");
            AssertNoHash(ConsolePcEngineCd, path);
        }

        // ===== PC-FX (test_hash_disc.c) =====

        [TestMethod]
        public void Pcfx()
        {
            WriteFile("game.bin", GeneratePcfxBin(72));
            var path = WriteMockCue("game.cue", "game.bin");
            AssertHash(ConsolePcfx, path, "0a03af66559b8529c50c4e7788379598");
        }

        [TestMethod]
        public void Pcfx_InvalidHeader()
        {
            var image = GeneratePcfxBin(72);
            image[12] = 0x34;
            WriteFile("game.bin", image);
            var path = WriteMockCue("game.cue", "game.bin");
            AssertNoHash(ConsolePcfx, path);
        }

        [TestMethod]
        public void Pcfx_PceCd()
        {
            // The mock cdreader serves track 2 of game.cue as game2.bin; a real two-track cue expresses that.
            var image = GeneratePceCdBin(72);
            WriteFile("game.bin", image);
            WriteFile("game2.bin", image);
            var path = WriteText(
                "game.cue",
                "FILE \"game.bin\" BINARY\r\n  TRACK 01 MODE1/2048\r\n    INDEX 01 00:00:00\r\n" +
                "FILE \"game2.bin\" BINARY\r\n  TRACK 02 MODE1/2048\r\n    INDEX 01 00:00:00\r\n");
            AssertHash(ConsolePcfx, path, "6565819195a49323e080e7539b54f251");
        }

        // ===== PlayStation (test_hash_disc.c) =====

        private static byte[] PsxNoSystemCnfImage()
        {
            var binarySize = 0x12000;
            var sectorsNeeded = ((binarySize + 2047) / 2048) + 20;
            var image = GenerateIso9660Bin(sectorsNeeded, "HOMEBREW");
            var exe = GenerateIso9660File(image, "PSX.EXE", null, binarySize);
            CopyAscii("PS-X EXE", image, exe);
            binarySize -= 2048;
            image[exe + 28] = (byte)(binarySize & 0xFF);
            image[exe + 29] = (byte)((binarySize >> 8) & 0xFF);
            image[exe + 30] = (byte)((binarySize >> 16) & 0xFF);
            image[exe + 31] = (byte)((binarySize >> 24) & 0xFF);
            return image;
        }

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Psx_Cd() => AssertHash(ConsolePlayStation, WriteMockCueImage(GeneratePsxBin("SLUS_007.45", 0x07D800)), "db433fb038cde4fb15c144e8c7dea6e3");

        [TestMethod]
        public void Psx_Cd_StandardIso() => AssertHash(ConsolePlayStation, WriteMockCueImage(RepackAsStandardIso(GeneratePsxBin("SLUS_007.45", 0x07D800))), "db433fb038cde4fb15c144e8c7dea6e3");

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Psx_Cd_NoSystemCnf() => AssertHash(ConsolePlayStation, WriteMockCueImage(PsxNoSystemCnfImage()), "e494c79a7315be0dc3e8571c45df162c");

        [TestMethod]
        public void Psx_Cd_NoSystemCnf_StandardIso() => AssertHash(ConsolePlayStation, WriteMockCueImage(RepackAsStandardIso(PsxNoSystemCnfImage())), "e494c79a7315be0dc3e8571c45df162c");

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Psx_Cd_ExeInSubfolder() => AssertHash(ConsolePlayStation, WriteMockCueImage(GeneratePsxBin("bin\\SCES_012.37", 0x07D800)), "674018e23a4052113665dfb264e9c2fc");

        [TestMethod]
        public void Psx_Cd_ExeInSubfolder_StandardIso() => AssertHash(ConsolePlayStation, WriteMockCueImage(RepackAsStandardIso(GeneratePsxBin("bin\\SCES_012.37", 0x07D800))), "674018e23a4052113665dfb264e9c2fc");

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Psx_Cd_ExtraSlash() => AssertHash(ConsolePlayStation, WriteMockCueImage(GeneratePsxBin("\\SLUS_007.45", 0x07D800)), "db433fb038cde4fb15c144e8c7dea6e3");

        [TestMethod]
        public void Psx_Cd_ExtraSlash_StandardIso() => AssertHash(ConsolePlayStation, WriteMockCueImage(RepackAsStandardIso(GeneratePsxBin("\\SLUS_007.45", 0x07D800))), "db433fb038cde4fb15c144e8c7dea6e3");

        // ===== PlayStation 2 (test_hash_disc.c) =====

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Ps2_Iso() => AssertHash(ConsolePlayStation2, WriteFile("game.iso", GeneratePs2Bin("SLUS_200.64", 0x07D800)), "01a517e4ad72c6c2654d1b839be7579d");

        [TestMethod]
        public void Ps2_Iso_StandardIso() => AssertHash(ConsolePlayStation2, WriteFile("game.iso", RepackAsStandardIso(GeneratePs2Bin("SLUS_200.64", 0x07D800))), "01a517e4ad72c6c2654d1b839be7579d");

        // Passes on the raw fixture only because DiscUtils cannot open it; the _StandardIso variant exercises the BOOT2 lookup.
        [TestMethod]
        public void Ps2_Psx() => AssertNoHash(ConsolePlayStation2, WriteMockCueImage(GeneratePsxBin("SLUS_007.45", 0x07D800)));

        [TestMethod]
        public void Ps2_Psx_StandardIso() => AssertNoHash(ConsolePlayStation2, WriteMockCueImage(RepackAsStandardIso(GeneratePsxBin("SLUS_007.45", 0x07D800))));

        // ===== PlayStation Portable (test_hash_disc.c) =====

        private static byte[] PspImage()
        {
            var image = GenerateIso9660Bin(160, "TEST");
            AddIsoFile(image, "PSP_GAME\\PARAM.SFO", GenerateGenericFile(690));
            AddIsoFile(image, "PSP_GAME\\SYSDIR\\EBOOT.BIN", GenerateGenericFile(273470));
            return image;
        }

        private static byte[] PspVideoImage()
        {
            var image = GenerateIso9660Bin(160, "TEST");
            AddIsoFile(image, "PSP_GAME\\SYSDIR\\UPDATE\\EBOOT.BIN", GenerateGenericFile(273470));
            AddIsoFile(image, "UMD_VIDEO\\PARAM.SFO", GenerateGenericFile(690));
            return image;
        }

        [TestMethod]
        [Ignore(DiscUtilsRejectsFixture)]
        public void Psp() => AssertHash(ConsolePsp, WriteFile("game.iso", PspImage()), "27ec2f9b7238b2ef29af31ddd254f201");

        [TestMethod]
        public void Psp_StandardIso() => AssertHash(ConsolePsp, WriteFile("game.iso", RepackAsStandardIso(PspImage())), "27ec2f9b7238b2ef29af31ddd254f201");

        // Passes on the raw fixture only because DiscUtils cannot open it; the _StandardIso variant exercises the PARAM.SFO lookup.
        [TestMethod]
        public void Psp_Video() => AssertNoHash(ConsolePsp, WriteFile("game.iso", PspVideoImage()));

        [TestMethod]
        public void Psp_Video_StandardIso() => AssertNoHash(ConsolePsp, WriteFile("game.iso", RepackAsStandardIso(PspVideoImage())));

        [TestMethod]
        public void Psp_Homebrew()
        {
            var path = WriteFile("eboot.pbp", GenerateGenericFile(3532124));
            AssertHash(ConsolePsp, path, "fcde8760893b09e508e5f4fe642eb132");
        }

        // ===== Sega CD / Saturn (test_hash_disc.c) =====

        [TestMethod]
        [Ignore(CuePartialSector)]
        public void SegaCd()
        {
            var image = GenerateGenericFile(512);
            CopyAscii("SEGADISCSYSTEM  ", image, 0);
            WriteFile("game.bin", image);
            var path = WriteMockCue("game.cue", "game.bin");
            AssertHash(ConsoleSegaCd, path, "574498e1453cb8934df60c4ab906e783");
        }

        [TestMethod]
        public void SegaCd_Buffered()
        {
            var image = GenerateGenericFile(512);
            CopyAscii("SEGADISCSYSTEM  ", image, 0);
            var path = WriteFile("game.iso", image);
            AssertHash(ConsoleSegaCd, path, "574498e1453cb8934df60c4ab906e783");
        }

        [TestMethod]
        public void SegaCd_InvalidHeader()
        {
            WriteFile("game.bin", GenerateGenericFile(512));
            var path = WriteMockCue("game.cue", "game.bin");
            AssertNoHash(ConsoleSegaCd, path);
        }

        [TestMethod]
        [Ignore(CuePartialSector)]
        public void Saturn()
        {
            var image = GenerateGenericFile(512);
            CopyAscii("SEGA SEGASATURN ", image, 0);
            WriteFile("game.bin", image);
            var path = WriteMockCue("game.cue", "game.bin");
            AssertHash(ConsoleSaturn, path, "4cd9c8e41cd8d137be15bbe6a93ae1d8");
        }

        [TestMethod]
        public void Saturn_InvalidHeader()
        {
            WriteFile("game.bin", GenerateGenericFile(512));
            var path = WriteMockCue("game.cue", "game.bin");
            AssertNoHash(ConsoleSaturn, path);
        }

        // ===== Full-file and m3u rows (test_hash.c: test_hash_full_file / test_hash_m3u) =====

        [TestMethod] public void FullFile_AmstradPc_Dsk() => AssertFullFile(ConsoleAmstradPc, "test.dsk", 194816, "9d616e4ad3f16966f61422c57e22aadd");
        [TestMethod] public void M3u_AmstradPc_Dsk() => AssertM3u(ConsoleAmstradPc, "test.dsk", 194816, "9d616e4ad3f16966f61422c57e22aadd");
        [TestMethod] public void FullFile_AppleII_Nib() => AssertFullFile(ConsoleAppleII, "test.nib", 232960, "96e8d33bdc385fd494327d6e6791cbe4");
        [TestMethod] public void FullFile_AppleII_Dsk() => AssertFullFile(ConsoleAppleII, "test.dsk", 143360, "88be638f4d78b4072109e55f13e8a0ac");
        [TestMethod] public void M3u_AppleII_Dsk() => AssertM3u(ConsoleAppleII, "test.dsk", 143360, "88be638f4d78b4072109e55f13e8a0ac");
        [TestMethod] public void FullFile_Commodore64_Nib() => AssertFullFile(ConsoleCommodore64, "test.nib", 327936, "e7767d32b23e3fa62c5a250a08caeba3");
        [TestMethod] public void FullFile_Commodore64_D64() => AssertFullFile(ConsoleCommodore64, "test.d64", 174848, "ecd5a8ef4e77f2e9469d9b6e891394f0");
        [TestMethod] [Ignore(FullFileM3u)] public void M3u_Commodore64_D64() => AssertM3u(ConsoleCommodore64, "test.d64", 174848, "ecd5a8ef4e77f2e9469d9b6e891394f0");
        [TestMethod] public void FullFile_Msx_Dsk() => AssertFullFile(ConsoleMsx, "test.dsk", 737280, "0e73fe94e5f2e2d8216926eae512b7a6");
        [TestMethod] public void M3u_Msx_Dsk() => AssertM3u(ConsoleMsx, "test.dsk", 737280, "0e73fe94e5f2e2d8216926eae512b7a6");
        [TestMethod] public void FullFile_Pc8800_D88() => AssertFullFile(ConsolePc8800, "test.d88", 348288, "8cca4121bf87200f45e91b905a9f5afd");
        [TestMethod] public void M3u_Pc8800_D88() => AssertM3u(ConsolePc8800, "test.d88", 348288, "8cca4121bf87200f45e91b905a9f5afd");
        [TestMethod] public void FullFile_ZxSpectrum_Tap() => AssertFullFile(ConsoleZxSpectrum, "test.tap", 1596, "714a9f455e616813dd5421c5b347e5e5");
        [TestMethod] public void FullFile_ZxSpectrum_Tzx() => AssertFullFile(ConsoleZxSpectrum, "test.tzx", 14971, "93723e6d1100f9d1d448a27cf6618c47");

        // ===== m3u support (test_hash.c) =====

        private const string M3uGoldenMd5 = "a0f425b23200568132ba76b2405e3933";

        private void AssertValidM3u(string discFilename, string m3uFilename, string m3uContents)
        {
            WriteFile(discFilename, GenerateGenericFile(131072));
            var path = WriteText(m3uFilename, m3uContents);
            AssertHash(ConsolePc8800, path, M3uGoldenMd5);
        }

        [TestMethod]
        public void M3u_Buffered()
        {
            // rcheevos passes the m3u contents as an iterator buffer; the file-path equivalent is the same m3u on disk.
            AssertValidM3u("test.d88", "test.m3u", "test.d88");
        }

        [TestMethod]
        public void M3u_WithComments() => AssertValidM3u("test.d88", "test.m3u", "#EXTM3U\r\n\r\n#EXTBYT:131072\r\ntest.d88\r\n");

        [TestMethod]
        public void M3u_Empty()
        {
            var path = WriteText("test.m3u", "#EXTM3U\r\n\r\n#EXTBYT:131072\r\n");
            AssertNoHash(ConsolePc8800, path);
        }

        [TestMethod]
        public void M3u_TrailingWhitespace() => AssertValidM3u("test.d88", "test.m3u", "#EXTM3U  \r\n  \r\n#EXTBYT:131072  \r\ntest.d88  \t  \r\n");

        [TestMethod]
        public void M3u_LineEnding() => AssertValidM3u("test.d88", "test.m3u", "#EXTM3U\n\n#EXTBYT:131072\ntest.d88\n");

        [TestMethod]
        public void M3u_ExtensionCase() => AssertValidM3u("test.D88", "test.M3U", "#EXTM3U\r\n\r\n#EXTBYT:131072\r\ntest.D88\r\n");

        [TestMethod]
        public void M3u_RelativePath() => AssertValidM3u("folder1/folder2/test.d88", "folder1/test.m3u", "#EXTM3U\r\n\r\n#EXTBYT:131072\r\nfolder2/test.d88");

        [TestMethod]
        public void M3u_AbsolutePath()
        {
            // rcheevos rows use "/absolute", "\\absolute", "C:\\absolute", UNC and "samba:" paths against a mock
            // filereader; on a real filesystem only a rooted drive path under the temp directory is writable.
            var absolute = Path.Combine(_dir, "absolute", "test.d88");
            AssertValidM3u(absolute, "relative/test.m3u", "#EXTM3U\r\n\r\n#EXTBYT:131072\r\n" + absolute);
        }

        [TestMethod]
        public void FileWithoutExt()
        {
            var path = WriteFile("test", GenerateNesFile(32, true));
            AssertHash(ConsoleNintendo, path, "6a2305a2b6675a97ff792709be1ca857");
        }

        // ===== Arcade (test_hash_rom.c) =====

        [DataTestMethod]
        [DataRow("game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("game.7z", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("\\game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("roms\\game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("C:\\roms\\game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/roms/game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/roms/game.7z", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/nes_game.zip", "9b7aad36b365712fc93728088de4c209")]
        [DataRow("/home/user/nes/game.zip", "9b7aad36b365712fc93728088de4c209")]
        [DataRow("C:\\roms\\nes\\game.zip", "9b7aad36b365712fc93728088de4c209")]
        [DataRow("C:\\roms\\NES\\game.zip", "9b7aad36b365712fc93728088de4c209")]
        [DataRow("nes\\game.zip", "9b7aad36b365712fc93728088de4c209")]
        [DataRow("/home/user/snes/game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/nes2/game.zip", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/chf/game.zip", "6ef57f16562ea0c7f49d93853b313e32")]
        [DataRow("/home/user/channelf/game.zip", "7b6506637a0cc79bd1d24a43a34fa3b9")]
        [DataRow("/home/user/coleco/game.zip", "c546f63ae7de98add4b9f221a4749260")]
        [DataRow("/home/user/colecovision/game.zip", "47279207b94dbf2a45cb13efa56d685e")]
        [DataRow("/home/user/msx/game.zip", "59ab85f6b56324fd81b4e324b804c29f")]
        [DataRow("/home/user/msx1/game.zip", "33328d832dcb0854383cdd4a4565c459")]
        [DataRow("/home/user/pce/game.zip", "c414a783f3983bbe2e9e01d9d5320c7e")]
        [DataRow("/home/user/pcengine/game.zip", "49370c3cbe98bdcdce545c68379487db")]
        [DataRow("/home/user/sgx/game.zip", "db545ab29694bfda1010317d4bac83b8")]
        [DataRow("/home/user/supergrafx/game.zip", "5665c9ef4c2f6609d8e420c4d86ba692")]
        [DataRow("/home/user/tg16/game.zip", "8b6c5c2e54915be2cdba63973862e143")]
        [DataRow("/home/user/fds/game.zip", "c0c135a97e8c577cfdf9204823ff211f")]
        [DataRow("/home/user/gamegear/game.zip", "f6f471e952b8103032b723f57bdbe767")]
        [DataRow("/home/user/mastersystem/game.zip", "f4805afe0ff5647140a26bd0a1057373")]
        [DataRow("/home/user/sms/game.zip", "43f35f575dead94dd2f42f9caf69fe5a")]
        [DataRow("/home/user/megadriv/game.zip", "f99d0aaf12ba3eb6ced9878c76692c63")]
        [DataRow("/home/user/megadrive/game.zip", "73eb5d7034b382093b1d36414d9e84e4")]
        [DataRow("/home/user/genesis/game.zip", "b62f810c63e1cba7f5b7569643bec236")]
        [DataRow("/home/user/sg1000/game.zip", "e8f6c711c4371f09537b4f2a7a304d6c")]
        [DataRow("/home/user/spectrum/game.zip", "a5f62157b2617bd728c4b1bc885c29e9")]
        [DataRow("/home/user/ngp/game.zip", "d4133b74c4e57274ca514e27a370dcb6")]
        public void Arcade(string path, string expectedMd5)
        {
            // The arcade hash is derived from the path alone, so no fixture file is written.
            AssertHash(ConsoleArcade, path, expectedMd5);
        }

        [DataTestMethod]
        [DataRow("game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("\\game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("roms\\game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("C:\\roms\\game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/roms/game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        [DataRow("/home/user/games/game", "c8d46d341bea4fd5bff866a65ff8aea9")]
        public void Arcade_FromDirectory(string path, string expectedMd5)
        {
            AssertHash(ConsoleArcade, path, expectedMd5);
        }

        [TestMethod]
        [Ignore(NeoContentHash)]
        public void Neo()
        {
            var path = WriteFile("game.neo", GenerateNeoFile(131072, "Test Game", "TestCorp"));
            var payloadHash = HashUtils.ComputeMd5Hex(GenerateGenericFile(131072));
            AssertHash(ConsoleArcade, path, payloadHash);
        }

        [TestMethod]
        [Ignore(NeoContentHash)]
        public void Neo_HeaderVariants()
        {
            var path1 = WriteFile("game1.neo", GenerateNeoFile(131072, "Test Game", "TestCorp"));
            var path2 = WriteFile("game2.neo", GenerateNeoFile(131072, "test game (alt name)", "OtherTool"));
            var hashes1 = Hash(ConsoleArcade, path1);
            var hashes2 = Hash(ConsoleArcade, path2);
            Assert.AreEqual(1, hashes1.Count);
            Assert.AreEqual(1, hashes2.Count);
            Assert.AreEqual(hashes1[0], hashes2[0]);
        }

        [TestMethod]
        [Ignore(NeoContentHash)]
        public void Neo_BadMagic()
        {
            var image = GenerateNeoFile(131072, "Test Game", "TestCorp");
            image[3] = 2;
            var path = WriteFile("game.neo", image);
            AssertNoHash(ConsoleArcade, path);
        }

        // ===== Arduboy (test_hash_rom.c) =====

        private const string ArduboyMd5 = "67b64633285a7f965064ba29dab45148";

        [TestMethod]
        public void Arduboy()
        {
            var path = WriteText(
                "game.hex",
                ":100000000C94690D0C94910D0C94910D0C94910D20\n" +
                ":100010000C94910D0C94910D0C94910D0C94910DE8\n" +
                ":100020000C94910D0C94910D0C94C32A0C94352BC7\n" +
                ":00000001FF\n");
            AssertHash(ConsoleArduboy, path, ArduboyMd5);
        }

        [TestMethod]
        public void Arduboy_Crlf()
        {
            var path = WriteText(
                "game.hex",
                ":100000000C94690D0C94910D0C94910D0C94910D20\r\n" +
                ":100010000C94910D0C94910D0C94910D0C94910DE8\r\n" +
                ":100020000C94910D0C94910D0C94C32A0C94352BC7\r\n" +
                ":00000001FF\r\n");
            AssertHash(ConsoleArduboy, path, ArduboyMd5);
        }

        [TestMethod]
        public void Arduboy_NoFinalLf()
        {
            var path = WriteText(
                "game.hex",
                ":100000000C94690D0C94910D0C94910D0C94910D20\n" +
                ":100010000C94910D0C94910D0C94910D0C94910DE8\n" +
                ":100020000C94910D0C94910D0C94C32A0C94352BC7\n" +
                ":00000001FF");
            AssertHash(ConsoleArduboy, path, ArduboyMd5);
        }

        // ===== Atari 7800 (test_hash_rom.c) =====

        [TestMethod]
        public void Atari7800()
        {
            var path = WriteFile("test.a78", GenerateAtari7800File(16, false));
            AssertHash(ConsoleAtari7800, path, "455f07d8500f3fabc54906737866167f");
        }

        [TestMethod]
        [Ignore(Atari7800HeaderByte0)]
        public void Atari7800_WithHeader()
        {
            var path = WriteFile("test.a78", GenerateAtari7800File(16, true));
            AssertHash(ConsoleAtari7800, path, "455f07d8500f3fabc54906737866167f");
        }

        // ===== NES / FDS (test_hash_rom.c) =====

        [TestMethod]
        public void Nes_32k()
        {
            var path = WriteFile("test.nes", GenerateNesFile(32, false));
            AssertHash(ConsoleNintendo, path, "6a2305a2b6675a97ff792709be1ca857");
        }

        [TestMethod]
        public void Nes_32k_WithHeader()
        {
            var path = WriteFile("test.nes", GenerateNesFile(32, true));
            AssertHash(ConsoleNintendo, path, "6a2305a2b6675a97ff792709be1ca857");
        }

        [TestMethod]
        public void Nes_256k()
        {
            var path = WriteFile("test.nes", GenerateNesFile(256, false));
            AssertHash(ConsoleNintendo, path, "545d527301b8ae148153988d6c4fcb84");
        }

        [TestMethod]
        public void Fds_TwoSides()
        {
            var path = WriteFile("test.fds", GenerateFdsFile(2, false));
            AssertHash(ConsoleNintendo, path, "fd770d4d34c00760fabda6ad294a8f0b");
        }

        [TestMethod]
        public void Fds_TwoSides_WithHeader()
        {
            var path = WriteFile("test.fds", GenerateFdsFile(2, true));
            AssertHash(ConsoleNintendo, path, "fd770d4d34c00760fabda6ad294a8f0b");
        }

        [TestMethod]
        public void Nes_File_32k()
        {
            var path = WriteFile("test.nes", GenerateNesFile(32, false));
            AssertHash(ConsoleNintendo, path, "6a2305a2b6675a97ff792709be1ca857");
        }

        [TestMethod]
        public void Nes_Iterator_32k()
        {
            var path = WriteFile("test.nes", GenerateNesFile(32, false));
            var hashes = Hash(ConsoleNintendo, path);
            CollectionAssert.AreEqual(new[] { "6a2305a2b6675a97ff792709be1ca857" }, hashes.ToArray());
        }

        [TestMethod]
        public void Nes_File_Iterator_32k()
        {
            var path = WriteFile("test.nes", GenerateNesFile(32, false));
            var hashes = Hash(ConsoleNintendo, path);
            CollectionAssert.AreEqual(new[] { "6a2305a2b6675a97ff792709be1ca857" }, hashes.ToArray());
        }

        // ===== Nintendo 64 (test_hash_rom.c: test_hash_n64 / test_hash_n64_file) =====

        private const string N64Md5 = "06096d7ce21cb6bcde38391534c4eb91";

        [TestMethod] public void N64_Z64() => AssertHash(ConsoleNintendo64, WriteFile("game.z64", TestRomZ64), N64Md5);
        [TestMethod] public void N64_V64() => AssertHash(ConsoleNintendo64, WriteFile("game.v64", TestRomV64), N64Md5);
        [TestMethod] public void N64_N64() => AssertHash(ConsoleNintendo64, WriteFile("game.n64", TestRomN64), N64Md5);
        [TestMethod] public void N64_File_Z64() => AssertHash(ConsoleNintendo64, WriteFile("game.z64", TestRomZ64), N64Md5);
        [TestMethod] public void N64_File_V64() => AssertHash(ConsoleNintendo64, WriteFile("game.v64", TestRomV64), N64Md5);
        [TestMethod] public void N64_File_N64() => AssertHash(ConsoleNintendo64, WriteFile("game.n64", TestRomN64), N64Md5);
        [TestMethod] public void N64_File_N64MisnamedZ64() => AssertHash(ConsoleNintendo64, WriteFile("game.n64", TestRomZ64), N64Md5);
        [TestMethod] public void N64_File_Z64MisnamedN64() => AssertHash(ConsoleNintendo64, WriteFile("game.z64", TestRomN64), N64Md5);
        [TestMethod] public void N64_Ndd() => AssertHash(ConsoleNintendo64, WriteFile("game.ndd", TestRomNdd), "a698b32a52970d8a52a5a52c83acc2a9");

        // ===== Nintendo DS / DSi (test_hash_rom.c) =====

        private const string NdsMd5 = "56b30c276cba4affa886bd38e8e34d7e";

        [TestMethod]
        public void Nds()
        {
            var path = WriteFile("game.nds", GenerateNdsFile(2, 1234567, 654321));
            AssertHash(ConsoleNintendoDs, path, NdsMd5);
        }

        [TestMethod]
        public void Nds_Supercard()
        {
            var image = GenerateNdsFile(2, 1234567, 654321);
            var image2 = new byte[image.Length + 512];
            Buffer.BlockCopy(image, 0, image2, 512, image.Length);
            image2[0] = 0x2E;
            image2[1] = 0x00;
            image2[2] = 0x00;
            image2[3] = 0xEA;
            image2[0xB0] = 0x44;
            image2[0xB1] = 0x46;
            image2[0xB2] = 0x96;
            image2[0xB3] = 0x00;

            var path = WriteFile("game.nds", image2);
            AssertHash(ConsoleNintendoDs, path, NdsMd5);
        }

        [TestMethod]
        public void Nds_Buffered()
        {
            var path = WriteFile("game.nds", GenerateNdsFile(2, 1234567, 654321));
            AssertHash(ConsoleNintendoDs, path, NdsMd5);
        }

        [TestMethod]
        public void Dsi()
        {
            var path = WriteFile("game.nds", GenerateNdsFile(2, 1234567, 654321));
            AssertHash(ConsoleNintendoDsi, path, NdsMd5);
        }

        [TestMethod]
        public void Dsi_Buffered()
        {
            var path = WriteFile("game.nds", GenerateNdsFile(2, 1234567, 654321));
            AssertHash(ConsoleNintendoDsi, path, NdsMd5);
        }

        // ===== Super Cassette Vision (test_hash_rom.c) =====

        [TestMethod]
        [Ignore(ScvHeaderNotSkipped)]
        public void ScvCart()
        {
            var image = GenerateGenericFile(32768 + 32);
            CopyAscii("EmuSCV....CART..................", image, 0);
            var path = WriteFile("game.cart", image);
            AssertHash(ConsoleSuperCassetteVision, path, "4309c9844b44f9ff8256dfc04687b8fd");
        }

        // ===== Full-file cartridge rows (test_hash_rom.c: test_hash_full_file / test_hash_m3u) =====

        [TestMethod] public void FullFile_Arcadia2001_Bin() => AssertFullFile(ConsoleArcadia2001, "test.bin", 4096, "572686c3a073162e4ec6eff86e6f6e3a");
        [TestMethod] public void FullFile_Atari2600_Bin() => AssertFullFile(ConsoleAtari2600, "test.bin", 2048, "02c3f2fa186388ba8eede9147fb431c4");
        [TestMethod] public void FullFile_AtariJaguar_Jag() => AssertFullFile(ConsoleAtariJaguar, "test.jag", 0x400000, "a247ec8a8c42e18fcb80702dfadac14b");
        [TestMethod] public void FullFile_Colecovision_Col() => AssertFullFile(ConsoleColecovision, "test.col", 16384, "455f07d8500f3fabc54906737866167f");
        [TestMethod] public void FullFile_ElektorTvGamesComputer_Pgm() => AssertFullFile(ConsoleElektorTvGamesComputer, "test.pgm", 4096, "572686c3a073162e4ec6eff86e6f6e3a");
        [TestMethod] public void FullFile_ElektorTvGamesComputer_Tvc() => AssertFullFile(ConsoleElektorTvGamesComputer, "test.tvc", 1861, "37097124a29aff663432d049654a17dc");
        [TestMethod] public void FullFile_FairchildChannelF_Bin() => AssertFullFile(ConsoleFairchildChannelF, "test.bin", 2048, "02c3f2fa186388ba8eede9147fb431c4");
        [TestMethod] public void FullFile_FairchildChannelF_Chf() => AssertFullFile(ConsoleFairchildChannelF, "test.chf", 2048, "02c3f2fa186388ba8eede9147fb431c4");
        [TestMethod] public void FullFile_Gameboy_Gb() => AssertFullFile(ConsoleGameboy, "test.gb", 131072, "a0f425b23200568132ba76b2405e3933");
        [TestMethod] public void FullFile_GameboyColor_Gbc() => AssertFullFile(ConsoleGameboyColor, "test.gbc", 2097152, "cf86acf519625a25a17b1246975e90ae");
        [TestMethod] public void FullFile_GameboyColor_Gba() => AssertFullFile(ConsoleGameboyColor, "test.gba", 4194304, "a247ec8a8c42e18fcb80702dfadac14b");
        [TestMethod] public void FullFile_GameGear_Gg() => AssertFullFile(ConsoleGameGear, "test.gg", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_Intellivision_Bin() => AssertFullFile(ConsoleIntellivision, "test.bin", 8192, "ce1127f881b40ce6a67ecefba50e2835");
        [TestMethod] public void FullFile_IntertonVc4000_Bin() => AssertFullFile(ConsoleIntertonVc4000, "test.bin", 2048, "02c3f2fa186388ba8eede9147fb431c4");
        [TestMethod] public void FullFile_MagnavoxOdyssey2_Bin() => AssertFullFile(ConsoleMagnavoxOdyssey2, "test.bin", 4096, "572686c3a073162e4ec6eff86e6f6e3a");
        [TestMethod] public void FullFile_MasterSystem_Sms() => AssertFullFile(ConsoleMasterSystem, "test.sms", 131072, "a0f425b23200568132ba76b2405e3933");
        [TestMethod] public void FullFile_MegaDrive_Md() => AssertFullFile(ConsoleMegaDrive, "test.md", 1048576, "da9461b3b0f74becc3ccf6c2a094c516");
        [TestMethod] [Ignore(FullFileM3u)] public void M3u_MegaDrive_Md() => AssertM3u(ConsoleMegaDrive, "test.md", 1048576, "da9461b3b0f74becc3ccf6c2a094c516");
        [TestMethod] public void FullFile_MegaDuck_Bin() => AssertFullFile(ConsoleMegaDuck, "test.bin", 65536, "8e6576cd5c21e44e0bbfc4480577b040");
        [TestMethod] public void FullFile_NeoGeoPocket_Ngc() => AssertFullFile(ConsoleNeoGeoPocket, "test.ngc", 2097152, "cf86acf519625a25a17b1246975e90ae");
        [TestMethod] public void FullFile_Oric_Tap() => AssertFullFile(ConsoleOric, "test.tap", 18119, "953a2baa3232c63286aeae36b2172cef");
        [TestMethod] public void FullFile_PcEngine_Pce() => AssertFullFile(ConsolePcEngine, "test.pce", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_PcEngine_PceWithHeader() => AssertFullFile(ConsolePcEngine, "test.pce", 524288 + 512, "258c93ebaca1c3f488ab48218e5e8d38");
        [TestMethod] public void FullFile_PcEngine_PceOddSizeWithHeader() => AssertFullFile(ConsolePcEngine, "test.pce", 491520 + 512, "ebb565a7f964ccdfaecdce0d6ed540af");
        [TestMethod] public void FullFile_PokemonMini_Min() => AssertFullFile(ConsolePokemonMini, "test.min", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_Sega32x_Bin() => AssertFullFile(ConsoleSega32x, "test.bin", 3145728, "07d733f252896ec41b4fd521fe610e2c");
        [TestMethod] public void FullFile_Sg1000_Sg() => AssertFullFile(ConsoleSg1000, "test.sg", 32768, "6a2305a2b6675a97ff792709be1ca857");
        [TestMethod] public void FullFile_SuperNintendo_Smc() => AssertFullFile(ConsoleSuperNintendo, "test.smc", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_SuperNintendo_SmcWithHeader() => AssertFullFile(ConsoleSuperNintendo, "test.smc", 524288 + 512, "258c93ebaca1c3f488ab48218e5e8d38");
        [TestMethod] public void FullFile_SuperCassetteVision_Bin() => AssertFullFile(ConsoleSuperCassetteVision, "test.bin", 32768, "6a2305a2b6675a97ff792709be1ca857");
        [TestMethod] public void FullFile_Ti83_83g() => AssertFullFile(ConsoleTi83, "test.83g", 1695, "bfb6048395a425c69743900785987c42");
        [TestMethod] public void FullFile_Ti83_83p() => AssertFullFile(ConsoleTi83, "test.83p", 2500, "6e81d530ee9a79d4f4f505729ad74bb5");
        [TestMethod] public void FullFile_Tic80_Tic() => AssertFullFile(ConsoleTic80, "test.tic", 67682, "79b96f4ffcedb3ce8210a83b22cd2c69");
        [TestMethod] public void FullFile_Uzebox_Uze() => AssertFullFile(ConsoleUzebox, "test.uze", 53654, "a9aab505e92edc034d3c732869159789");
        [TestMethod] public void FullFile_Vectrex_Vec() => AssertFullFile(ConsoleSg1000, "test.vec", 4096, "572686c3a073162e4ec6eff86e6f6e3a");
        [TestMethod] public void FullFile_VirtualBoy_Vb() => AssertFullFile(ConsoleSg1000, "test.vb", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_Supervision_Sv() => AssertFullFile(ConsoleSupervision, "test.sv", 32768, "6a2305a2b6675a97ff792709be1ca857");
        [TestMethod] public void FullFile_Wasm4_Wasm() => AssertFullFile(ConsoleWasm4, "test.wasm", 33454, "bce38bb5f05622fc7e0e56757059d180");
        [TestMethod] public void FullFile_WonderSwan_Ws() => AssertFullFile(ConsoleWonderSwan, "test.ws", 524288, "68f0f13b598e0b66461bc578375c3888");
        [TestMethod] public void FullFile_WonderSwan_Wsc() => AssertFullFile(ConsoleWonderSwan, "test.wsc", 4194304, "a247ec8a8c42e18fcb80702dfadac14b");

        // ===== Helpers =====

        // test_hash.c: test_hash_full_file
        private void AssertFullFile(int consoleId, string filename, int size, string expectedMd5)
        {
            var path = WriteFile(filename, GenerateGenericFile(size));
            AssertHash(consoleId, path, expectedMd5);
        }

        // test_hash.c: test_hash_m3u
        private void AssertM3u(int consoleId, string filename, int size, string expectedMd5)
        {
            WriteFile(filename, GenerateGenericFile(size));
            var path = WriteText("test.m3u", filename);
            AssertHash(consoleId, path, expectedMd5);
        }

        private static IReadOnlyList<string> Hash(int consoleId, string path)
        {
            var hasher = RaHasherFactory.Create(consoleId, new PlayniteAchievementsSettings(), logger: null);
            Assert.IsNotNull(hasher, $"No hasher for console {consoleId}.");
            return hasher.ComputeHashesAsync(path, CancellationToken.None).GetAwaiter().GetResult()
                   ?? Array.Empty<string>();
        }

        private static void AssertHash(int consoleId, string path, string expectedMd5)
        {
            var hashes = Hash(consoleId, path);
            Assert.IsTrue(
                hashes.Contains(expectedMd5, StringComparer.OrdinalIgnoreCase),
                $"Expected {expectedMd5}; plugin returned [{string.Join(", ", hashes)}].");
        }

        private static void AssertNoHash(int consoleId, string path)
        {
            var hashes = Hash(consoleId, path);
            Assert.AreEqual(0, hashes.Count, $"Expected no hash; plugin returned [{string.Join(", ", hashes)}].");
        }

        private string WriteFile(string relativePath, byte[] data)
        {
            var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(_dir, relativePath);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(path, data);
            return path;
        }

        private string WriteText(string relativePath, string contents)
        {
            return WriteFile(relativePath, Encoding.ASCII.GetBytes(contents));
        }

        // A cue sheet equivalent to rcheevos's mock cdreader mapping "game.cue" onto "game.bin".
        private string WriteMockCue(string cueName, string binName)
        {
            return WriteText(cueName, $"FILE \"{binName}\" BINARY\r\n  TRACK 01 MODE1/2048\r\n    INDEX 01 00:00:00\r\n");
        }

        private string WriteMockCueImage(byte[] image)
        {
            WriteFile("game.bin", image);
            return WriteMockCue("game.cue", "game.bin");
        }

        // Rebuilds an image produced by GenerateIso9660Bin/GenerateIso9660File as a standard ISO9660 + Joliet
        // image with the same directory tree, file names, and file bytes.
        private static byte[] RepackAsStandardIso(byte[] image)
        {
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TEST" };
            var descriptor = 16 * 2048;
            var rootSector = image[descriptor + 158] | (image[descriptor + 159] << 8) | (image[descriptor + 160] << 16);
            AddRcheevosDirectory(builder, image, rootSector, string.Empty);

            using (var built = builder.Build())
            using (var buffer = new MemoryStream())
            {
                built.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static void AddRcheevosDirectory(CDBuilder builder, byte[] image, int sector, string prefix)
        {
            var start = sector * 2048;
            for (var entry = start; entry < start + 2048 && image[entry] != 0; entry += image[entry])
            {
                var name = Encoding.ASCII.GetString(image, entry + 33, image[entry + 32]);
                var target = image[entry + 2] | (image[entry + 3] << 8) | (image[entry + 4] << 16);

                if (image[entry + 25] != 0)
                {
                    builder.AddDirectory(prefix + name);
                    AddRcheevosDirectory(builder, image, target, prefix + name + "\\");
                    continue;
                }

                var semicolon = name.IndexOf(';');
                if (semicolon >= 0)
                {
                    name = name.Substring(0, semicolon);
                }

                var size = image[entry + 10] | (image[entry + 11] << 8) | (image[entry + 12] << 16) | (image[entry + 13] << 24);
                var data = new byte[size];
                Buffer.BlockCopy(image, target * 2048, data, 0, size);
                builder.AddFile(prefix + name, data);
            }
        }

        private static void AddIsoFile(byte[] image, string name, string contents)
        {
            var bytes = Encoding.ASCII.GetBytes(contents);
            GenerateIso9660File(image, name, bytes, bytes.Length);
        }

        private static void AddIsoFile(byte[] image, string name, byte[] contents)
        {
            GenerateIso9660File(image, name, contents, contents.Length);
        }

        private static byte[] Prefix(byte[] data, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(data, 0, result, 0, length);
            return result;
        }
    }
}
