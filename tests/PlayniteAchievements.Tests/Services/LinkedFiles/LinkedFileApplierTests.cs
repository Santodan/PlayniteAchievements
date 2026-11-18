using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileApplierTests
    {
        private static readonly DateTime Now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        private static readonly DateTime Earlier = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private static LinkedFileTable Table(string[] columns, params string[][] rows)
        {
            return LinkedFileTable.Build(columns, rows);
        }

        /// <summary>A mapping as a stored one reads: <see cref="LinkedFileSchema.Normalize"/> gives it its match column.</summary>
        private static LinkedFileSchema Schema(params (string column, LinkedFileField field)[] mappings)
        {
            return LinkedFileSchema.Normalize(new LinkedFileSchema
            {
                Columns = mappings.Select(mapping => new LinkedFileColumnMapping { Column = mapping.column, Field = mapping.field }).ToList()
            });
        }

        private static ManualAchievementLink Link()
        {
            return new ManualAchievementLink
            {
                SourceKey = "Steam",
                SourceGameId = "480",
                UnlockStates = new Dictionary<string, bool> { ["ach_one"] = true },
                UnlockTimes = new Dictionary<string, DateTime?> { ["ach_one"] = Earlier }
            };
        }

        private static List<CustomAchievementDefinition> Definitions()
        {
            return new List<CustomAchievementDefinition>
            {
                new CustomAchievementDefinition { Id = "c1", DisplayName = "Custom One" },
                new CustomAchievementDefinition { Id = "c2", DisplayName = "Custom Two", Unlocked = true, UnlockTimeUtc = Earlier }
            };
        }

        private static List<AchievementDetail> Achievements()
        {
            return new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "ach_one", DisplayName = "One", Unlocked = true, UnlockTimeUtc = Earlier },
                new AchievementDetail { ApiName = "ach_two", DisplayName = "Two" },
                new AchievementDetail { ApiName = "custom:c1", DisplayName = "Custom One", IsCustom = true },
                new AchievementDetail { ApiName = "custom:c2", DisplayName = "Custom Two", IsCustom = true, Unlocked = true, UnlockTimeUtc = Earlier }
            };
        }

        [TestMethod]
        public void TrophyRarityAndCategorySetCustomDefinitions()
        {
            var table = Table(
                new[] { "id", "trophy", "rarity", "category" },
                new[] { "c1", "Gold", "12.5%", "Story > Act 1" },
                new[] { "c2", "bronze", "Ultra Rare", "" },
                new[] { "c3", "silver", "Common", "Side" });
            var schema = Schema(
                ("id", LinkedFileField.ApiName), ("trophy", LinkedFileField.TrophyType),
                ("rarity", LinkedFileField.Rarity), ("category", LinkedFileField.Category));
            schema.CreateUnmatchedRows = true;

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(0, plan.Report.ParseErrors);
            var c1 = plan.CustomDefinitions.Single(definition => definition.Id == "c1");
            Assert.AreEqual("gold", c1.TrophyType);
            Assert.AreEqual(12.5, c1.GlobalPercentUnlocked);
            Assert.AreEqual(PercentRarityHelper.GetRarityTier(12.5).ToString(), c1.Rarity);
            Assert.AreEqual(CustomAchievementTextImportService.ParseCategoryPath("Story > Act 1"), c1.Category);
            var c2 = plan.CustomDefinitions.Single(definition => definition.Id == "c2");
            Assert.AreEqual("bronze", c2.TrophyType);
            Assert.AreEqual("UltraRare", c2.Rarity);
            Assert.IsNull(c2.GlobalPercentUnlocked);
            Assert.IsNull(c2.Category, "A blank category cell leaves the category unchanged.");
            var created = plan.CustomDefinitions.Single(definition => definition.Id == "c3");
            Assert.AreEqual("silver", created.TrophyType);
            Assert.AreEqual("Common", created.Rarity);
            Assert.AreEqual("Side", created.Category);
        }

        [TestMethod]
        public void BadTrophyOrRarityIsAParseError()
        {
            var table = Table(new[] { "id", "trophy", "rarity" }, new[] { "c1", "diamond", "" }, new[] { "c2", "", "150%" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("trophy", LinkedFileField.TrophyType), ("rarity", LinkedFileField.Rarity));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(2, plan.Report.ParseErrors);
            Assert.IsFalse(plan.CustomChanged);
        }

        [TestMethod]
        public void IconAlreadyCopiedFromTheSameSource_IsUnchanged()
        {
            var definitions = Definitions();
            definitions[0].UnlockedIconPath = @"C:\managed\c1.png";
            var table = Table(new[] { "id", "icon" }, new[] { "c1", "https://example.com/c1.png" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("icon", LinkedFileField.UnlockedIconPath));

            var same = LinkedFileApplier.Build(table, schema, null, Achievements(), null, definitions, Now, (stored, source) => true);
            var different = LinkedFileApplier.Build(table, schema, null, Achievements(), null, definitions, Now);

            Assert.IsFalse(same.CustomChanged);
            Assert.AreEqual(@"C:\managed\c1.png", same.CustomDefinitions[0].UnlockedIconPath);
            Assert.IsTrue(different.CustomChanged);
            Assert.AreEqual("https://example.com/c1.png", different.CustomDefinitions[0].UnlockedIconPath);
        }

        [TestMethod]
        public void FileWinsForTheRowsItContains_IncludingRelocks()
        {
            var table = Table(
                new[] { "id", "done", "when" },
                new[] { "ach_one", "false", "" },
                new[] { "ach_two", "true", "1700000000" },
                new[] { "c1", "yes", "" },
                new[] { "c2", "no", "" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked), ("when", LinkedFileField.UnlockTime));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(LinkedFileReadStatus.Ok, plan.Report.Status);
            Assert.AreEqual(4, plan.Report.MatchedRows);
            Assert.AreEqual(2, plan.Report.Unlocked);
            Assert.AreEqual(2, plan.Report.Relocked);
            Assert.IsTrue(plan.ManualChanged);
            Assert.IsTrue(plan.CustomChanged);
            Assert.IsFalse(plan.ManualUnlocks.ContainsKey("ach_one"), "A locked row removes the unlock.");
            Assert.AreEqual(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), plan.ManualUnlocks["ach_two"]);
            var c1 = plan.CustomDefinitions.Single(definition => definition.Id == "c1");
            Assert.IsTrue(c1.Unlocked);
            Assert.AreEqual(Now, c1.UnlockTimeUtc, "An unlock without a time stamps now.");
            var c2 = plan.CustomDefinitions.Single(definition => definition.Id == "c2");
            Assert.IsFalse(c2.Unlocked);
            Assert.IsNull(c2.UnlockTimeUtc);
        }

        [TestMethod]
        public void UnmatchedRowsKeepTheirStateAndLegacyKeysMigrate()
        {
            var link = new ManualAchievementLink
            {
                SourceKey = "Exophase",
                SourceGameId = "slug",
                UnlockStates = new Dictionary<string, bool> { ["Three"] = true },
                UnlockTimes = new Dictionary<string, DateTime?> { ["Three"] = Earlier }
            };
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "exophase:3", DisplayName = "Three" },
                new AchievementDetail { ApiName = "exophase:4", DisplayName = "Four" }
            };
            var table = Table(new[] { "id", "done" }, new[] { "exophase:4", "true" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked));

            var plan = LinkedFileApplier.Build(table, schema, null, achievements, link, null, Now);

            Assert.IsTrue(plan.ManualChanged);
            Assert.AreEqual(Earlier, plan.ManualUnlocks["exophase:3"], "The unmatched row carries forward under its current API name.");
            Assert.AreEqual(Now, plan.ManualUnlocks["exophase:4"]);
            Assert.AreEqual(1, plan.Report.UnmatchedAchievements);
        }

        [TestMethod]
        public void NothingChanges_WhenTheFileAgreesWithTheGame()
        {
            var table = Table(new[] { "id", "done", "when" }, new[] { "ach_one", "true", "" }, new[] { "c2", "true", "" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked), ("when", LinkedFileField.UnlockTime));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.IsFalse(plan.ManualChanged);
            Assert.IsFalse(plan.CustomChanged);
            Assert.AreEqual(Earlier, plan.ManualUnlocks["ach_one"], "An unlock without a time keeps the time it already had.");
            Assert.AreEqual(Earlier, plan.CustomDefinitions.Single(definition => definition.Id == "c2").UnlockTimeUtc);
            Assert.IsFalse(plan.Report.ChangedAnything);
        }

        [TestMethod]
        public void TimeOnlyMappingAndPresenceRule()
        {
            var timeOnly = Table(new[] { "id", "when" }, new[] { "ach_one", "" }, new[] { "ach_two", "1700000000" }, new[] { "c1", "0" });
            var plan = LinkedFileApplier.Build(
                timeOnly, Schema(("id", LinkedFileField.ApiName), ("when", LinkedFileField.UnlockTime)), null, Achievements(), Link(), Definitions(), Now);
            Assert.AreEqual(1, plan.Report.Relocked, "An empty time cell means locked when only the time is mapped.");
            Assert.AreEqual(1, plan.Report.Unlocked);
            Assert.IsFalse(plan.CustomDefinitions.Single(definition => definition.Id == "c1").Unlocked, "A zero stamp is no time, so the row reads as locked.");

            var presence = Table(new[] { "id" }, new[] { "ach_two" });
            var presenceSchema = Schema(("id", LinkedFileField.ApiName));
            var presencePlan = LinkedFileApplier.Build(presence, presenceSchema, null, Achievements(), Link(), Definitions(), Now);
            Assert.AreEqual(Now, presencePlan.ManualUnlocks["ach_two"]);
            Assert.AreEqual(Earlier, presencePlan.ManualUnlocks["ach_one"], "Achievements with no row keep their state.");
            Assert.AreEqual(1, presencePlan.Report.Unlocked);
            Assert.AreEqual(0, presencePlan.Report.Relocked);

            var progressOnly = LinkedFileApplier.Build(
                Table(new[] { "id", "num" }, new[] { "ach_two", "3" }),
                Schema(("id", LinkedFileField.ApiName), ("num", LinkedFileField.ProgressNum)),
                null, Achievements(), Link(), Definitions(), Now);
            Assert.IsFalse(progressOnly.ManualChanged, "A progress column says the row is not simply unlocked.");
        }

        [TestMethod]
        public void RowsWithErrorsAreSkippedAndReported()
        {
            var table = Table(
                new[] { "id", "done", "when", "num", "den" },
                new[] { "ach_two", "true", "2999-01-01T00:00:00Z", "", "" },
                new[] { "c1", "perhaps", "", "", "" },
                new[] { "c2", "true", "", "5", "3" });
            var schema = Schema(
                ("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked), ("when", LinkedFileField.UnlockTime),
                ("num", LinkedFileField.ProgressNum), ("den", LinkedFileField.ProgressDenom));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(3, plan.Report.ParseErrors);
            Assert.IsFalse(plan.ManualChanged);
            Assert.IsFalse(plan.CustomChanged);
            Assert.IsTrue(plan.Report.Messages.Any(message => message.Contains("in the future")));
            Assert.IsTrue(plan.Report.Messages.Any(message => message.Contains("not true or false")));
            Assert.IsTrue(plan.Report.Messages.Any(message => message.Contains("greater than the progress total")));
        }

        [TestMethod]
        public void DefinitionFieldsApplyToCustomRowsOnly()
        {
            var table = Table(
                new[] { "id", "name", "points", "hidden", "desc" },
                new[] { "c1", "Renamed", "5", "yes", "New text" },
                new[] { "ach_two", "Ignored", "9", "", "" });
            var schema = Schema(
                ("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName), ("points", LinkedFileField.Points),
                ("hidden", LinkedFileField.Hidden), ("desc", LinkedFileField.Description));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            var c1 = plan.CustomDefinitions.Single(definition => definition.Id == "c1");
            Assert.AreEqual("Renamed", c1.DisplayName);
            Assert.AreEqual(5, c1.Points);
            Assert.IsTrue(c1.Hidden);
            Assert.AreEqual("New text", c1.Description);
            Assert.AreEqual(1, plan.Report.Updated);
            Assert.IsTrue(plan.CustomChanged);
            Assert.IsFalse(plan.ManualChanged);
            Assert.IsTrue(plan.Report.Messages.Any(message => message.Contains("custom achievements only")));
        }

        [TestMethod]
        public void CreateUnmatchedRows_AddsDefinitionsWithIdsFromTheFileOrTheName()
        {
            var table = Table(
                new[] { "id", "name", "done" },
                new[] { "new_one", "New One", "true" },
                new[] { "", "Another One!", "false" },
                new[] { "c1", "", "true" },
                new[] { "", "", "true" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("name", LinkedFileField.DisplayName), ("done", LinkedFileField.Unlocked));
            schema.CreateUnmatchedRows = true;

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(2, plan.Report.Created);
            var created = plan.CustomDefinitions.Single(definition => definition.Id == "new_one");
            Assert.AreEqual("New One", created.DisplayName);
            Assert.IsTrue(created.Unlocked);
            Assert.AreEqual(Now, created.UnlockTimeUtc);
            var generated = plan.CustomDefinitions.Single(definition => definition.Id == "another-one");
            Assert.IsFalse(generated.Unlocked);
            Assert.IsTrue(plan.CustomDefinitions.Single(definition => definition.Id == "c1").Unlocked, "The existing custom row matched by id is updated, not recreated.");
            Assert.IsTrue(plan.Report.Messages.Any(message => message.Contains("neither an API name nor a name")));
            Assert.AreEqual(4, plan.CustomDefinitions.Count);

            var emptyGame = LinkedFileApplier.Build(
                Table(new[] { "name" }, new[] { "First" }),
                LinkedFileSchema.Normalize(new LinkedFileSchema
                {
                    Columns = new List<LinkedFileColumnMapping> { new LinkedFileColumnMapping { Column = "name", Field = LinkedFileField.DisplayName } },
                    CreateUnmatchedRows = true
                }),
                null, new List<AchievementDetail>(), null, null, Now);
            Assert.AreEqual(1, emptyGame.Report.Created, "A game with no achievements yet can still be seeded from the file.");
            Assert.IsTrue(emptyGame.CustomDefinitions.Single().Unlocked);
        }

        [TestMethod]
        public void NoAchievementsAndMissingColumns()
        {
            var noAchievements = LinkedFileApplier.Build(
                Table(new[] { "id" }, new[] { "x" }), Schema(("id", LinkedFileField.ApiName)), null, new List<AchievementDetail>(), null, null, Now);
            Assert.AreEqual(LinkedFileReadStatus.NoAchievements, noAchievements.Report.Status);
            Assert.IsFalse(noAchievements.ManualChanged);

            var missingColumn = LinkedFileApplier.Build(
                Table(new[] { "other" }, new[] { "x" }), Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked)), null, Achievements(), Link(), Definitions(), Now);
            Assert.AreEqual(2, missingColumn.Report.Messages.Count(message => message.Contains("is not in the file")));
            Assert.AreEqual(1, missingColumn.Report.UnmatchedRows);
            StringAssert.Contains(missingColumn.Report.Summarize(), "status=Ok rows=1 matched=0");
        }

        [TestMethod]
        public void ZeroProgressTotalMeansNoProgress()
        {
            // achievements.ini save files write CurProgress=0 and MaxProgress=0 for achievements without progress.
            var table = Table(
                new[] { "id", "done", "num", "den" },
                new[] { "ach_two", "1", "0", "0" },
                new[] { "c1", "1", "x", "0" });
            var schema = Schema(
                ("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked),
                ("num", LinkedFileField.ProgressNum), ("den", LinkedFileField.ProgressDenom));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            Assert.AreEqual(1, plan.Report.ParseErrors, "Only the unreadable progress fails.");
            Assert.IsTrue(plan.ManualChanged, "The zero-total row still unlocks.");
            Assert.IsFalse(plan.ManualProgress.Any());

            var failed = LinkedFileRowValues.Parse(table, 1, schema, null, Now);
            CollectionAssert.AreEquivalent(new[] { LinkedFileField.ProgressNum }, failed.FailedFields.ToArray());
        }

        [TestMethod]
        public void PlatformRowsOnlyAddUnlocksAndProgress()
        {
            var table = Table(
                new[] { "id", "done", "num" },
                new[] { "ach_one", "0", "" },
                new[] { "ach_two", "1", "3" },
                new[] { "c2", "0", "" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked), ("num", LinkedFileField.ProgressNum));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), null, Definitions(), Now);

            var update = plan.PlatformUpdates.Single();
            Assert.AreEqual("ach_two", update.ApiName, "A row that says locked leaves the platform's unlock alone.");
            Assert.IsTrue(update.Unlocked);
            Assert.AreEqual(Now, update.UnlockTimeUtc);
            Assert.AreEqual(3, update.ProgressNum);
            Assert.AreEqual(1, plan.Report.Unlocked);
            Assert.AreEqual(1, plan.Report.Relocked, "Only the custom achievement is locked by its row.");
            Assert.IsFalse(plan.ManualChanged);
            Assert.IsFalse(plan.Report.Messages.Any(message => message.Contains("not tracked")));

            var again = LinkedFileApplier.Build(
                Table(new[] { "id", "done" }, new[] { "ach_one", "1" }),
                Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked)),
                null, Achievements(), null, Definitions(), Now);
            Assert.AreEqual(0, again.PlatformUpdates.Count, "An achievement already unlocked keeps its state and time.");
            Assert.AreEqual(0, again.Report.Unlocked);
        }

        [TestMethod]
        public void ProgressOnLinkedDefinitionRowsGoesToTheCachePlan()
        {
            var table = Table(new[] { "id", "num", "den" }, new[] { "ach_two", "2", "5" }, new[] { "c1", "1", "4" });
            var schema = Schema(("id", LinkedFileField.ApiName), ("num", LinkedFileField.ProgressNum), ("den", LinkedFileField.ProgressDenom));

            var plan = LinkedFileApplier.Build(table, schema, null, Achievements(), Link(), Definitions(), Now);

            var progress = plan.ManualProgress.Single();
            Assert.AreEqual("ach_two", progress.ApiName);
            Assert.AreEqual(2, progress.ProgressNum);
            Assert.AreEqual(5, progress.ProgressDenom);
            Assert.IsFalse(plan.ManualChanged, "Progress alone does not touch the unlock map.");
            var c1 = plan.CustomDefinitions.Single(definition => definition.Id == "c1");
            Assert.AreEqual(1, c1.ProgressNum);
            Assert.AreEqual(4, c1.ProgressDenom);
            Assert.IsTrue(plan.CustomChanged);
        }

        [TestMethod]
        public void Validator_NamesWhatAMappingLacks()
        {
            CollectionAssert.AreEqual(
                new[] { LinkedFileSchemaProblem.NoMatchColumn },
                LinkedFileSchemaValidator.Validate(new LinkedFileSchema()).ToArray());
            CollectionAssert.AreEqual(
                new[] { LinkedFileSchemaProblem.NoColumns },
                LinkedFileSchemaValidator.Validate(null).ToArray());

            Assert.AreEqual(0, LinkedFileSchemaValidator.Validate(Schema(("id", LinkedFileField.ApiName), ("done", LinkedFileField.Unlocked))).Count);
            Assert.IsTrue(LinkedFileSchemaValidator.IsValid(Schema(("name", LinkedFileField.DisplayName))), "A listed row means unlocked.");
            Assert.IsTrue(LinkedFileSchemaValidator.IsValid(Schema(("done", LinkedFileField.Unlocked))), "Rows pair by order.");

            Assert.IsTrue(LinkedFileSchemaValidator.IsValid(new LinkedFileSchema { MatchColumn = "id" }), "A list of ids needs no other column.");

            var create = Schema(("done", LinkedFileField.Unlocked));
            create.CreateUnmatchedRows = true;
            CollectionAssert.AreEqual(
                new[] { LinkedFileSchemaProblem.CreateUnmatchedNeedsNameOrApiName },
                LinkedFileSchemaValidator.Validate(create).ToArray());

            var custom = Schema(("id", LinkedFileField.ApiName), ("when", LinkedFileField.UnlockTime));
            custom.TimeFormat = LinkedFileTimeFormat.Custom;
            CollectionAssert.AreEqual(
                new[] { LinkedFileSchemaProblem.CustomTimeFormatMissing },
                LinkedFileSchemaValidator.Validate(custom).ToArray());
        }
    }
}
