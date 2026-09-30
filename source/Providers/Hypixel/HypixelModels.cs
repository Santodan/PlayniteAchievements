using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// A player's achievements as the public hypixel.net profile page lists them: one panel per
    /// game, in page order.
    /// </summary>
    internal sealed class HypixelProfile
    {
        public List<HypixelProfilePanel> Panels { get; } = new List<HypixelProfilePanel>();
    }

    /// <summary>
    /// One game panel of the profile page (<c>div.panel.game-&lt;slug&gt;</c>).
    /// </summary>
    internal sealed class HypixelProfilePanel
    {
        /// <summary>The panel's CSS slug, e.g. "murder-mystery".</summary>
        public string Slug { get; set; }

        /// <summary>The panel heading as shown on the page, e.g. "Murder Mystery".</summary>
        public string DisplayName { get; set; }

        /// <summary>Absolute URL of the game icon in the panel heading, or null when it has none.</summary>
        public string IconUrl { get; set; }

        public List<HypixelProfileEntry> Entries { get; } = new List<HypixelProfileEntry>();
    }

    /// <summary>
    /// One achievement row of a panel. Tiered achievements list one row per tier, each carrying
    /// the tier number, its threshold and the player's running value.
    /// </summary>
    internal sealed class HypixelProfileEntry
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Completed { get; set; }

        /// <summary>True for rows in a "(Legacy)" section, which can no longer be earned.</summary>
        public bool Legacy { get; set; }

        /// <summary>Tier number for a tiered row; null for a one-time achievement.</summary>
        public int? Tier { get; set; }

        public long? Progress { get; set; }
        public long? Amount { get; set; }

        public bool IsTiered => Tier.HasValue;
    }

    /// <summary>
    /// Keyless <c>/v2/resources/achievements</c> response: every achievement definition, keyed by
    /// game and then by the achievement's own identifier.
    /// </summary>
    internal sealed class HypixelAchievementsResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("lastUpdated")]
        public long LastUpdated { get; set; }

        [JsonProperty("achievements")]
        public Dictionary<string, HypixelGameDefinitions> Achievements { get; set; }
    }

    internal sealed class HypixelGameDefinitions
    {
        [JsonProperty("one_time")]
        public Dictionary<string, HypixelOneTimeDefinition> OneTime { get; set; }

        [JsonProperty("tiered")]
        public Dictionary<string, HypixelTieredDefinition> Tiered { get; set; }
    }

    internal sealed class HypixelOneTimeDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("points")]
        public int? Points { get; set; }

        [JsonProperty("globalPercentUnlocked")]
        public double? GlobalPercentUnlocked { get; set; }

        [JsonProperty("legacy")]
        public bool Legacy { get; set; }
    }

    internal sealed class HypixelTieredDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("tiers")]
        public List<HypixelTierDefinition> Tiers { get; set; }

        [JsonProperty("legacy")]
        public bool Legacy { get; set; }
    }

    internal sealed class HypixelTierDefinition
    {
        [JsonProperty("tier")]
        public int Tier { get; set; }

        [JsonProperty("points")]
        public int? Points { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }
    }

    /// <summary>
    /// hypixel.net answered 404 for the username: no Hypixel player has that name.
    /// </summary>
    internal sealed class HypixelPlayerNotFoundException : Exception
    {
        public HypixelPlayerNotFoundException(string username)
            : base($"No Hypixel player named '{username}'.")
        {
            Username = username;
        }

        public string Username { get; }
    }

    /// <summary>
    /// A non-success HTTP status from hypixel.net or api.hypixel.net, kept as a status code so
    /// callers can act on it.
    /// </summary>
    internal sealed class HypixelApiException : Exception
    {
        public HypixelApiException(HttpStatusCode statusCode, Uri uri)
            : base($"HTTP {(int)statusCode} from {uri}.")
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}
