using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass, DoNotParallelize]
    public class NativeSourceIconTests
    {
        private string _root;
        private PlayniteAchievementsPlugin _previousPlugin;
        [TestInitialize] public void Initialize()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayAchNativeIcons-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root); _previousPlugin = PlayniteAchievementsPlugin.Instance;
        }
        [TestCleanup] public void Cleanup()
        {
            PlayniteAchievementsPlugin.Instance = _previousPlugin;
            Directory.Delete(_root, true);
        }
        private string ImageFile()
        {
            var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0 };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(_root, "source.png"); using (var stream = File.Create(file)) encoder.Save(stream);
            return file;
        }
        [TestMethod] public async Task ImportUsesUniqueManagedFilesAndNeverOverwritesTheSavedIcon()
        {
            var original = ImageFile(); var originalBytes = File.ReadAllBytes(original);
            using (var service = new DiskImageService(null, Path.Combine(_root, "cache")))
            {
                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin { DiskImageService = service };
                var first = await NativeSourceIconService.ImportAsync("ghostship", original);
                Assert.IsNotNull(first); var savedBytes = File.ReadAllBytes(first);
                var second = await NativeSourceIconService.ImportAsync("ghostship", original);
                Assert.IsNotNull(second); Assert.AreNotEqual(first, second);
                StringAssert.Contains(first, Path.Combine("provider_icons", "native"));
                CollectionAssert.AreEqual(savedBytes, File.ReadAllBytes(first));
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(original));
            }
        }
        [TestMethod] public void EditingAndClearingIconsKeepsSavedSelectionAndFilesIntact()
        {
            var file = ImageFile();
            var saved = new LocalSettings { CustomProviderIconPath = "unrelated-local-icon.png" };
            saved.NativeSourceIcons["ghostship"] = new NativeSourceIconSettings { CustomIconPath = file };
            saved.NativeSourceIcons["dusklight"] = new NativeSourceIconSettings { BorrowedProviderIconKey = "PSN" };
            var edit = (LocalSettings)saved.Clone();
            var changed = edit.NativeSourceIcons["ghostship"].Copy(); changed.CustomIconPath = "";
            edit.NativeSourceIcons = new Dictionary<string, NativeSourceIconSettings>(edit.NativeSourceIcons) { ["ghostship"] = changed };
            Assert.AreEqual(file, saved.NativeSourceIcons["ghostship"].CustomIconPath);
            Assert.AreEqual("PSN", edit.NativeSourceIcons["dusklight"].BorrowedProviderIconKey);
            Assert.AreEqual("unrelated-local-icon.png", edit.CustomProviderIconPath); Assert.IsTrue(File.Exists(file));
        }
        [TestMethod] public void NativeCustomImageRendersWithoutLockingTheSelectedFile()
        {
            var file = ImageFile();
            var image = ProviderIconConverter.BuildIcon(NativeSourceIconService.FilePrefix + file, "#E60012");
            Assert.IsNotNull(image); Assert.IsTrue(image.IsFrozen); Assert.IsInstanceOfType(image.Drawing, typeof(ImageDrawing));
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.IsTrue(stream.Length > 0);
        }
        [TestMethod] public void CustomImagePrecedesBorrowedIconAndMissingFileFallsBackToOriginalConsole()
        {
            var file = ImageFile(); var settings = new LocalSettings();
            settings.NativeSourceIcons["ghostship"] = new NativeSourceIconSettings { CustomIconPath = file, BorrowedProviderIconKey = "PSN" };
            Assert.AreEqual(NativeSourceIconService.FilePrefix + file, NativeSourceIconService.IconKey(NativeSourceCatalog.Find("ghostship"), settings));
            settings.NativeSourceIcons["ghostship"].BorrowedProviderIconKey = ""; File.Delete(file);
            Assert.AreEqual(NativeSourceIconService.ConsolePrefix + "n64", NativeSourceIconService.IconKey(NativeSourceCatalog.Find("ghostship"), settings));
        }
        [TestMethod] public void LocationResetDoesNotResetIconSelection()
        {
            var settings = new LocalSettings(); settings.NativeSourceIcons["ghostship"] = new NativeSourceIconSettings { CustomIconPath = "saved.png" };
            settings.NativeSourceLocations["ghostship"] = new NativeSourceLocations();
            var copy = (LocalSettings)settings.Clone();
            Assert.AreEqual("saved.png", copy.NativeSourceIcons["ghostship"].CustomIconPath);
        }
    }
}
