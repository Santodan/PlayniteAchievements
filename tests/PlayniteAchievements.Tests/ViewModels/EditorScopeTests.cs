using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// Which games the editor lists decides which of its features are on, and the undo history of
    /// the library editor spans games: a gesture across two games undoes as one step, and a write
    /// to a third game leaves it alone.
    /// </summary>
    [TestClass]
    public class EditorScopeTests
    {
        private static readonly Guid GameA = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        private static readonly Guid GameB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid GameC = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

        private static readonly EditorEditIntent CapstoneEdit =
            EditorEditIntent.Command("Capstone", "LOCPlayAch_Dynamic_Capstone");

        private static readonly EditorEditIntent NameEdit =
            EditorEditIntent.FieldEdit("DisplayName", "LOCPlayAch_Common_Edit");

        [TestMethod]
        public void TheSingleGameFeatures_AreOn_OnlyForExactlyOneGame()
        {
            Assert.IsTrue(EditorScope.ForGame(GameA).AllowsSingleGameFeatures);
            Assert.AreEqual(GameA, EditorScope.ForGame(GameA).SingleGameId);
            Assert.IsFalse(EditorScope.ForGame(GameA).IsLibrary);

            Assert.IsFalse(EditorScope.Library().AllowsSingleGameFeatures, "every game");
            Assert.IsNull(EditorScope.Library().FilterGameIds);
            Assert.IsFalse(EditorScope.Library(new[] { GameA, GameB }).AllowsSingleGameFeatures, "two games");
            Assert.AreEqual(GameA, EditorScope.Library(new[] { GameA, GameA }).SingleGameId, "one game picked twice");
            Assert.IsNull(EditorScope.Library(new[] { Guid.Empty }).FilterGameIds, "an empty id picks nothing");
        }

        [TestMethod]
        public void AGestureAcrossTwoGames_IsOneStep_WithEachGamesFacets()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Capstones(), Capstones("a1"), true, CapstoneEdit, gameId: GameA);
            journal.Record(Capstones(), Capstones("b1"), true, CapstoneEdit, gameId: GameB);
            journal.CommitOpenStep();

            var entry = journal.Undo();
            Assert.IsNotNull(entry);
            Assert.IsFalse(journal.CanUndo, "one press for both games");
            CollectionAssert.AreEqual(new[] { GameA, GameB }, entry.GameFacets.Select(game => game.GameId).ToList());

            var a = Capstones("a1");
            entry.GameFacets[0].Facets.Single().ApplyBefore(a);
            Assert.AreEqual(0, a.Capstones?.Count ?? 0, "game A's own record goes back");
        }

        [TestMethod]
        public void AForeignWriteToAnotherGame_KeepsTheHistory()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Capstones(), Capstones("a1"), true, CapstoneEdit, gameId: GameA);
            journal.CommitOpenStep();

            journal.NoteForeignWrite(Capstones(), Capstones("c1"), GameC);
            Assert.IsTrue(journal.CanUndo, "game C's write says nothing about game A's step");

            journal.NoteForeignWrite(Capstones(), Capstones("a2"), GameA);
            Assert.IsFalse(journal.CanUndo, "a write over game A's own capstones puts its step in doubt");
        }

        [TestMethod]
        public void FieldEditsOfTheSameName_InTwoGames_AreKeptApart()
        {
            var journal = new EditorUndoJournal();

            journal.RecordRowValue("ach", "DisplayName", "Old A", "New A", NameEdit, GameA);
            journal.RecordRowValue("ach", "DisplayName", "Old B", "New B", NameEdit, GameB);
            journal.CommitOpenStep();

            var entry = journal.Undo();
            Assert.AreEqual(2, entry.RowValues.Count, "one change per game, not one overwriting the other");
            Assert.AreEqual("Old A", entry.RowValues.Single(change => change.GameId == GameA).OldValue);
            Assert.AreEqual("Old B", entry.RowValues.Single(change => change.GameId == GameB).OldValue);
        }

        private static GameCustomDataFile Capstones(params string[] apiNames)
        {
            return new GameCustomDataFile
            {
                Capstones = apiNames.Select((apiName, index) => new CapstoneAssignment { ApiName = apiName }).ToList()
            };
        }
    }
}
