using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class UnlockSoundResolverTests
    {
        private string _root;
        private string _bundled;
        private string _themeA;
        private string _themeB;
        private UnlockSoundSettings _settings;
        private CapturingLogger _logger;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", "UnlockSoundResolver", Guid.NewGuid().ToString("N"));
            _bundled = Path.Combine(_root, "bundled");
            _themeA = Path.Combine(_root, "themeA");
            _themeB = Path.Combine(_root, "themeB");
            Directory.CreateDirectory(_bundled);
            Directory.CreateDirectory(_themeA);
            Directory.CreateDirectory(_themeB);
            foreach (var tier in UnlockSoundTierExtensions.All)
            {
                Touch(Path.Combine(_bundled, tier.ToFileBaseName() + ".mp3"));
            }

            _settings = new UnlockSoundSettings();
            _logger = new CapturingLogger();
        }

        [TestCleanup]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        [TestMethod]
        public void ToFileBaseName_IsBareLowercaseForEveryTier()
        {
            CollectionAssert.AreEqual(
                new[] { "common", "uncommon", "rare", "ultrarare", "hidden", "capstone" },
                UnlockSoundTierExtensions.All.Select(t => t.ToFileBaseName()).ToArray());
        }

        [TestMethod]
        public void Resolve_FallsToBundledWhenNothingElseExists()
        {
            var resolved = Create().Resolve(UnlockSoundTier.Rare);

            Assert.AreEqual(UnlockSoundSource.Default, resolved.Source);
            Assert.AreEqual(Path.Combine(_bundled, "rare.mp3"), resolved.Path);
        }

        [TestMethod]
        public void Resolve_CustomPathWinsOverThemeAndBundled()
        {
            var custom = Touch(Path.Combine(_root, "mine.wav"));
            Touch(Path.Combine(_themeA, UnlockSoundResolver.ThemeSoundsRelativeDirectory, "rare.wav"));
            _settings.Rare = custom;

            var resolved = Create().Resolve(UnlockSoundTier.Rare);

            Assert.AreEqual(UnlockSoundSource.Custom, resolved.Source);
            Assert.AreEqual(custom, resolved.Path);
        }

        [TestMethod]
        public void Resolve_MissingCustomPathFallsThroughAndLogsOnce()
        {
            _settings.Rare = Path.Combine(_root, "gone.wav");
            var resolver = Create();

            var first = resolver.Resolve(UnlockSoundTier.Rare);
            var second = resolver.Resolve(UnlockSoundTier.Rare);

            Assert.AreEqual(UnlockSoundSource.Default, first.Source);
            Assert.AreEqual(UnlockSoundSource.Default, second.Source);
            Assert.AreEqual(1, _logger.Messages.Count(m => m.Contains("is missing")));
        }

        [TestMethod]
        public void Resolve_NewThemeLayoutBeatsLegacyLayoutInTheSameTheme()
        {
            var modern = Touch(Path.Combine(_themeA, UnlockSoundResolver.ThemeSoundsRelativeDirectory, "common.mp3"));
            Touch(Path.Combine(_themeA, UnlockSoundResolver.LegacyThemeSoundsRelativeDirectory, "common.wav"));

            var resolved = Create().Resolve(UnlockSoundTier.Common);

            Assert.AreEqual(UnlockSoundSource.Theme, resolved.Source);
            Assert.AreEqual(modern, resolved.Path);
        }

        [TestMethod]
        public void Resolve_LegacyLayoutIsAcceptedWhenTheNewOneIsAbsent()
        {
            var legacy = Touch(Path.Combine(_themeA, UnlockSoundResolver.LegacyThemeSoundsRelativeDirectory, "ultrarare.flac"));

            var resolved = Create().Resolve(UnlockSoundTier.UltraRare);

            Assert.AreEqual(UnlockSoundSource.Theme, resolved.Source);
            Assert.AreEqual(legacy, resolved.Path);
        }

        [TestMethod]
        public void Resolve_FirstThemeDirectoryWins()
        {
            var a = Touch(Path.Combine(_themeA, UnlockSoundResolver.ThemeSoundsRelativeDirectory, "hidden.wav"));
            Touch(Path.Combine(_themeB, UnlockSoundResolver.ThemeSoundsRelativeDirectory, "hidden.wav"));

            var resolved = Create().Resolve(UnlockSoundTier.Hidden);

            Assert.AreEqual(a, resolved.Path);
        }

        [TestMethod]
        public void Resolve_ProbesWavThenMp3ThenFlac()
        {
            var dir = Path.Combine(_themeA, UnlockSoundResolver.ThemeSoundsRelativeDirectory);
            Touch(Path.Combine(dir, "capstone.flac"));
            var mp3 = Touch(Path.Combine(dir, "capstone.mp3"));

            Assert.AreEqual(mp3, Create().Resolve(UnlockSoundTier.Capstone).Path);

            var wav = Touch(Path.Combine(dir, "capstone.wav"));
            Assert.AreEqual(wav, Create().Resolve(UnlockSoundTier.Capstone).Path);
        }

        [TestMethod]
        public void Resolve_SkipsOggWithOneLogLineAndUsesBundled()
        {
            Touch(Path.Combine(_themeA, UnlockSoundResolver.LegacyThemeSoundsRelativeDirectory, "uncommon.ogg"));
            var resolver = Create();

            var first = resolver.Resolve(UnlockSoundTier.Uncommon);
            resolver.Resolve(UnlockSoundTier.Uncommon);

            Assert.AreEqual(UnlockSoundSource.Default, first.Source);
            Assert.AreEqual(1, _logger.Messages.Count(m => m.Contains(".ogg is not supported")));
        }

        [TestMethod]
        public void Resolve_ReturnsNoneWhenBundledIsMissing()
        {
            File.Delete(Path.Combine(_bundled, "common.mp3"));

            var resolved = Create().Resolve(UnlockSoundTier.Common);

            Assert.AreEqual(UnlockSoundSource.None, resolved.Source);
            Assert.IsNull(resolved.Path);
            Assert.IsTrue(_logger.Messages.Any(m => m.Contains("Bundled sound missing")));
        }

        [TestMethod]
        public void ResolveAll_ReturnsSixRowsInTierOrder()
        {
            var all = Create().ResolveAll();

            CollectionAssert.AreEqual(UnlockSoundTierExtensions.All, all.Select(r => r.Tier).ToArray());
            Assert.IsTrue(all.All(r => r.Source == UnlockSoundSource.Default));
        }

        [TestMethod]
        public void Resolve_SurvivesAThrowingThemeDirectoryProvider()
        {
            var resolver = new UnlockSoundResolver(
                () => _settings,
                () => throw new InvalidOperationException("boom"),
                _bundled,
                _logger);

            Assert.AreEqual(UnlockSoundSource.Default, resolver.Resolve(UnlockSoundTier.Rare).Source);
        }

        [TestMethod]
        public void BuildOpenFileDialogFilter_ListsTheProbedExtensions()
        {
            Assert.AreEqual(
                "Audio Files (*.wav;*.mp3;*.flac)|*.wav;*.mp3;*.flac",
                UnlockSoundResolver.BuildOpenFileDialogFilter());
        }

        private UnlockSoundResolver Create()
        {
            return new UnlockSoundResolver(
                () => _settings,
                () => new List<string> { _themeA, _themeB },
                _bundled,
                _logger);
        }

        private static string Touch(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 0 });
            return path;
        }

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Messages = new List<string>();

            public void Debug(string message) => Messages.Add(message);
            public void Debug(Exception exception, string message) => Messages.Add(message);
            public void Error(string message) => Messages.Add(message);
            public void Error(Exception exception, string message) => Messages.Add(message);
            public void Info(string message) => Messages.Add(message);
            public void Info(Exception exception, string message) => Messages.Add(message);
            public void Trace(string message) => Messages.Add(message);
            public void Trace(Exception exception, string message) => Messages.Add(message);
            public void Warn(string message) => Messages.Add(message);
            public void Warn(Exception exception, string message) => Messages.Add(message);
        }
    }
}
