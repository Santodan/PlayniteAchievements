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
            public bool ApplyReportsNothing;
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
                        return File.Exists(path)
                            ? LinkedFileReadResult.Ok(Table)
                            : LinkedFileReadResult.Failed(LinkedFileReadStatus.FileMissing, "missing");
                    },
                    LoadAchievements = () => new List<AchievementDetail>
                    {
                        new AchievementDetail { ApiName = "a", DisplayName = "Alpha" },
                        new AchievementDetail { ApiName = "b", DisplayName = "Beta" }
                    },
                    SchemaStore = store,
                    ApplyNow = () => ApplyReportsNothing ? null : AppliedReport ?? new LinkedFileApplyReport { Status = LinkedFileReadStatus.Ok, MatchedRows = 2 },
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
        public void ChoosingAFile_ReadsThePreviewButStoresNothingBeforeApply()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                var path = CreateFile(folder);

                vm.FilePath = path;
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);

                Assert.AreEqual(LinkedFileFormat.Delimited, vm.BuildSchema().Format);
                Assert.AreEqual(3, vm.Columns.Count, "The file's two columns and the position column.");
                Assert.AreEqual(3, vm.PreviewRows.Count);
                Assert.AreEqual(0, harness.Writes, "Closing without Apply leaves the game as it was.");
                Assert.IsNull(harness.Record.LinkedFile);
                Assert.IsFalse(vm.ChangedLink);
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
                Assert.AreEqual("Alpha", vm.PreviewRows[0].Match);
                Assert.AreEqual("LOCPlayAch_LinkedFile_NoMatch", vm.PreviewRows[2].Match, "The zzz row matches nothing.");
                Assert.IsFalse(vm.PreviewRows[2].IsMatched);
                Assert.IsTrue(vm.AchievementRows.All(row => row.IsMatched), "Every achievement has a row.");

                vm.Columns[1].SelectedField = apiName;
                Assert.AreEqual(LinkedFileField.Ignore, vm.Columns[0].Field, "Picking a field another column feeds moves it.");
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[1].Field);

                var stored = vm.BuildSchema().Columns.Single();
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

                Assert.AreEqual(4, vm.Columns.Count);
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
                        TimeFormat = LinkedFileTimeFormat.WindowsFileTime,
                        Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "done", Field = LinkedFileField.Unlocked } }
                    }
                };

                var vm = new LinkedFileMappingViewModel(harness.Services());

                Assert.AreEqual(0, harness.Writes, "Opening the window writes nothing.");
                Assert.AreEqual("Tab", vm.DelimiterText);
                Assert.IsTrue(vm.UsesUnlockedValues);
                Assert.IsFalse(vm.UsesTimeOptions);
                Assert.AreEqual(LinkedFileTimeFormat.WindowsFileTime, vm.SelectedTimeFormat.Value);
                Assert.AreEqual(LinkedFileField.Unlocked, vm.Columns.Single(column => column.ColumnName == "done").Field);
                Assert.IsTrue(vm.HasStoredLink);
            });
        }

        [TestMethod]
        public void Settings_ShowOnlyForTheColumnsTheyRead()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[0].Field, "A new file starts from a guess.");
                Assert.AreEqual(LinkedFileField.Unlocked, vm.Columns[1].Field);
                Assert.IsTrue(vm.UsesUnlockedValues);

                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Ignore);
                Assert.IsFalse(vm.UsesTimeOptions);
                Assert.IsFalse(vm.UsesUnlockedValues);
                StringAssert.Contains(vm.ResultText, "LOCPlayAch_LinkedFile_Count_Unlocked", "Listed rows read as unlocked without a setting.");
                Assert.IsTrue(vm.ApplyNowCommand.CanExecute(null));

                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);
                Assert.IsTrue(vm.UsesUnlockedValues);

                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.UnlockTime);
                Assert.IsTrue(vm.UsesTimeOptions);
                Assert.IsFalse(vm.UsesUnlockedValues);
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
                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Ignore);

                vm.SaveAsCommand.Execute(null);
                Assert.IsTrue(store.TryGet("My mapping", out var saved));
                Assert.AreEqual("id", saved.Columns.Single().Column);
                Assert.AreEqual("My mapping", vm.SelectedSavedName);
                Assert.AreEqual("My mapping", vm.BuildSchema().Name);
                Assert.IsNull(harness.Record.LinkedFile, "Saving a mapping to the list does not link the game.");

                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Ignore);
                vm.SelectedSavedName = null;
                vm.SelectedSavedName = "My mapping";
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[0].Field, "Picking a preset fills the columns.");

                harness.NameAnswer = "Renamed";
                vm.RenameSavedCommand.Execute(null);
                Assert.IsTrue(store.Contains("Renamed"));
                Assert.IsFalse(store.Contains("My mapping"));
                Assert.AreEqual("Renamed", vm.BuildSchema().Name);

                vm.DeleteSavedCommand.Execute(null);
                Assert.AreEqual(0, store.GetAll().Count);
                Assert.AreEqual(LinkedFileField.ApiName, vm.Columns[0].Field, "Deleting a saved mapping leaves the window's own mapping.");
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

                var closed = false;
                vm.RequestClose += (s, e) => closed = true;
                vm.ApplyNowCommand.Execute(null);
                StringAssert.Contains(harness.Message, "LOCPlayAch_LinkedFile_ApplyReportTitle");
                Assert.IsTrue(closed, "Apply closes the window.");
                Assert.AreEqual("done", harness.Record.LinkedFile.Schema.Columns.Single(column => column.Field == LinkedFileField.Unlocked).Column);
                Assert.IsTrue(vm.ChangedLink);

                closed = false;
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
        public void ApplyNow_WithoutAReport_StillCloses()
        {
            WithFolder(folder =>
            {
                var harness = new Harness { ApplyReportsNothing = true };
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);
                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);

                var closed = false;
                vm.RequestClose += (s, e) => closed = true;
                vm.ApplyNowCommand.Execute(null);

                Assert.IsTrue(closed, "Apply closes the window when the refresh runs after it.");
                Assert.IsNull(harness.Message, "No report means no message.");
                Assert.IsNotNull(harness.Record.LinkedFile);
            });
        }

        [TestMethod]
        public void MissingFile_ShowsWhyAndBlocksApply()
        {
            var harness = new Harness();
            var vm = new LinkedFileMappingViewModel(harness.Services());

            vm.FilePath = @"C:\nowhere\progress.csv";

            Assert.IsTrue(vm.IsFileMissing);
            Assert.IsFalse(vm.ApplyNowCommand.CanExecute(null), "Without a complete mapping there is nothing to apply.");
            Assert.IsNull(harness.Record.LinkedFile);
        }

        [TestMethod]
        public void MissingFile_CanBeLinkedWithACompleteSavedMapping()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var store = new LinkedFileSchemaStore(folder);
                store.Save(new LinkedFileSchema
                {
                    Name = "Section per achievement",
                    Format = LinkedFileFormat.Ini,
                    Columns = new List<LinkedFileColumnMapping>
                    {
                        new LinkedFileColumnMapping { Column = "$section", Field = LinkedFileField.ApiName },
                        new LinkedFileColumnMapping { Column = "Achieved", Field = LinkedFileField.Unlocked }
                    }
                });
                harness.AppliedReport = new LinkedFileApplyReport { Status = LinkedFileReadStatus.FileMissing };
                var vm = new LinkedFileMappingViewModel(harness.Services(store));

                vm.FilePath = Path.Combine(folder, "not-yet", "achievements.ini");
                vm.SelectedSavedName = "Section per achievement";

                Assert.IsTrue(vm.ApplyNowCommand.CanExecute(null), "A file written only at the first unlock can be linked before it exists.");
                vm.ApplyNowCommand.Execute(null);
                Assert.AreEqual(Path.Combine(folder, "not-yet", "achievements.ini"), harness.Record.LinkedFile.FilePath);
                Assert.AreEqual(LinkedFileFormat.Ini, harness.Record.LinkedFile.Schema.Format);
            });
        }

        [TestMethod]
        public void PairingRowsByHand_IsStagedUntilApplyAndStoredWithTheLink()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);

                var rowA = vm.PreviewRows[0];
                var rowZ = vm.PreviewRows[2];
                Assert.IsTrue(rowZ.CanPick);
                Assert.AreEqual("LOCPlayAch_LinkedFile_NoMatch", rowZ.Match);
                Assert.AreEqual("id:zzz", rowZ.RowKey);

                rowZ.IsPickerOpen = true;
                rowZ.Search = "bet";
                var beta = rowZ.Options.Single(option => !option.IsSpecial);
                Assert.AreEqual("a", rowA.RowKey.Substring(3));
                StringAssert.Contains(beta.PairedWith, "b", "Beta is matched to the file's own b row.");
                rowZ.Pick(beta);

                Assert.IsFalse(rowZ.IsPickerOpen);
                Assert.AreSame(rowZ, vm.PreviewRows[2], "A pick updates the rows in place.");
                Assert.AreEqual("Beta", rowZ.Match);
                Assert.IsTrue(rowZ.IsUserSet);
                Assert.AreEqual("LOCPlayAch_LinkedFile_NoMatch", vm.PreviewRows[1].Match, "The b row lost Beta to the hand pairing.");

                rowA.IsPickerOpen = true;
                rowA.Pick(rowA.Options.First(option => option.IsSpecial && option.Key == string.Empty));
                Assert.AreEqual("LOCPlayAch_Common_None", rowA.Match);
                Assert.AreEqual(0, harness.Writes, "Pairings are staged until Apply.");

                vm.ApplyNowCommand.Execute(null);
                var stored = harness.Record.LinkedFile.Pairings;
                Assert.AreEqual(2, stored.Count);
                Assert.AreEqual("b", stored["id:zzz"]);
                Assert.AreEqual(string.Empty, stored["id:a"]);

                var reopened = new LinkedFileMappingViewModel(harness.Services());
                Assert.AreEqual("Beta", reopened.PreviewRows[2].Match);
                var back = reopened.PreviewRows[0];
                back.IsPickerOpen = true;
                Assert.IsNull(back.Options[0].Key, "A hand-paired row offers Automatic first.");
                back.Pick(back.Options[0]);
                Assert.AreEqual("Alpha", back.Match);
            });
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

        [TestMethod]
        public void SpoilerMask_FollowsHiddenAndLockedSettings()
        {
            Func<string, string> key = name => name;
            var hidden = new AchievementDetail { ApiName = "h", DisplayName = "Secret", Description = "Spoiler", Hidden = true, UnlockedIconPath = @"C:\i\h.png" };
            var locked = new AchievementDetail { ApiName = "l", DisplayName = "Plain", Description = "Text", UnlockedIconPath = @"C:\i\l.png" };
            var settings = new SpoilerSettings();

            var maskedHidden = AchievementSpoilerMask.Apply(hidden, settings, key);
            Assert.AreEqual("LOCPlayAch_Achievements_HiddenTitle", maskedHidden.Name);
            Assert.IsNull(maskedHidden.Description);
            Assert.IsTrue(maskedHidden.IsTitleMasked);
            Assert.AreNotEqual(@"C:\i\h.png", maskedHidden.Icon);

            Assert.AreEqual("Plain", AchievementSpoilerMask.Apply(locked, settings, key).Name, "Locked titles show by default.");
            settings.ShowLockedTitle = false;
            Assert.AreEqual("LOCPlayAch_Common_Locked", AchievementSpoilerMask.Apply(locked, settings, key).Name);

            hidden.Unlocked = true;
            Assert.AreEqual("Secret", AchievementSpoilerMask.Apply(hidden, settings, key).Name, "Unlocked achievements are never masked.");
            Assert.AreEqual("Secret", AchievementSpoilerMask.Apply(hidden, SpoilerSettings.ShowAll, key).Name);
        }

        [TestMethod]
        public void RevealToggle_ShowsMaskedAchievementNames()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var services = harness.Services();
                services.LoadAchievements = () => new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "a", DisplayName = "Alpha", Hidden = true },
                    new AchievementDetail { ApiName = "b", DisplayName = "Beta" }
                };
                services.Spoilers = new SpoilerSettings();
                var vm = new LinkedFileMappingViewModel(services);
                vm.FilePath = CreateFile(folder);
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.ApiName);

                Assert.IsTrue(vm.HasMaskedAchievements);
                Assert.AreEqual("LOCPlayAch_Achievements_HiddenTitle", vm.PreviewRows[0].Match);

                vm.ToggleRevealCommand.Execute(null);
                Assert.IsTrue(vm.IsRevealed);
                Assert.AreEqual("Alpha", vm.PreviewRows[0].Match);
                Assert.IsTrue(vm.HasMaskedAchievements, "The toggle stays available to mask again.");

                vm.ToggleRevealCommand.Execute(null);
                Assert.AreEqual("LOCPlayAch_Achievements_HiddenTitle", vm.PreviewRows[0].Match);
            });
        }

        [TestMethod]
        public void NewLink_TakesTheBestMatchAndListsTheAchievementsWithTheirRows()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);

                Assert.AreEqual("id", vm.SelectedMatch.Column, "The id column pairs both achievements by API name.");
                Assert.AreEqual(LinkedFileMatchBy.ApiName, vm.SelectedMatch.By);
                Assert.AreEqual("id", vm.BuildSchema().MatchColumn);
                Assert.IsNull(vm.ProblemText, vm.ProblemText);

                var alpha = vm.AchievementRows.Single(row => row.ApiName == "a");
                Assert.IsTrue(alpha.IsMatched);
                StringAssert.Contains(alpha.RowText, "#1");
                Assert.AreEqual(LinkedFileField.Unlocked, vm.ValueColumns[0].Field, "The status column comes first.");
                Assert.AreEqual("LOCPlayAch_Common_Unlocked", alpha[0]);
                Assert.AreEqual("LOCPlayAch_Common_Locked", vm.AchievementRows.Single(row => row.ApiName == "b")[0]);
            });
        }

        [TestMethod]
        public void GameWithoutAchievements_HidesTheMatchAndFollowsTheNamingColumn()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                harness.Table = LinkedFileTable.Build(new[] { "title", "done" }, new[] { new[] { "First Steps", "1" } });
                var services = harness.Services();
                services.LoadAchievements = () => new List<AchievementDetail>();
                var vm = new LinkedFileMappingViewModel(services);
                vm.FilePath = CreateFile(folder);

                Assert.IsFalse(vm.ShowsMatching, "Every row is new, so there is nothing to match.");
                Assert.IsTrue(vm.CreateUnmatched);
                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.DisplayName);
                vm.Columns[1].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Unlocked);

                var schema = vm.BuildSchema();
                Assert.AreEqual("title", schema.MatchColumn, "Later reads find the new achievements by their names.");
                Assert.AreEqual(LinkedFileMatchBy.Name, schema.MatchBy);
                Assert.IsNull(vm.ProblemText, vm.ProblemText);

                vm.Columns[0].SelectedField = vm.FieldOptions.Single(option => option.Value == LinkedFileField.Ignore);
                Assert.AreEqual("LOCPlayAch_LinkedFile_Problem_CreateNeedsName", vm.ProblemText, "Only the naming column is asked for.");
            });
        }

        [TestMethod]
        public void PairingByHand_SwitchesToTheWayThatPairsTheRowOnItsOwn()
        {
            WithFolder(folder =>
            {
                var harness = new Harness();
                // Positions from 0 in "n"; the names in "name" match nothing.
                harness.Table = LinkedFileTable.Build(
                    new[] { "n", "name" },
                    new[] { new[] { "1", "x" }, new[] { "0", "y" } });
                var vm = new LinkedFileMappingViewModel(harness.Services());
                vm.FilePath = CreateFile(folder);
                Assert.AreEqual(LinkedFileMatchBy.PositionFromZero, vm.SelectedMatch.By, "A 0 rules out counting from 1.");

                // Choose counting from 1, which pairs only the 1 row, and with the wrong achievement.
                vm.SelectedMatch = vm.MatchChoices.Single(choice => choice.Column == "n" && choice.By == LinkedFileMatchBy.PositionFromOne);
                var beta = vm.AchievementRows.Single(row => row.ApiName == "b");
                Assert.IsFalse(beta.IsMatched);

                // Pairing Beta with the row holding 1 is what counting from 0 does on its own.
                beta.IsPickerOpen = true;
                beta.Pick(beta.Options.Single(option => option.Key == "order:1"));

                Assert.AreEqual(LinkedFileMatchBy.PositionFromZero, vm.SelectedMatch.By);
                Assert.IsTrue(vm.AchievementRows.All(row => row.IsMatched));
                Assert.IsFalse(vm.AchievementRows.Any(row => row.IsUserSet), "No hand pairing is left behind.");
            });
        }

        [TestMethod]
        public void StatusText_DescribesStateAndFlagsProblems()
        {
            Func<string, string> key = name => name;
            Assert.IsNull(LinkedFileStatusText.DescribeState(null, key));
            Assert.IsFalse(LinkedFileStatusText.HasProblem(null));
            Assert.IsTrue(LinkedFileStatusText.HasProblem(new LinkedFileLink()));

            var missing = new LinkedFileLink { FilePath = @"C:\nowhere\p.csv" };
            Assert.AreEqual("LOCPlayAch_LinkedFile_FileMissing", LinkedFileStatusText.DescribeState(missing, key));
            Assert.IsTrue(LinkedFileStatusText.HasProblem(missing));

            WithFolder(folder =>
            {
                var path = CreateFile(folder);
                var notRead = new LinkedFileLink { FilePath = path };
                Assert.AreEqual("LOCPlayAch_LinkedFile_NotReadYet", LinkedFileStatusText.DescribeState(notRead, key));
                Assert.IsFalse(LinkedFileStatusText.HasProblem(notRead));

                var ok = new LinkedFileLink { FilePath = path, LastReadUtc = DateTime.UtcNow, LastReadStatus = "Ok" };
                Assert.AreEqual("LOCPlayAch_LinkedFile_ReadFormat", LinkedFileStatusText.DescribeState(ok, key));
                Assert.IsFalse(LinkedFileStatusText.HasProblem(ok));

                var failed = new LinkedFileLink { FilePath = path, LastReadStatus = "ReadFailed" };
                Assert.AreEqual("LOCPlayAch_LinkedFile_Status_ReadFailed", LinkedFileStatusText.DescribeState(failed, key));
                Assert.IsTrue(LinkedFileStatusText.HasProblem(failed));
            });
        }
    }
}
