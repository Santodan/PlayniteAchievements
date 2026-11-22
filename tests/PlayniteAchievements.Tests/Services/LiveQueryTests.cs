using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Tests.TestSupport;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class LiveQueryTests
    {
        private static readonly Guid GameA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid GameB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        private readonly ManualReadScheduler _scheduler = new ManualReadScheduler();
        private readonly AchievementDataFeed _feed = new AchievementDataFeed(null, null);
        private readonly List<AchievementQuery> _loads = new List<AchievementQuery>();
        private readonly List<string> _applied = new List<string>();

        private LiveQuery<string> Create(AchievementQuery query)
        {
            var live = new LiveQuery<string>(_feed, query, Load, _scheduler);
            live.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LiveQuery<string>.Result))
                {
                    _applied.Add(live.Result);
                }
            };
            return live;
        }

        private string Load(AchievementQuery query)
        {
            // Each result names its query and the number of the load that read it.
            _loads.Add(query);
            return (query.GameIds == null ? "all" : string.Join(",", query.GameIds)) + "#" + _loads.Count;
        }

        [TestMethod]
        public void LoadsOnCreate_OffTheUiThread_AndAppliesOnIt()
        {
            var live = Create(AchievementQuery.CurrentUser);

            Assert.IsNull(live.Result);
            _scheduler.RunReads();
            Assert.AreEqual(1, _loads.Count);
            Assert.IsNull(live.Result, "the result waits for the UI queue");
            _scheduler.RunUi();
            Assert.AreEqual("all#1", live.Result);
        }

        [TestMethod]
        public void TouchingChanges_CoalesceIntoOneRerunPerWindow()
        {
            var live = Create(AchievementQuery.CurrentUser.ForGames(new[] { GameA }));
            _scheduler.Drain();

            _feed.Publish(AchievementDataChange.ForGames(new[] { GameA }));
            _feed.Publish(AchievementDataChange.ForGames(new[] { GameA }));
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            Assert.AreEqual(1, _loads.Count, "nothing reruns before the window ends");

            _scheduler.Elapse();
            _scheduler.Drain();
            Assert.AreEqual(2, _loads.Count);
            Assert.AreEqual(GameA + "#2", live.Result);
        }

        [TestMethod]
        public void TenRefreshSaves_InOneWindow_RerunTheLibraryReadOnce()
        {
            var live = Create(AchievementQuery.CurrentUser.WithSummaryRules());
            _scheduler.Drain();

            // A bulk refresh publishes each game's save on its own.
            for (var i = 0; i < 10; i++)
            {
                _feed.Publish(AchievementDataChange.ForGames(new[] { Guid.NewGuid() }));
            }

            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();
            Assert.AreEqual(2, _loads.Count, "the first load and one rerun");
            Assert.AreEqual("all#2", live.Result);
        }

        [TestMethod]
        public void ChangesThatDoNotTouchTheQuery_AreIgnored()
        {
            Create(AchievementQuery.CurrentUser.ForGames(new[] { GameA }));
            _scheduler.Drain();

            _feed.Publish(AchievementDataChange.ForGames(new[] { GameB }));
            _feed.RaiseFriendChange(FriendCacheChange.ForRoster("Steam"));
            _scheduler.Drain();

            Assert.AreEqual(0, _scheduler.PendingTimers);
            Assert.AreEqual(1, _loads.Count);
        }

        [TestMethod]
        public void SetQuery_LoadsNow_AndTheOlderQuerysResultIsNeverApplied()
        {
            var live = Create(AchievementQuery.CurrentUser.ForGames(new[] { GameA }));
            live.SetQuery(AchievementQuery.CurrentUser.ForGames(new[] { GameB }));
            _scheduler.Drain();

            Assert.AreEqual(2, _loads.Count, "the second load waits for the first and then runs");
            CollectionAssert.AreEqual(new[] { GameB + "#2" }, _applied);
        }

        [TestMethod]
        public void RerunDuringALoad_AppliesTheLoadThenRunsAgain()
        {
            var live = Create(AchievementQuery.CurrentUser);
            _scheduler.Drain();

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.RunUi();
            Assert.AreEqual(1, _scheduler.PendingReads, "the rerun is reading");

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.RunUi();
            _scheduler.Elapse();
            _scheduler.RunUi();
            Assert.AreEqual(1, _scheduler.PendingReads, "a second rerun waits for the first");

            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { "all#1", "all#2", "all#3" }, _applied);
            Assert.AreEqual(3, _loads.Count);
        }

        [TestMethod]
        public void Dispose_StopsReruns_AndDropsTheResult()
        {
            var live = Create(AchievementQuery.CurrentUser);
            _scheduler.Drain();

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            live.Dispose();
            _scheduler.Elapse();
            _scheduler.Drain();

            Assert.AreEqual(1, _loads.Count);
            Assert.IsNull(live.Result);

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            Assert.AreEqual(0, _scheduler.PendingTimers);
        }

        [TestMethod]
        public void Dispose_DuringALoad_DropsItsResult()
        {
            var live = Create(AchievementQuery.CurrentUser);
            live.Dispose();
            _scheduler.Drain();

            Assert.AreEqual(1, _loads.Count);
            Assert.AreEqual(0, _applied.Count);
        }

        [TestMethod]
        public void FailedLoad_KeepsTheLastResult()
        {
            var fail = false;
            Exception error = null;
            var live = new LiveQuery<string>(
                _feed,
                AchievementQuery.CurrentUser,
                query => fail ? throw new InvalidOperationException("read failed") : "ok",
                _scheduler,
                onError: ex => error = ex);
            _scheduler.Drain();

            fail = true;
            live.SetQuery(AchievementQuery.CurrentUser.ForGames(new[] { GameA }));
            _scheduler.Drain();

            Assert.AreEqual("ok", live.Result);
            Assert.IsInstanceOfType(error, typeof(InvalidOperationException));

            fail = false;
            live.SetQuery(AchievementQuery.CurrentUser);
            _scheduler.Drain();
            Assert.AreEqual("ok", live.Result, "a failed load does not block the next");
        }
    }
}
