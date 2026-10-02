using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using System;
using System.IO;
using System.Text;
using static PlayniteAchievements.Tests.Providers.RetroAchievements.RcheevosFixtureData;

namespace PlayniteAchievements.Tests.Providers.RetroAchievements
{
    /// <summary>
    /// Track selection and sector layout parity with rcheevos test/rhash/test_cdreader.c. rcheevos
    /// mocks empty bin files of a given size; here they are real zero-filled files of that length.
    /// </summary>
    [TestClass]
    public class DiscImageTrackTests
    {
        private static readonly byte[] SyncPattern = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        private const string CueSingleBinMultipleData =
            "FILE \"game.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "  TRACK 02 MODE1/2352\n" +
            "    PREGAP 00:03:00\n" +
            "    INDEX 01 00:55:45\n" +
            "  TRACK 03 MODE1/2352\n" +
            "    INDEX 01 11:30:74\n" +
            "  TRACK 04 MODE1/2352\n" +
            "    INDEX 01 13:31:51\n" +
            "  TRACK 05 MODE1/2352\n" +
            "    INDEX 01 13:48:56\n" +
            "  TRACK 06 MODE1/2352\n" +
            "    INDEX 01 34:48:19\n" +
            "  TRACK 07 MODE1/2352\n" +
            "    INDEX 01 50:42:74\n" +
            "  TRACK 08 MODE1/2352\n" +
            "    INDEX 01 55:20:74\n" +
            "  TRACK 09 MODE1/2352\n" +
            "    INDEX 01 56:25:67\n" +
            "  TRACK 10 MODE1/2352\n" +
            "    INDEX 01 59:04:08\n" +
            "  TRACK 11 MODE1/2352\n" +
            "    INDEX 01 61:17:18\n" +
            "  TRACK 12 MODE1/2352\n" +
            "    INDEX 01 62:44:33\n" +
            "  TRACK 13 AUDIO\n" +
            "    PREGAP 00:02:00\n" +
            "    INDEX 01 66:24:37\n";

        private const string CueMultipleBinMultipleData =
            "FILE \"track1.bin\" BINARY\n" +
            "  TRACK 01 AUDIO\n" +
            "    INDEX 01 00:00:00\n" +
            "FILE \"track2.bin\" BINARY\n" +
            "  TRACK 02 MODE1/2352\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:03:00\n" +
            "FILE \"track3.bin\" BINARY\n" +
            "  TRACK 03 MODE1/2352\n" +
            "    INDEX 00 00:00:00\n" +
            "    INDEX 01 00:02:00\n" +
            "FILE \"track4.bin\" BINARY\n" +
            "  TRACK 04 AUDIO\n" +
            "    INDEX 00 00:00:00\n";

        private const string CueSingleTrack =
            "FILE \"game.bin\" BINARY\n" +
            "  TRACK 01 MODE2/2352\n" +
            "    INDEX 01 00:00:00\n";

        private string _dir;

        [TestInitialize]
        public void Initialize()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsDiscImage_" + Guid.NewGuid().ToString("N"));
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

