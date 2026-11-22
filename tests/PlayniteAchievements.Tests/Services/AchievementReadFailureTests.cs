using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Tests.TestSupport;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// A read through <see cref="AchievementReadService"/> that fails throws, so the live reads
    /// over it keep what they show, and warns once per kind of failure.
    /// </summary>
    [TestClass]
    public class AchievementReadFailureTests
    {
        private readonly ManualReadScheduler _scheduler = new ManualReadScheduler();
        private readonly AchievementDataFeed _feed = new AchievementDataFeed(null, null);
        private readonly WarnLogger _logger = new WarnLogger();
        private string _failure;

        // The read connection: the read runs, or fails with the set message.
        private AchievementReadService Reads() =>
            new AchievementReadService(read => _failure != null ? throw new InvalidOperationException(_failure) : read(null), _logger);

        [TestMethod]
        public void LiveQuery_KeepsItsLastResult_AndEachKindOfFailureWarnsOnce()
        {
            var reads = Reads();
            var loads = 0;
            var live = new LiveQuery<string>(_feed, AchievementQuery.CurrentUser, _ => reads.Read(db => "read#" + ++loads), _scheduler);
            _scheduler.Drain();
            Assert.AreEqual("read#1", live.Result);

            _failure = "no such column: x";
            live.SetQuery(live.Query);
            _scheduler.Drain();
            live.SetQuery(live.Query);
            _scheduler.Drain();
            Assert.AreEqual("read#1", live.Result, "a failed read is not an empty result");
            Assert.AreEqual(1, _logger.Warnings);

            _failure = "database is locked";
            live.SetQuery(live.Query);
            _scheduler.Drain();
            Assert.AreEqual(2, _logger.Warnings, "another kind of failure warns too");

            _failure = null;
            live.SetQuery(live.Query);
            _scheduler.Drain();
            Assert.AreEqual("read#2", live.Result);
        }

        [TestMethod]
        public void PagedRows_KeepsItsRows_WhenTheKeysOrAPageFailToRead()
        {
            var reads = Reads();
            var total = 3;
            var rows = new PagedRows<string>(
                _feed,
                AchievementQuery.CurrentUser,
                _ => reads.Read<IReadOnlyList<AchievementQuerySql.RowKey>>(db =>
                    Enumerable.Range(0, total).Select(index => new AchievementQuerySql.RowKey { DefinitionId = index }).ToList()),
                (_, keys) => reads.Read<IReadOnlyList<string>>(db => keys.Select(key => "row" + key.DefinitionId).ToList()),
                () => "placeholder",
                _scheduler);
            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { "row0", "row1", "row2" }, rows.ToList());

            _failure = "no such table: Games";
            total = 0;
            rows.Refresh();
            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { "row0", "row1", "row2" }, rows.ToList(), "a failed read is not an empty list");
            Assert.AreEqual(1, _logger.Warnings);

            _failure = null;
            rows.Refresh();
            _scheduler.Drain();
            Assert.AreEqual(0, rows.Count);
        }

        private sealed class WarnLogger : ILogger
        {
            public int Warnings { get; private set; }

            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) => Warnings++;
            public void Warn(Exception exception, string message) => Warnings++;
        }
    }
}
