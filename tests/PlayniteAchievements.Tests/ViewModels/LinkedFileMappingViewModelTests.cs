using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    [DoNotParallelize]
    public class LinkedFileMappingViewModelTests
    {
        private sealed class Harness
        {
            public GameCustomDataFile Record = new GameCustomDataFile
            {
                ManualLink = new ManualAchievementLink { SourceKey = "Steam", SourceGameId = "480" }
            };

            public int Writes;
            public LinkedFileTable Table = LinkedFileTable.Build(
                new[] { "id", "done" },
                new[] { new[] { "a", "true" }, new[] { "b", "false" }, new[] { "zzz", "true" } });

            public int Reads;
            public LinkedFileApplyReport AppliedReport;
            public bool ConfirmAnswer = true;
            public string NameAnswer = "My mapping";
            public string Message;
            public string Folder;

            public LinkedFileMappingServices Services(LinkedFileSchemaStore store = null)
            {
                return new LinkedFileMappingServices
                {
                    LoadRecord = () => Record,
                    UpdateRecord = change =>
                    {
                        change(Record);
                        Writes++;
                    },
                    ReadFile = (path, schema) =>
                    {
                        Reads++;
                        return LinkedFileReadResult.Ok(Table);
                    },
                    LoadAchievements = () => new List<AchievementDetail>
                    {
                        new AchievementDetail { ApiName = "a", DisplayName = "Alpha" },
                        new AchievementDetail { ApiName = "b", DisplayName = "Beta" }
                    },
                    SchemaStore = store,
                    ApplyNow = () => AppliedReport ?? new LinkedFileApplyReport { Status = LinkedFileReadStatus.Ok, MatchedRows = 2 },
                    BrowseFile = current => null,
                    Confirm = message => ConfirmAnswer,
                    ShowMessage = message => Message = message,
                    AskName = defaultName => NameAnswer,
                    RunRead = work => Task.FromResult(work()),
                    Localize = key => key
                };
            }
        }

        private static string CreateFile(string folder)
        {
            var path = Path.Combine(folder, "progress.csv");
            File.WriteAllText(path, "id,done\na,true\n");
            return path;
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
        public void NewLink_StoresNothingUntilThereIsAFileOrAMapping()
        {
            var harness = new Harness();
            var vm = new LinkedFileMappingViewModel(harness.Services());

            vm.TimesAreLocal = false;

            Assert.AreEqual(0, harness.Writes);
            Assert.IsNull(harness.Record.LinkedFile);
            Assert.IsFalse(vm.ChangedLink);
        }

        [TestMethod]
        public void ChoosingAFile_StoresTheLinkAndReadsThePreview()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                var path = CreateFile(folder);

                vm.FilePath = path;

                Assert.AreEqual(path, harness.Record.LinkedFile?.FilePath);
                Assert.AreEqual(LinkedFileFormat.Delimited, harness.Record.LinkedFile.Schema.Format);
                Assert.AreEqual(2, vm.Columns.Count);
                Assert.AreEqual(3, vm.PreviewRows.Count);
                Assert.IsTrue(vm.ChangedLink);
            });
        }

        [TestMethod]
        public void MappingColumns_KeepsOneColumnPerFieldAndRecomputesTheResult()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);

                var apiName = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);
                var unlocked = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);
                vm.Columns[0].SelectedField = apiName;
                vm.Columns[1].SelectedField = unlocked;

                Assert.IsTrue(vm.ProblemText == null, vm.ProblemText);
                StringAssert.Contains(vm.ResultText, "LOCPlayAch_LinkedFile_Count_Matched");
                Assert.AreEqual(1, vm.UnmatchedRows.Count, "The zzz row matches nothing.");
                Assert.AreEqual(0, vm.UnmatchedAchievements.Count);

                vm.Columns[1].SelectedField = apiName;
                Assert.AreEqual(LinkedFileField.Ignore, vm.Columns[0].Field, "Picking a field another column feeds moves it.");
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[1].Field);

                var stored = harness.Record.LinkedFile.Schema.Columns.Single();
                Assert.AreEqual("done", stored.Column);
                Assert.AreEqual(LinkedFileField.ApiName, stored.Field);
            });
        }

        [TestMethod]
        public void MappingSurvivesAReread_ByColumnName()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);
                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);

                harness.Table = LinkedFileTable.Build(new[] { "extra", "done", "id" }, new[] { new[] { "x", "1", "a" } });
                vm.Reread().Wait();

                Assert.AreEqual(3, vm.Columns.Count);
                Assert.AreEqual(LinkedFileField.Unlocked, vm.Columns.Single(column => column.ColumnName == "done").Field);
                Assert.AreEqual(LinkedFileField.Ignore, vm.Columns.Single(column => column.ColumnName == "extra").Field);
            });
        }

        [TestMethod]
        public void LoadingAStoredLink_RestoresItsSettings()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                harness.Record.LinkedFile = new LinkedFileLink
                {
                    FilePath = CreateFile(folder),
                    Schema = new LinkedFileSchema
                    {
                        Name = "Mine",
                        Format = LinkedFileFormat.Delimited,
                        Delimiter = "\t",
                        HasHeader = true,
                        RowPresenceMeansUnlocked = false,
                        MatchOrder = new List<LinkedFileMatchKey> { LinkedFileMatchKey.DisplayName },
                        TimeFormat = LinkedFileTimeFormat.WindowsFileTime,
                        Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "done", Field = LinkedFileField.Unlocked } }
                    }
                };

                var vm = new LinkedFileMappingViewModel(harness.Services());

                Assert.AreEqual(0, harness.Writes, "Opening the window writes nothing.");
                Assert.AreEqual("Tab", vm.DelimiterText);
                Assert.IsFalse(vm.MatchByApiName);
                Assert.IsTrue(vm.MatchByName);
                Assert.AreEqual(LinkedFileTimeFormat.WindowsFileTime, vm.SelectedTimeFormat.Value);
                Assert.AreEqual(LinkedFileField.Unlocked, vm.Columns.Single(column => column.ColumnName == "done").Field);
                Assert.IsTrue(vm.HasStoredLink);
            });
        }

        [TestMethod]
        public void RowPresence_IsOnlyOfferedWithoutAnUnlockColumn()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);
                vm.RowPresenceMeansUnlocked = true;
                Assert.IsTrue(vm.CanUseRowPresence);
                Assert.IsTrue(vm.BuildSchema().RowPresenceMeansUnlocked);

                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);

                Assert.IsFalse(vm.CanUseRowPresence);
                Assert.IsFalse(vm.BuildSchema().RowPresenceMeansUnlocked);
            });
        }

        [TestMethod]
        public void SavedMappings_SaveApplyRenameAndDelete()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var store = new LinkedFileSchemaStore(folder);
                var vm = new LinkedFileMappingViewModel(harness.Services(store));
                vm.FilePath = CreateFile(folder);
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);

                vm.SaveAsCommand.Execute(null);
                Assert.IsTrue(store.TryGet("My mapping", out var saved));
                Assert.AreEqual("id", saved.Columns.Single().Column);
                Assert.AreEqual("My mapping", vm.SelectedSavedName);
                Assert.AreEqual("My mapping", harness.Record.LinkedFile.Schema.Name);

                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Ignore);
                vm.ApplySavedCommand.Execute(null);
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[0].Field, "Applying a saved mapping fills the columns.");

                harness.NameAnswer = "Renamed";
                vm.RenameSavedCommand.Execute(null);
                Assert.IsTrue(store.Contains("Renamed"));
                Assert.IsFalse(store.Contains("My mapping"));
                Assert.AreEqual("Renamed", harness.Record.LinkedFile.Schema.Name);

                vm.DeleteSavedCommand.Execute(null);
                Assert.AreEqual(0, store.GetAll().Count);
                Assert.IsNotNull(harness.Record.LinkedFile, "Deleting a saved mapping never touches the game's own copy.");
            });
        }

        [TestMethod]
        public void ApplyNowAndUnlink()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                Assert.IsFalse(vm.ApplyNowCommand.CanExecute(null), "Nothing to apply without a file and a mapping.");

                vm.FilePath = CreateFile(folder);
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);
                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);
                Assert.IsTrue(vm.ApplyNowCommand.CanExecute(null));

                vm.ApplyNowCommand.Execute(null);
                StringAssert.Contains(harness.Message, "LOCPlayAch_LinkedFile_ApplyReportTitle");

                var closed = false;
                vm.RequestClose += (s, e) => closed = true;
                harness.ConfirmAnswer = false;
                vm.UnlinkCommand.Execute(null);
                Assert.IsNotNull(harness.Record.LinkedFile, "A declined confirmation keeps the link.");

                harness.ConfirmAnswer = true;
                vm.UnlinkCommand.Execute(null);
                Assert.IsNull(harness.Record.LinkedFile);
                Assert.IsTrue(closed);
            });
        }

        [TestMethod]
        public void MissingFile_ShowsWhyAndBlocksApply()
        {
            var harness = new Harness();
            var vm = new LinkedFileMappingViewModel(harness.Services());

            vm.FilePath = @"C:\nowhere\progress.csv";

            Assert.IsTrue(vm.IsFileMissing);
            Assert.IsFalse(vm.ApplyNowCommand.CanExecute(null));
            Assert.AreEqual(@"C:\nowhere\progress.csv", harness.Record.LinkedFile.FilePath, "The path is kept so the file can appear later.");
        }

        [TestMethod]
        public void StatusText_SummarizesALink()
        {
            Func<string, string> key = name => name;
            Assert.IsNull(LinkedFileStatusText.Summarize(null, key));
            Assert.AreEqual("LOCPlayAch_LinkedFile_MappingOnly", LinkedFileStatusText.Summarize(new LinkedFileLink(), key));
            StringAssert.EndsWith(
                LinkedFileStatusText.Summarize(new LinkedFileLink { FilePath = @"C:\nowhere\p.csv" }, key),
                "LOCPlayAch_LinkedFile_FileMissing");

            WithFolder(folder =>
            {
                var path = CreateFile(folder);
                StringAssert.EndsWith(LinkedFileStatusText.Summarize(new LinkedFileLink { FilePath = path }, key), "LOCPlayAch_LinkedFile_NotReadYet");
                StringAssert.StartsWith(
                    LinkedFileStatusText.Summarize(new LinkedFileLink { FilePath = path, LastReadUtc = DateTime.UtcNow, LastReadStatus = "Ok" }, key),
                    "progress.csv");
                StringAssert.EndsWith(
                    LinkedFileStatusText.Summarize(new LinkedFileLink { FilePath = path, LastReadStatus = "FileUnstable" }, key),
                    "LOCPlayAch_LinkedFile_Status_Unstable");
            });
        }
    }
}