        [TestMethod]
        public void Cue_Track2()
        {
            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.Track(2)))
            {
                AssertTrack(track, "game.bin", 9807840, 2352, 16);
                Assert.AreEqual(2048, track.RawDataSize);
            }
        }

        [TestMethod]
        public void Cue_Track12()
        {
            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.Track(12)))
            {
                AssertTrack(track, "game.bin", 664047216, 2352, 16);
            }
        }

        [TestMethod]
        public void Cue_Track14_NotPresent()
        {
            EmptyFile("game.bin", 718310208);
            Assert.IsNull(Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.Track(14)));
        }

        [TestMethod]
        public void Cue_MissingBin()
        {
            Assert.IsNull(Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.Track(2)));
        }

        [TestMethod]
        public void Cue_FirstData()
        {
            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.FirstData))
            {
                AssertTrack(track, "game.bin", 9807840, 2352, 16);
                Assert.AreEqual(0, track.PregapSectors);
            }
        }

        [TestMethod]
        public void Cue_LargestData()
        {
            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", CueSingleBinMultipleData, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "game.bin", 146190912, 2352, 16);
            }
        }

        [TestMethod]
        public void Cue_LargestData_MultipleBin()
        {
            EmptyFile("track2.bin", 406423248);
            EmptyFile("track3.bin", 11553024);
            using (var track = Open("game.cue", CueMultipleBinMultipleData, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "track2.bin", 0, 2352, 16);
                Assert.AreEqual(225, track.PregapSectors);
            }
        }

        [TestMethod]
        public void Cue_LargestData_LastTrack()
        {
            const string cue =
                "FILE \"game.bin\" BINARY\n" +
                "  TRACK 01 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "  TRACK 02 MODE1/2352\n" +
                "    PREGAP 00:03:00\n" +
                "    INDEX 01 00:55:45\n" +
                "  TRACK 03 MODE1/2352\n" +
                "    INDEX 01 11:30:74\n" +
                "  TRACK 04 MODE1/2352\n" +
                "    INDEX 01 13:31:51\n" +
                "  TRACK 05 MODE1/2352\n" +
                "    INDEX 01 13:48:56\n";

            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", cue, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "game.bin", 146190912, 2352, 16);
            }
        }

        [TestMethod]
        public void Cue_LargestData_Index0s()
        {
            const string cue =
                "FILE \"game.bin\" BINARY\n" +
                "  TRACK 01 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "  TRACK 02 MODE1/2352\n" +
                "    INDEX 00 00:44:65\n" +
                "    INDEX 01 00:47:65\n" +
                "  TRACK 03 AUDIO\n" +
                "    INDEX 00 01:19:52\n" +
                "    INDEX 01 01:21:52\n";

            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", cue, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "game.bin", 7914480, 2352, 16);
                Assert.AreEqual(225, track.PregapSectors);
            }
        }

        [TestMethod]
        public void Cue_LargestData_Index2()
        {
            const string cue =
                "FILE \"game.bin\" BINARY\n" +
                "  TRACK 01 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "  TRACK 02 MODE1/2352\n" +
                "    INDEX 00 00:00:00\n" +
                "    INDEX 01 00:02:00\n" +
                "    INDEX 02 00:08:64\n";

            EmptyFile("game.bin", 718310208);
            using (var track = Open("game.cue", cue, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "game.bin", 0, 2352, 16);
                Assert.AreEqual(150, track.PregapSectors);
            }
        }

        [TestMethod]
        public void Cue_LargestData_MultipleBins()
        {
            EmptyFile("track1.bin", 4132464);
            EmptyFile("track2.bin", 30080102);
            EmptyFile("track3.bin", 40343152);
            EmptyFile("track4.bin", 47277552);
            using (var track = Open("game.cue", CueMultipleBinMultipleData, DiscTrackSelector.Largest))
            {
                AssertTrack(track, "track3.bin", 0, 2352, 16);
                Assert.AreEqual(150, track.PregapSectors);
            }
        }

        [TestMethod]
        public void Cue_LargestData_OnlyAudio()
        {
            const string cue =
                "FILE \"track1.bin\" BINARY\n" +
                "  TRACK 01 AUDIO\n" +
                "    INDEX 01 00:00:00\n" +
                "FILE \"track2.bin\" BINARY\n" +
                "  TRACK 02 AUDIO\n" +
                "    INDEX 00 00:00:00\n" +
                "    INDEX 01 00:03:00\n" +
                "FILE \"track3.bin\" BINARY\n" +
                "  TRACK 03 AUDIO\n" +
                "    INDEX 00 00:00:00\n" +
                "    INDEX 01 00:02:00\n" +
                "FILE \"track4.bin\" BINARY\n" +
                "  TRACK 04 AUDIO\n" +
                "    INDEX 00 00:00:00\n";

            EmptyFile("track1.bin", 4132464);
            EmptyFile("track2.bin", 30080102);
            EmptyFile("track3.bin", 40343152);
            EmptyFile("track4.bin", 47277552);
            Assert.IsNull(Open("game.cue", cue, DiscTrackSelector.Largest));
        }

        [TestMethod]
        public void Gdi_Track3()
        {
            EmptyFile("track03.bin", 2352 * 32);
            using (var track = Open("game.gdi", "3\n1 0 4 2352 track01.bin 0\n2 600 0 2352 track02.raw 0\n3 45000 4 2352 track03.bin 0", DiscTrackSelector.Track(3)))
            {
                AssertTrack(track, "track03.bin", 0, 2352, 16);
                Assert.AreEqual(0, track.PregapSectors);
                Assert.AreEqual(45000, track.TrackFirstSector);
                Assert.AreEqual(2048, track.RawDataSize);
            }
        }

        [TestMethod]
        public void Gdi_Track3_QuotedWithExtraWhitespace()
        {
            EmptyFile("track 03.bin", 2352 * 32);
            const string gdi =
                "3\n\n" +
                "  1       0   4   2352   \"track 01.bin\"   0\n\n" +
                "  2     600   0   2352   \"track 02.raw\"   0\n\n" +
                "  3   45000   4   2352   \"track 03.bin\"   0\n\n";
            using (var track = Open("game.gdi", gdi, DiscTrackSelector.Track(3)))
            {
                AssertTrack(track, "track 03.bin", 0, 2352, 16);
                Assert.AreEqual(45000, track.TrackFirstSector);
            }
        }

        [TestMethod]
        public void Gdi_LastTrack()
        {
            EmptyFile("track26.bin", 2352 * 32);
            var gdi = new StringBuilder("26\n1 0 4 2352 track01.bin 0\n2 450 0 2352 track02.raw 0\n3 45000 4 2352 track03.bin 0\n");
            for (var i = 4; i <= 25; i++)
            {
                gdi.Append(i).Append(' ').Append(370000 + i * 1000).Append(" 0 2352 track").Append(i.ToString("00")).Append(".raw 0\n");
            }

            gdi.Append("26 548106 4 2352 track26.bin 0\n");
            using (var track = Open("game.gdi", gdi.ToString(), DiscTrackSelector.Last))
            {
                AssertTrack(track, "track26.bin", 0, 2352, 16);
                Assert.AreEqual(548106, track.TrackFirstSector);
            }
        }

        [DataTestMethod]
        [DataRow(2352)]
        [DataRow(2336)]
        public void DetermineSectorSize_Sync(int sectorSize)
        {
            var image = new byte[sectorSize * 32];
            Buffer.BlockCopy(SyncPattern, 0, image, sectorSize * 16, SyncPattern.Length);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), image);

            using (var track = Open("game.cue", CueSingleTrack, DiscTrackSelector.Track(1)))
            {
                AssertTrack(track, "game.bin", 0, sectorSize, 16);
                Assert.AreEqual(2048, track.RawDataSize);
            }
        }

        [DataTestMethod]
        [DataRow(2352)]
        [DataRow(2336)]
        public void DetermineSectorSize_SyncPrimaryVolumeDescriptor(int sectorSize)
        {
            var image = new byte[sectorSize * 32];
            Buffer.BlockCopy(SyncPattern, 0, image, sectorSize * 16, SyncPattern.Length);
            CopyAscii("CD001", image, (sectorSize * 16) + 25);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), image);

            using (var track = Open("game.cue", CueSingleTrack, DiscTrackSelector.Track(1)))
            {
                AssertTrack(track, "game.bin", 0, sectorSize, 24);
            }
        }

        [DataTestMethod]
        [DataRow(2352)]
        [DataRow(2336)]
        public void DetermineSectorSize_SyncPrimaryVolumeDescriptor_Index0(int sectorSize)
        {
            var cue = "FILE \"game.bin\" BINARY\n  TRACK 01 MODE2/" + sectorSize + "\n    INDEX 00 00:00:00\n    INDEX 01 00:02:00\n";
            var image = new byte[sectorSize * 200];
            Buffer.BlockCopy(SyncPattern, 0, image, sectorSize * (150 + 16), SyncPattern.Length);
            CopyAscii("CD001", image, (sectorSize * (150 + 16)) + 25);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), image);

            using (var track = Open("game.cue", cue, DiscTrackSelector.Track(1)))
            {
                AssertTrack(track, "game.bin", 0, sectorSize, 24);
                Assert.AreEqual(150, track.PregapSectors);
            }
        }

        [TestMethod]
        public void DetermineSectorSize_2048WithoutDescriptor_UsesCueMode()
        {
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[2048 * 32]);
            using (var track = Open("game.cue", CueSingleTrack, DiscTrackSelector.Track(1)))
            {
                AssertTrack(track, "game.bin", 0, 2352, 24);
            }
        }

        [TestMethod]
        public void DetermineSectorSize_2048PrimaryVolumeDescriptor()
        {
            var image = new byte[2048 * 32];
            CopyAscii("CD001", image, (2048 * 16) + 1);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), image);
            using (var track = Open("game.cue", CueSingleTrack, DiscTrackSelector.Track(1)))
            {
                AssertTrack(track, "game.bin", 0, 2048, 0);
            }
        }

        [TestMethod]
        public void BinWithoutCue_RawSectorsDetectedFromSyncAndMsf()
        {
            var cooked = GenerateGenericFile(70 * 2048);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), ConvertTo2352((byte[])cooked.Clone(), 0));

            using (var track = DiscImage.Open(RaHashSource.FromFile(Path.Combine(_dir, "game.bin"))).OpenTrack(DiscTrackSelector.Track(1)))
            {
                Assert.IsNotNull(track);
                Assert.AreEqual(2352, track.SectorSize);
                Assert.AreEqual(16, track.HeaderSize);
                Assert.AreEqual(0, track.TrackFirstSector);
            }
        }

        [TestMethod]
        public void BinWithoutCue_SecondaryTrackNotAvailable()
        {
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), new byte[2048 * 32]);
            Assert.IsNull(DiscImage.Open(RaHashSource.FromFile(Path.Combine(_dir, "game.bin"))).OpenTrack(DiscTrackSelector.Track(2)));
        }

        // The payload view and sector runs over raw sectors must return the cooked bytes, across batch
        // boundaries and at a partial final sector.
        [TestMethod]
        public void RawTrack_PayloadViewAndSectorRunsMatchCookedImage()
        {
            var cooked = GenerateGenericFile(70 * 2048);
            var raw = ConvertTo2352((byte[])cooked.Clone(), 0);
            var truncated = new byte[raw.Length - 2352 + 16 + 1000];
            Buffer.BlockCopy(raw, 0, truncated, 0, truncated.Length);
            File.WriteAllBytes(Path.Combine(_dir, "game.bin"), truncated);

            var expectedLength = (69 * 2048) + 1000;
            using (var track = Open("game.cue", "FILE \"game.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n", DiscTrackSelector.FirstData))
            using (var view = track.OpenPayloadStream())
            {
                Assert.AreEqual(expectedLength, view.Length);

                view.Position = 2040;
                var buffer = new byte[expectedLength];
                var read = HashUtils.ReadFull(view, buffer, 0, buffer.Length);
                Assert.AreEqual(expectedLength - 2040, read);
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != cooked[2040 + i]) Assert.Fail($"View byte {2040 + i} differs.");
                }

                var run = new byte[40 * 2048];
                var runRead = track.ReadSectorRun(35, 40, 2048, run, 0);
                Assert.AreEqual((34 * 2048) + 1000, runRead);
                for (var i = 0; i < runRead; i++)
                {
                    if (run[i] != cooked[(35 * 2048) + i]) Assert.Fail($"Run byte {i} differs.");
                }
            }
        }

        private DiscTrack Open(string name, string contents, DiscTrackSelector selector)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, contents, Encoding.ASCII);
            return DiscImage.Open(RaHashSource.FromFile(path)).OpenTrack(selector);
        }

        private void EmptyFile(string name, long size)
        {
            using (var stream = new FileStream(Path.Combine(_dir, name), FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(size);
            }
        }

        private static void AssertTrack(DiscTrack track, string fileName, long fileTrackOffset, int sectorSize, int headerSize)
        {
            Assert.IsNotNull(track);
            Assert.AreEqual(fileName, Path.GetFileName(track.FilePath));
            Assert.AreEqual(fileTrackOffset, track.FileTrackOffset);
            Assert.AreEqual(sectorSize, track.SectorSize);
            Assert.AreEqual(headerSize, track.HeaderSize);
        }
    }
}
