using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Raised when the Guild Wars 2 API rejects the token. The API answers 401 with
    /// "Invalid access token" for a malformed or revoked key, so this is an ordinary state the
    /// refresh pipeline surfaces as "authentication required" rather than an error.
    /// </summary>
    internal sealed class Gw2AuthorizationException : Exception
    {
        public Gw2AuthorizationException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    /// <summary>
    /// Raised when the key is valid but lacks a scope the refresh needs. Scopes are chosen by the
    /// player when they create the key, so a key without progression authenticates happily and then
    /// returns nothing useful.
    /// </summary>
    internal sealed class Gw2MissingPermissionException : Exception
    {
        public Gw2MissingPermissionException(string permission, string message)
            : base(message)
        {
            Permission = permission;
        }

        public string Permission { get; }
    }

    /// <summary>Raised for transport and server-side failures that survived the retry policy.</summary>
    internal sealed class Gw2ApiException : Exception
    {
        public Gw2ApiException(string message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>The /v2/build id. Changes only when ArenaNet ships a game build.</summary>
    internal sealed class Gw2Build
    {
        [JsonProperty("id")]
        public int Id { get; set; }
    }

    /// <summary>/v2/tokeninfo. Reports the scopes the player granted the key.</summary>
    internal sealed class Gw2TokenInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("permissions")]
        public List<string> Permissions { get; set; }
    }

    /// <summary>/v2/account. Supplies the account identity shown on the settings card.</summary>
    internal sealed class Gw2Account
    {
        /// <summary>Account GUID. Stable across display-name changes, so it is the stored identity.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>The account name, in Player.1234 form.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>One rung of an achievement's tier ladder.</summary>
    internal sealed class Gw2Tier
    {
        /// <summary>Running total this tier is earned at.</summary>
        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>Achievement points this tier alone awards.</summary>
        [JsonProperty("points")]
        public int Points { get; set; }
    }

    /// <summary>/v2/achievements. The definition half, identical for every account.</summary>
    internal sealed class Gw2Achievement
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>Present on roughly one achievement in eleven; the category icon covers the rest.</summary>
        [JsonProperty("icon")]
        public string Icon { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>What the player has to do. Often the only populated prose of the three.</summary>
        [JsonProperty("requirement")]
        public string Requirement { get; set; }

        [JsonProperty("locked_text")]
        public string LockedText { get; set; }

        /// <summary>Default or ItemSet.</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Permanent, Daily, Weekly, Hidden, Repeatable, Pvp, RequiresUnlock and others.</summary>
        [JsonProperty("flags")]
        public List<string> Flags { get; set; }

        [JsonProperty("tiers")]
        public List<Gw2Tier> Tiers { get; set; }

        [JsonProperty("point_cap")]
        public int? PointCap { get; set; }
    }

    /// <summary>/v2/achievements/categories. 355 of them, every one with an icon.</summary>
    internal sealed class Gw2Category
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }

        [JsonProperty("icon")]
        public string Icon { get; set; }

        /// <summary>
        /// Achievement ids in the order the category displays them. The default schema serves plain
        /// integers here; only the 2024-07-20 schema and later wrap them in objects, and the client
        /// deliberately does not ask for that.
        /// </summary>
        [JsonProperty("achievements")]
        public List<int> Achievements { get; set; }
    }

    /// <summary>/v2/achievements/groups. The 19 top-level tabs of the in-game panel.</summary>
    internal sealed class Gw2Group
    {
        /// <summary>A GUID string rather than an integer, unlike every other id in this API.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }

        [JsonProperty("categories")]
        public List<int> Categories { get; set; }
    }

    /// <summary>
    /// /v2/account/achievements. Only achievements the account has touched appear; one absent from
    /// this list has no progress at all.
    /// </summary>
    internal sealed class Gw2AccountAchievement
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>Running progress toward Max. Absent once the ladder is finished.</summary>
        [JsonProperty("current")]
        public int? Current { get; set; }

        /// <summary>The next unearned tier's threshold, not the ladder's final one.</summary>
        [JsonProperty("max")]
        public int? Max { get; set; }

        /// <summary>True once every tier is earned.</summary>
        [JsonProperty("done")]
        public bool Done { get; set; }

        /// <summary>Per-objective checklist state. Meaning varies per achievement.</summary>
        [JsonProperty("bits")]
        public List<int> Bits { get; set; }

        /// <summary>How many times a repeatable achievement has been completed.</summary>
        [JsonProperty("repeated")]
        public int? Repeated { get; set; }

        /// <summary>Absent means unlocked; only a locked achievement states this explicitly.</summary>
        [JsonProperty("unlocked")]
        public bool? Unlocked { get; set; }
    }

    /// <summary>
    /// The definition half of a refresh: every group, category and achievement for one language.
    /// Cached on disk and reused until the game build changes.
    /// </summary>
    internal sealed class Gw2Catalog
    {
        /// <summary>The /v2/build id this catalog was fetched at.</summary>
        [JsonProperty("buildId")]
        public int BuildId { get; set; }

        /// <summary>The lang the text was fetched in.</summary>
        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("groups")]
        public List<Gw2Group> Groups { get; set; }

        [JsonProperty("categories")]
        public List<Gw2Category> Categories { get; set; }

        [JsonProperty("achievements")]
        public List<Gw2Achievement> Achievements { get; set; }

        [JsonIgnore]
        public bool IsUsable =>
            Groups != null && Groups.Count > 0 &&
            Categories != null && Categories.Count > 0 &&
            Achievements != null && Achievements.Count > 0;
    }
}
