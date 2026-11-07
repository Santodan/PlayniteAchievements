using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileValueParserTests
    {
        [TestMethod]
        public void Booleans_WordsNumbersAndSchemaValues()
        {
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean(" Yes ", null, out var yes) && yes);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("achieved", null, out var achieved) && achieved);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("1.000000", null, out var one) && one);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("0.000000", null, out var zero) && !zero);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("Locked", null, out var locked) && !locked);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("No", null, out var no) && !no);
            Assert.IsFalse(LinkedFileValueParser.TryParseBoolean("maybe", null, out _));
            Assert.IsFalse(LinkedFileValueParser.TryParseBoolean("", null, out _));

            var custom = new List<string> { "Done" };
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("done", custom, out var done) && done);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("true", custom, out var notListed) && !notListed, "The schema's own values replace the defaults.");

            var levels = new List<string> { "2" };
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("2.0", levels, out var two) && two, "Listed numbers match by value.");
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("1", levels, out var oneLevel) && !oneLevel);
            Assert.IsTrue(LinkedFileValueParser.TryParseBoolean("0", levels, out var zeroLevel) && !zeroLevel);
        }

        [TestMethod]
        public void Integers_PlainAndIntegralDecimals()
        {
            Assert.IsTrue(LinkedFileValueParser.TryParseInt("12", out var plain) && plain == 12);
            Assert.IsTrue(LinkedFileValueParser.TryParseInt(" 12.0 ", out var decimalText) && decimalText == 12);
            Assert.IsFalse(LinkedFileValueParser.TryParseInt("12.5", out _));
            Assert.IsFalse(LinkedFileValueParser.TryParseInt("x", out _));
        }

        [TestMethod]
        public void Times_AutoDetectsUnixSecondsMillisecondsAndFileTime()
        {
            var schema = new LinkedFileSchema();
            var expected = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("1700000000", schema, out var seconds));
            Assert.AreEqual(expected, seconds);
            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("1700000000000", schema, out var milliseconds));
            Assert.AreEqual(expected, milliseconds);
            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("1700000000.0", schema, out var spreadsheet));
            Assert.AreEqual(expected, spreadsheet);

            var fileTime = expected.ToFileTimeUtc();
            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc(fileTime.ToString(), schema, out var fromFileTime));
            Assert.AreEqual(expected, fromFileTime);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("0", schema, out var zero));
            Assert.IsNull(zero);
            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("  ", schema, out var blank));
            Assert.IsNull(blank);
            Assert.IsFalse(LinkedFileValueParser.TryParseUnlockTimeUtc("-5", schema, out _));
            Assert.IsFalse(LinkedFileValueParser.TryParseUnlockTimeUtc("soon", schema, out _));
        }

        [TestMethod]
        public void Times_IsoTextRespectsZonesAndTheLocalAssumption()
        {
            var utcSchema = new LinkedFileSchema { AssumeLocalTime = false };
            var localSchema = new LinkedFileSchema { AssumeLocalTime = true };
            var expected = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("2026-01-02T03:04:05Z", localSchema, out var zulu));
            Assert.AreEqual(expected, zulu);
            Assert.AreEqual(DateTimeKind.Utc, zulu.Value.Kind);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("2026-01-02T05:04:05+02:00", localSchema, out var offset));
            Assert.AreEqual(expected, offset);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("2026-01-02T03:04:05", utcSchema, out var assumedUtc));
            Assert.AreEqual(expected, assumedUtc);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("2026-01-02 03:04:05", localSchema, out var assumedLocal));
            Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local).ToUniversalTime(), assumedLocal);
            Assert.AreEqual(DateTimeKind.Utc, assumedLocal.Value.Kind);
        }

        [TestMethod]
        public void Times_ExplicitFormats()
        {
            var expected = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("1700000000", new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.UnixSeconds }, out var seconds));
            Assert.AreEqual(expected, seconds);
            Assert.IsFalse(LinkedFileValueParser.TryParseUnlockTimeUtc("abc", new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.UnixSeconds }, out _));

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("1700000000000", new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.UnixMilliseconds }, out var milliseconds));
            Assert.AreEqual(expected, milliseconds);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc(expected.ToFileTimeUtc().ToString(), new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.WindowsFileTime }, out var fileTime));
            Assert.AreEqual(expected, fileTime);

            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("2023-11-14T22:13:20Z", new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.Iso8601 }, out var iso));
            Assert.AreEqual(expected, iso);

            var custom = new LinkedFileSchema { TimeFormat = LinkedFileTimeFormat.Custom, CustomTimeFormat = "dd.MM.yyyy HH:mm", AssumeLocalTime = false };
            Assert.IsTrue(LinkedFileValueParser.TryParseUnlockTimeUtc("14.11.2023 22:13", custom, out var parsed));
            Assert.AreEqual(new DateTime(2023, 11, 14, 22, 13, 0, DateTimeKind.Utc), parsed);
            Assert.IsFalse(LinkedFileValueParser.TryParseUnlockTimeUtc("2023-11-14", custom, out _));
        }

        [TestMethod]
        public void IconSources_WebAddressesRelativeFilesAndMissingFiles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var icon = Path.Combine(folder, "icon.png");
                File.WriteAllBytes(icon, new byte[] { 1 });

                Assert.AreEqual("https://x/y.png", LinkedFileValueParser.ResolveIconSource("https://x/y.png", folder));
                Assert.AreEqual(icon, LinkedFileValueParser.ResolveIconSource("icon.png", folder));
                Assert.AreEqual(icon, LinkedFileValueParser.ResolveIconSource(icon, folder));
                Assert.IsNull(LinkedFileValueParser.ResolveIconSource("missing.png", folder));
                Assert.AreEqual("kept.png", LinkedFileValueParser.ResolveIconSource("kept.png", null));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
