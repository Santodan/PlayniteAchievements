using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileSchemaStoreTests
    {
        private static LinkedFileSchema Mapping(string name, string column = "id")
        {
            return new LinkedFileSchema
            {
                Name = name,
                Format = LinkedFileFormat.Ini,
                Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = column, Field = LinkedFileField.ApiName } }
            };
        }

        private static void WithFolder(Action<string> test)
        {
            var folder = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                test(folder);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void SaveRoundTripsThroughTheFileAndUpsertsByName()
        {
            WithFolder(folder =>
            {
                var store = new LinkedFileSchemaStore(folder);
                store.Save(Mapping(" Progress "));
                store.Save(Mapping("Alpha"));
                store.Save(Mapping("PROGRESS", column: "$section"));

                var reloaded = new LinkedFileSchemaStore(folder);
                CollectionAssert.AreEqual(new[] { "Alpha", "PROGRESS" }, reloaded.GetAll().Select(schema => schema.Name).ToArray());
                Assert.IsTrue(reloaded.TryGet("progress", out var progress));
                Assert.AreEqual("$section", progress.Columns.Single().Column);
                Assert.AreEqual(LinkedFileFormat.Ini, progress.Format);
                StringAssert.Contains(File.ReadAllText(reloaded.FilePath), "\"Ini\"");
            });
        }

        [TestMethod]
        public void ReturnedCopiesAreIndependent()
        {
            WithFolder(folder =>
            {
                var store = new LinkedFileSchemaStore(folder);
                var saved = store.Save(Mapping("A"));
                saved.Columns[0].Column = "changed";
                store.TryGet("A", out var fetched);
                fetched.Columns[0].Column = "changed again";

                store.TryGet("A", out var again);
                Assert.AreEqual("id", again.Columns[0].Column);
            });
        }

        [TestMethod]
        public void DeleteRenameAndEvents()
        {
            WithFolder(folder =>
            {
                var store = new LinkedFileSchemaStore(folder);
                var events = new List<string>();
                store.Changed += (sender, args) => events.Add((args.Deleted ? "-" : "+") + args.Name);

                store.Save(Mapping("A"));
                store.Save(Mapping("B"));
                Assert.IsFalse(store.Rename("A", "b"), "The target name is taken.");
                Assert.IsFalse(store.Rename("missing", "C"));
                Assert.IsFalse(store.Rename("A", " "));
                Assert.IsTrue(store.Rename("A", "C"));
                Assert.IsTrue(store.Rename("C", "c"), "A rename that only changes case is allowed.");
                Assert.IsTrue(store.Delete("B"));
                Assert.IsFalse(store.Delete("B"));

                CollectionAssert.AreEqual(new[] { "c" }, new LinkedFileSchemaStore(folder).GetAll().Select(schema => schema.Name).ToArray());
                CollectionAssert.AreEqual(new[] { "+A", "+B", "-A", "+C", "-C", "+c", "-B" }, events.ToArray());
            });
        }

        [TestMethod]
        public void BlankNamesAreRejectedAndACorruptFileLoadsEmpty()
        {
            WithFolder(folder =>
            {
                var store = new LinkedFileSchemaStore(folder);
                Assert.ThrowsException<ArgumentException>(() => store.Save(Mapping("  ")));
                Assert.ThrowsException<ArgumentException>(() => store.Save(null));

                File.WriteAllText(Path.Combine(folder, LinkedFileSchemaStore.FileName), "{ not json");
                Assert.AreEqual(0, new LinkedFileSchemaStore(folder).GetAll().Count);
            });
        }
    }
}
