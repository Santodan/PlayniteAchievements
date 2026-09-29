using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class SharedResourceDictionaryTests
    {
        [TestMethod]
        public void SharedResourceDictionary_ParsesEachSourceOnceAndSharesItAcrossOwners()
        {
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var directory = Path.Combine(
                    Path.GetTempPath(),
                    "PlayAch.SharedDict." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var first = WriteDictionary(directory, "first.xaml", "FirstKey", "first");
                    var second = WriteDictionary(directory, "second.xaml", "SecondKey", "second");

                    var ownerA = new SharedResourceDictionary { Source = first };
                    var ownerB = new SharedResourceDictionary { Source = first };
                    var ownerC = new SharedResourceDictionary { Source = second };

                    Assert.AreEqual(first, ownerA.Source);
                    Assert.AreEqual(1, ownerA.MergedDictionaries.Count);
                    Assert.AreSame(ownerA.MergedDictionaries[0], ownerB.MergedDictionaries[0]);
                    Assert.AreNotSame(ownerA.MergedDictionaries[0], ownerC.MergedDictionaries[0]);

                    // Lookups through the owner still resolve the shared content.
                    Assert.AreEqual("first", ownerB["FirstKey"]);
                    Assert.AreEqual("second", ownerC["SecondKey"]);
                    Assert.IsFalse(ownerA.Contains("SecondKey"));
                }
                finally
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch
                    {
                        // Temp cleanup only.
                    }
                }
            });
        }

        private static Uri WriteDictionary(string directory, string fileName, string key, string value)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(
                path,
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">" +
                $"<sys:String x:Key=\"{key}\">{value}</sys:String>" +
                "</ResourceDictionary>");
            return new Uri(path, UriKind.Absolute);
        }
    }
}
