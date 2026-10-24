using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    /// <summary>
    /// Steam's October 2026 achievements redesign: the IPlayerService achievement methods,
    /// the redesigned achievements page payload, and achievement sets as categories. Fixtures
    /// are live captures of Portal 2 (620, two named sets) and DREDGE (1562430, unnamed base set
    /// plus two DLC sets), reduced to achievement data with the account id replaced.
    /// </summary>
    [TestClass]
    public class SteamAchievementSetTests
    {
        private const string TestSteamId = "76561198000000001";

        // ------------------------------------------------------------------
        // Achievements page (valve-ssr-data)
        // ------------------------------------------------------------------

        [TestMethod]
        public void SsrParser_PublicProfilePage_ReadsSchemaSetsAndUnlocks()
        {
            var page = SteamAchievementsSsrParser.Parse(LoadFixture("ssr_portal2_user_public.html"), 620);

            Assert.IsNotNull(page);
            Assert.IsFalse(page.ViewerLoggedIn);
            Assert.AreEqual(TestSteamId, page.TargetSteamId);
            Assert.AreEqual(51, page.Schema.Achievements.Count);
            Assert.IsTrue(page.Schema.HasAchievementSets);
            Assert.AreEqual("Single Player", page.Schema.Groups[2].Name);
            Assert.AreEqual("Cooperative", page.Schema.Groups[1].Name);

            Assert.AreEqual(SteamSsrQueryState.Success, page.UserAchievementsState);
            Assert.AreEqual(23, page.UserAchievements.ByInternalKey.Values.Count(a => a.Unlocked));
            Assert.AreEqual(1371296862L, page.UserAchievements.ByInternalKey[1].UnlockTime);

            var wakeUp = page.Schema.Achievements.Single(a => a.InternalKey == 1);
            Assert.AreEqual("ACH.SURVIVE_CONTAINER_RIDE", wakeUp.Name);
            Assert.AreEqual(2, wakeUp.GroupId);
            StringAssert.StartsWith(wakeUp.Icon, "https://");
            Assert.IsTrue(page.Schema.GlobalPercentages.ContainsKey("ACH.SURVIVE_CONTAINER_RIDE"));
        }

        [TestMethod]
        public void SsrParser_UnownedGame_ReadsEmptyUnlocksAsSuccess()
        {
            var page = SteamAchievementsSsrParser.Parse(LoadFixture("ssr_dredge_dlc_unowned.html"), 1562430);

            Assert.IsNotNull(page);
            Assert.AreEqual(SteamSsrQueryState.Success, page.UserAchievementsState);
            Assert.AreEqual(0, page.UserAchievements.ByInternalKey.Count);
            Assert.AreEqual(60, page.Schema.Achievements.Count);
            Assert.AreEqual(string.Empty, page.Schema.Groups[0].Name);
            Assert.AreEqual(2561440, page.Schema.Groups[1].DlcAppId);
            Assert.AreEqual(2561450, page.Schema.Groups[2].DlcAppId);
        }

        [TestMethod]
        public void SsrParser_GlobalPage_HasSchemaWithoutPlayerData()
        {
            var page = SteamAchievementsSsrParser.Parse(LoadFixture("ssr_portal2_global.html"), 620);

            Assert.IsNotNull(page);
            Assert.AreEqual(SteamSsrQueryState.Missing, page.UserAchievementsState);
            Assert.IsNull(page.UserAchievements);
            Assert.AreEqual(51, page.Schema.Achievements.Count);
        }

        [TestMethod]
        public void SsrParser_WrongAppOrNoPayload_ReturnsNull()
        {
            Assert.IsNull(SteamAchievementsSsrParser.Parse(LoadFixture("ssr_portal2_global.html"), 1562430));
            Assert.IsNull(SteamAchievementsSsrParser.Parse("<html><body>achieveRow</body></html>", 620));
            Assert.IsNull(SteamAchievementsSsrParser.Parse(
                "<script type=\"application/json\" id=\"valve-ssr-data\">{not json</script>", 620));
            Assert.IsNull(SteamAchievementsSsrParser.Parse(null, 620));
        }

        [TestMethod]
        public void SsrParser_ErroredPlayerQuery_IsErrorState_AndLoggedInFlagIsRead()
        {
            var queryData = JsonConvert.SerializeObject(new
            {
                queries = new object[]
                {
                    new
                    {
                        state = new { status = "error", data = (object)null },
                        queryKey = new object[] { "achievements", 620, "user_achievements", TestSteamId }
                    }
                }
            });
            var json = JsonConvert.SerializeObject(new
            {
                UserConfig = new { logged_in = true, steamid = "76561198000000002" },
                renderContext = new { queryData }
            });
            var html = $"<script type=\"application/json\" id=\"valve-ssr-data\">{json}</script>";

            var page = SteamAchievementsSsrParser.Parse(html, 620);

            Assert.IsNotNull(page);
            Assert.IsTrue(page.ViewerLoggedIn);
            Assert.AreEqual("76561198000000002", page.ViewerSteamId);
            Assert.AreEqual(SteamSsrQueryState.Error, page.UserAchievementsState);
        }

        [TestMethod]
        public void SsrParser_FriendPage_ReadsTargetNotViewer_InEitherOrder()
        {
            // A signed-in viewer's page for a friend carries both players' unlocks (the
            // "compare to your progress" toggle); the loader's targetSteamId picks the friend.
            const string viewer = "76561198000000001";
            const string friend = "76561198000000002";
            object UserQuery(string steamId, int key) => new
            {
                state = new
                {
                    status = "success",
                    data = new
                    {
                        appid = 620,
                        steamid = steamId,
                        achievements = new Dictionary<string, object>
                        {
                            [key.ToString()] = new { internal_key = key, unlocked = true, unlock_time = 1511385775 }
                        }
                    }
                },
                queryKey = new object[] { "achievements", 620, "user_achievements", steamId }
            };

            foreach (var order in new[] { new[] { friend, viewer }, new[] { viewer, friend } })
            {
                var queryData = JsonConvert.SerializeObject(new
                {
                    queries = order.Select(id => UserQuery(id, id == friend ? 30 : 1)).ToArray()
                });
                var json = JsonConvert.SerializeObject(new
                {
                    loaderData = new[] { "{\"steamid\":\"" + viewer + "\"}", "{\"appid\":620,\"targetSteamId\":\"" + friend + "\"}" },
                    UserConfig = new { logged_in = true, steamid = viewer },
                    renderContext = new { queryData }
                });

                var page = SteamAchievementsSsrParser.Parse(
                    $"<script type=\"application/json\" id=\"valve-ssr-data\">{json}</script>", 620);

                Assert.IsNotNull(page);
                Assert.AreEqual(friend, page.TargetSteamId);
                Assert.AreEqual(SteamSsrQueryState.Success, page.UserAchievementsState);
                CollectionAssert.AreEquivalent(new[] { 30 }, page.UserAchievements.ByInternalKey.Keys.ToArray());
            }
        }

        // ------------------------------------------------------------------
        // Web API payloads and unlock mapping
        // ------------------------------------------------------------------

        [TestMethod]
        public void MapSchema_ApiPayload_ReadsInternalKeysAndSets()
        {
            var schema = LoadApiSchema(1562430);

            Assert.AreEqual(60, schema.Achievements.Count);
            Assert.IsTrue(schema.Achievements.All(a => a.InternalKey.HasValue));
            Assert.IsTrue(schema.HasAchievementSets);
            Assert.AreEqual("The Pale Reach", schema.Groups[1].Name);
            Assert.AreEqual(2561440, schema.Groups[1].DlcAppId);
            Assert.AreEqual(3655650858L, schema.SchemaHash);
        }

        [TestMethod]
        public void MapUnlocks_WebApiAndPage_ProduceIdenticalUnlocks()
        {
            var apiSchema = LoadApiSchema(620);
            var apiUser = JsonConvert.DeserializeObject<GetUserAchievementsRoot>(LoadFixture("api_user_achievements_620.json")).Response;
            var userFromApi = new SteamUserAchievements
            {
                ByInternalKey = apiUser.Achievements.ToDictionary(a => a.InternalKey)
            };
            var page = SteamAchievementsSsrParser.Parse(LoadFixture("ssr_portal2_user_public.html"), 620);

            var fromApi = SteamAchievementSetMapper.MapUnlocks(620, apiSchema, userFromApi);
            var fromPage = SteamAchievementSetMapper.MapUnlocks(620, page.Schema, page.UserAchievements);

            Assert.AreEqual(0, fromApi.UnknownKeyCount);
            Assert.AreEqual(0, fromPage.UnknownKeyCount);
            Assert.AreEqual(23, fromApi.Unlocks.UnlockedApiNames.Count);
            CollectionAssert.AreEquivalent(
                fromApi.Unlocks.UnlockedApiNames.ToList(),
                fromPage.Unlocks.UnlockedApiNames.ToList());
            foreach (var apiName in fromApi.Unlocks.UnlockedApiNames)
            {
                Assert.AreEqual(fromApi.Unlocks.UnlockTimesUtc[apiName], fromPage.Unlocks.UnlockTimesUtc[apiName], apiName);
            }

            Assert.AreEqual(
                new DateTime(2013, 6, 15, 11, 47, 42, DateTimeKind.Utc),
                fromApi.Unlocks.UnlockTimesUtc["ACH.SURVIVE_CONTAINER_RIDE"]);
        }

        [TestMethod]
        public void MapUnlocks_UnknownKeysAndZeroTimes()
        {
            var schema = Schema(new SchemaAchievement { Name = "A", InternalKey = 1 });
            var user = User(
                new UserAchievement { InternalKey = 1, Unlocked = true, UnlockTime = 0 },
                new UserAchievement { InternalKey = 99, Unlocked = true, UnlockTime = 1700000000 });

            var mapping = SteamAchievementSetMapper.MapUnlocks(1, schema, user);

            Assert.AreEqual(1, mapping.UnknownKeyCount);
            Assert.IsTrue(mapping.Unlocks.UnlockedApiNames.Contains("A"));
            Assert.IsFalse(mapping.Unlocks.UnlockTimesUtc.ContainsKey("A"));
        }

        [TestMethod]
        public void MapUnlocks_FallsBackToPageSchemaForKeysTheApiSchemaLacks()
        {
            var schema = Schema(new SchemaAchievement { Name = "A", InternalKey = 1 });
            var pageSchema = Schema(new SchemaAchievement { Name = "B", InternalKey = 2 });
            var user = User(new UserAchievement { InternalKey = 2, Unlocked = true, UnlockTime = 1700000000 });

            var mapping = SteamAchievementSetMapper.MapUnlocks(1, schema, user, pageSchema);

            Assert.AreEqual(0, mapping.UnknownKeyCount);
            Assert.IsTrue(mapping.Unlocks.UnlockedApiNames.Contains("B"));
        }

        [TestMethod]
        public void MapUnlocks_LockedEntryIsNotUnlocked()
        {
            var schema = Schema(new SchemaAchievement { Name = "A", InternalKey = 1 });
            var user = User(new UserAchievement { InternalKey = 1, Unlocked = false });

            var mapping = SteamAchievementSetMapper.MapUnlocks(1, schema, user);

            Assert.AreEqual(0, mapping.Unlocks.UnlockedApiNames.Count);
        }

        [TestMethod]
        public void ResolveProgress_UsesSchemaRange()
        {
            var definition = new SchemaAchievement { MinProgress = 0, MaxProgress = 250 };

            Assert.AreEqual((120, 250), SteamAchievementSetMapper.ResolveProgress(
                definition, new UserAchievement { ProgressInt = 120 }));
            Assert.AreEqual((250, 250), SteamAchievementSetMapper.ResolveProgress(
                definition, new UserAchievement { Unlocked = true }));
            Assert.AreEqual((250, 250), SteamAchievementSetMapper.ResolveProgress(
                definition, new UserAchievement { ProgressInt = 400 }));
            Assert.IsNull(SteamAchievementSetMapper.ResolveProgress(
                definition, new UserAchievement { Unlocked = false }));
            Assert.IsNull(SteamAchievementSetMapper.ResolveProgress(
                new SchemaAchievement { MinProgress = 0, MaxProgress = 1 }, new UserAchievement { ProgressInt = 1 }));
            Assert.IsNull(SteamAchievementSetMapper.ResolveProgress(
                new SchemaAchievement(), new UserAchievement { ProgressInt = 5 }));
        }

        // ------------------------------------------------------------------
        // Achievement sets as categories
        // ------------------------------------------------------------------

        [TestMethod]
        public void ApplySets_NamedSets_BecomeBaseCategories()
        {
            var schema = LoadApiSchema(620);
            var achievements = Details(schema);

            var named = SteamAchievementSetMapper.ApplySets(achievements, schema, "Portal 2");

            var wakeUp = achievements.Single(a => a.ApiName == "ACH.SURVIVE_CONTAINER_RIDE");
            var highFive = achievements.Single(a => a.ApiName == "ACH.HI_FIVE_YOUR_PARTNER");
            Assert.AreEqual("Single Player", wakeUp.Category);
            Assert.AreEqual("Base", wakeUp.CategoryKind);
            Assert.IsNull(wakeUp.Tags);
            Assert.AreEqual("Cooperative", highFive.Category);
            Assert.AreEqual("Base", highFive.CategoryKind);
            Assert.IsNull(highFive.Tags);
            Assert.AreEqual(51, named.Count);
        }

        [TestMethod]
        public void ApplySets_BaseAndDlcSets_SplitByDlcAppId()
        {
            var schema = LoadApiSchema(1562430);
            var achievements = Details(schema);

            var named = SteamAchievementSetMapper.ApplySets(achievements, schema, "DREDGE");

            var baseAchievement = achievements.Single(a => a.ApiName == "CATCH_FISH_ROD_1");
            var dlcAchievement = achievements.Single(a => a.ApiName == "DLC_3_1");
            var otherDlc = achievements.Single(a => a.ApiName == "DLC_4_1");
            Assert.AreEqual("DREDGE", baseAchievement.Category);
            Assert.AreEqual("Base", baseAchievement.CategoryKind);
            Assert.IsNull(baseAchievement.Tags);
            Assert.AreEqual("The Pale Reach", dlcAchievement.Category);
            Assert.AreEqual("DLC", dlcAchievement.CategoryKind);
            Assert.IsNull(dlcAchievement.Tags);
            Assert.AreEqual("The Iron Rig", otherDlc.Category);
            Assert.AreEqual(20, named.Count);
            Assert.IsFalse(named.Contains("CATCH_FISH_ROD_1"));
        }

        [TestMethod]
        public void ApplySets_GameWithoutSets_KeepsNullCategory()
        {
            var schema = Schema(new SchemaAchievement { Name = "A", InternalKey = 1, GroupId = 0 });
            schema.Groups[0] = new SchemaAchievementGroup { GroupId = 0, Name = string.Empty };
            var achievements = new List<AchievementDetail> { new AchievementDetail { ApiName = "A" } };

            var named = SteamAchievementSetMapper.ApplySets(achievements, schema, "Game");

            Assert.IsNull(achievements[0].Category);
            Assert.IsNull(achievements[0].CategoryKind);
            Assert.IsNull(achievements[0].Tags);
            Assert.AreEqual(0, named.Count);
        }

        [TestMethod]
        public void ApplySets_ArchivedAchievementOrSet_IsUnobtainable()
        {
            var schema = Schema(
                new SchemaAchievement { Name = "retired", InternalKey = 1, GroupId = 1, Archived = true },
                new SchemaAchievement { Name = "in_archived_set", InternalKey = 2, GroupId = 2 },
                new SchemaAchievement { Name = "plain", InternalKey = 3, GroupId = 1 });
            schema.Groups[1] = new SchemaAchievementGroup { GroupId = 1, Name = "Season 1" };
            schema.Groups[2] = new SchemaAchievementGroup { GroupId = 2, Name = "Event", Archived = true, DlcAppId = 42 };
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "retired" },
                new AchievementDetail { ApiName = "in_archived_set" },
                new AchievementDetail { ApiName = "plain" }
            };

            SteamAchievementSetMapper.ApplySets(achievements, schema, "Game");

            Assert.AreEqual("Base", achievements[0].CategoryKind);
            Assert.AreEqual("Unobtainable", achievements[0].Tags);
            Assert.AreEqual("DLC", achievements[1].CategoryKind);
            Assert.AreEqual("Unobtainable", achievements[1].Tags);
            Assert.AreEqual("Base", achievements[2].CategoryKind);
            Assert.IsNull(achievements[2].Tags);
        }

        [TestMethod]
        public void ApplySets_DlcSetName_DropsGameNamePrefix()
        {
            Assert.AreEqual(
                "The Pale Reach",
                SteamAchievementSetMapper.ResolveSetLabel(
                    new SchemaAchievementGroup { GroupId = 1, Name = "DREDGE - The Pale Reach", DlcAppId = 1 },
                    "DREDGE"));
            Assert.AreEqual(
                "DREDGE",
                SteamAchievementSetMapper.ResolveSetLabel(new SchemaAchievementGroup { GroupId = 0 }, "DREDGE"));
        }

        [TestMethod]
        public void SteamHuntersReplacesSets()
        {
            var schema = LoadApiSchema(1562430);
            var achievements = Details(schema);
            SteamAchievementSetMapper.ApplySets(achievements, schema, "DREDGE");
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2561440,
                    DlcAppName = "Renamed By SteamHunters",
                    AchievementApiNames = new List<string> { "DLC_3_1" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "DREDGE");

            Assert.AreEqual("Renamed By SteamHunters", achievements.Single(a => a.ApiName == "DLC_3_1").Category);
            Assert.AreEqual("DREDGE", achievements.Single(a => a.ApiName == "DLC_4_1").Category);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_BaseThenDlcSets()
        {
            var plan = SteamAchievementSetMapper.BuildCategoryImagePlan(LoadApiSchema(1562430), "DREDGE", 1562430);

            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual(1562430, plan[0].Value);
            CollectionAssert.AreEquivalent(new[] { 2561440, 2561450 }, plan.Skip(1).Select(p => p.Value).ToList());
            Assert.AreEqual(0, SteamAchievementSetMapper.BuildCategoryImagePlan(
                Schema(new SchemaAchievement { Name = "A" }), "Game", 1).Count);
        }

        // ------------------------------------------------------------------
        // API client outcomes
        // ------------------------------------------------------------------

        [TestMethod]
        public async Task GetGameSchemaAsync_EResult42_IsNoAchievements()
        {
            var client = Client(_ => Response(HttpStatusCode.OK, "{\"response\":{}}", eResult: 42));

            var result = await client.GetGameSchemaAsync(null, 10, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(SteamApiOutcome.NoAchievements, result.Outcome);
            Assert.AreEqual(false, await client.GetGameHasAchievementsAsync(null, 10, "english", CancellationToken.None).ConfigureAwait(false));
        }

        [TestMethod]
        public async Task GetGameSchemaAsync_WithoutToken_OmitsAccessToken()
        {
            Uri captured = null;
            var client = Client(request =>
            {
                captured = request.RequestUri;
                return Response(HttpStatusCode.OK, LoadFixture("api_game_achievements_620.json"), eResult: 1);
            });

            var result = await client.GetGameSchemaAsync(null, 620, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.AreEqual(51, result.Value.Achievements.Count);
            Assert.IsFalse(captured.Query.Contains("access_token"));
        }

        [TestMethod]
        public async Task GetUserAchievementsAsync_PrivateProfile_IsAccessDenied()
        {
            var client = Client(_ => Response(HttpStatusCode.OK, "{\"response\":{}}", eResult: 15));

            var result = await client.GetUserAchievementsAsync(null, TestSteamId, 620, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(SteamApiOutcome.AccessDenied, result.Outcome);
            Assert.IsNull(result.Value);
        }

        [TestMethod]
        public async Task GetUserAchievementsAsync_UnownedGame_IsOkAndEmpty()
        {
            var client = Client(_ => Response(
                HttpStatusCode.OK,
                "{\"response\":{\"schema_version\":25,\"schema_hash\":3655650858}}",
                eResult: 1));

            var result = await client.GetUserAchievementsAsync(null, TestSteamId, 1562430, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.AreEqual(0, result.Value.ByInternalKey.Count);
            Assert.AreEqual(3655650858L, result.Value.SchemaHash);
        }

        [TestMethod]
        public async Task GetUserAchievementsAsync_ParsesUnlocksAndGroups()
        {
            var client = Client(_ => Response(HttpStatusCode.OK, LoadFixture("api_user_achievements_620.json"), eResult: 1));

            var result = await client.GetUserAchievementsAsync("token", TestSteamId, 620, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.AreEqual(23, result.Value.ByInternalKey.Values.Count(a => a.Unlocked));
            Assert.IsTrue(result.Value.Groups.ContainsKey(1));
            Assert.AreEqual("WebApi", result.Value.Source);
        }

        [TestMethod]
        public async Task SendService_RejectedToken_RetriesWithoutToken()
        {
            var requests = new List<Uri>();
            var client = Client(request =>
            {
                requests.Add(request.RequestUri);
                return request.RequestUri.Query.Contains("access_token")
                    ? Response(HttpStatusCode.Unauthorized, string.Empty)
                    : Response(HttpStatusCode.OK, LoadFixture("api_user_achievements_620.json"), eResult: 1);
            });

            var result = await client.GetUserAchievementsAsync("expired", TestSteamId, 620, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.IsTrue(result.TokenRejected);
            Assert.AreEqual(2, requests.Count);
            Assert.IsFalse(requests[1].Query.Contains("access_token"));
        }

        [TestMethod]
        public async Task SendService_RejectedToken_OnHiddenProfile_StaysFlagged()
        {
            var client = Client(request => request.RequestUri.Query.Contains("access_token")
                ? Response(HttpStatusCode.Forbidden, string.Empty)
                : Response(HttpStatusCode.OK, "{\"response\":{}}", eResult: 15));

            var user = await client.GetUserAchievementsAsync("expired", TestSteamId, 620, CancellationToken.None).ConfigureAwait(false);
            var schema = await client.GetGameSchemaAsync("expired", 620, "english", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(SteamApiOutcome.AccessDenied, user.Outcome);
            Assert.IsTrue(user.TokenRejected);
            Assert.IsTrue(schema.TokenRejected);
        }

        [TestMethod]
        public async Task SendService_AcceptedToken_IsNotFlagged()
        {
            var client = Client(_ => Response(HttpStatusCode.OK, LoadFixture("api_user_achievements_620.json"), eResult: 1));

            var result = await client.GetUserAchievementsAsync("valid", TestSteamId, 620, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.IsFalse(result.TokenRejected);
        }

        [TestMethod]
        public async Task SendService_ServerErrorOrUnknownEResult_IsTransient()
        {
            var serverError = Client(_ => Response(HttpStatusCode.InternalServerError, string.Empty));
            var busy = Client(_ => Response(HttpStatusCode.OK, "{\"response\":{}}", eResult: 20));

            Assert.AreEqual(SteamApiOutcome.Transient,
                (await serverError.GetUserAchievementsAsync(null, TestSteamId, 620, CancellationToken.None).ConfigureAwait(false)).Outcome);
            Assert.AreEqual(SteamApiOutcome.Transient,
                (await busy.GetUserAchievementsAsync(null, TestSteamId, 620, CancellationToken.None).ConfigureAwait(false)).Outcome);
        }

        [TestMethod]
        public async Task GetGlobalAchievementPercentagesAsync_KeysByInternalKey()
        {
            var client = Client(_ => Response(HttpStatusCode.OK, LoadFixture("api_global_percentages_620.json"), eResult: 1));

            var result = await client.GetGlobalAchievementPercentagesAsync(null, 620, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.IsOk);
            Assert.AreEqual(74d, result.Value[1], 0.01);
            Assert.AreEqual(51, result.Value.Count);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static SchemaAndPercentages LoadApiSchema(int appId)
        {
            var root = JsonConvert.DeserializeObject<GetGameAchievementsRoot>(LoadFixture($"api_game_achievements_{appId}.json"));
            return SteamApiClient.MapSchema(appId, root.Response);
        }

        private static List<AchievementDetail> Details(SchemaAndPercentages schema)
        {
            return schema.Achievements.Select(a => new AchievementDetail { ApiName = a.Name }).ToList();
        }

        private static SchemaAndPercentages Schema(params SchemaAchievement[] achievements)
        {
            return new SchemaAndPercentages
            {
                Achievements = achievements.ToList(),
                GlobalPercentages = new Dictionary<string, double>()
            };
        }

        private static SteamUserAchievements User(params UserAchievement[] achievements)
        {
            return new SteamUserAchievements
            {
                ByInternalKey = achievements.ToDictionary(a => a.InternalKey)
            };
        }

        private static string LoadFixture(string fileName)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Steam", "Achievements", fileName));
        }

        private static SteamApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            return new SteamApiClient(new HttpClient(new StubHandler(responder)), logger: null);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body, int? eResult = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json")
            };
            if (eResult.HasValue)
            {
                response.Headers.Add("x-eresult", eResult.Value.ToString());
            }

            return response;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }
    }
}
