using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Paged response from the FFXIV Collect achievements catalog endpoint
    /// (GET /api/achievements).
    /// </summary>
    internal sealed class FfxivAchievementsResponse
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("results")]
        public List<FfxivAchievement> Results { get; set; }
    }

    /// <summary>
    /// A single achievement definition from FFXIV Collect. The same shape is used
    /// by the catalog endpoint and the character /achievements/owned endpoint.
    /// </summary>
    internal sealed class FfxivAchievement
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("points")]
        public int? Points { get; set; }

        [JsonProperty("patch")]
        public string Patch { get; set; }

        /// <summary>
        /// Global ownership percentage as a string, e.g. "98%".
        /// </summary>
        [JsonProperty("owned")]
        public string Owned { get; set; }

        /// <summary>
        /// Icon URL. FFXIV Collect serves these via the XIVAPI v2 asset endpoint in
        /// webp format; <see cref="FfxivApiClient"/> rewrites it to png.
        /// </summary>
        [JsonProperty("icon")]
        public string Icon { get; set; }

        [JsonProperty("category")]
        public FfxivNamedRef Category { get; set; }

        [JsonProperty("type")]
        public FfxivNamedRef Type { get; set; }
    }

    /// <summary>
    /// A named id reference used for achievement category and type.
    /// </summary>
    internal sealed class FfxivNamedRef
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// Character response from GET /api/characters/{id}?times=true.
    /// </summary>
    internal sealed class FfxivCharacter
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("server")]
        public string Server { get; set; }

        [JsonProperty("data_center")]
        public string DataCenter { get; set; }

        [JsonProperty("achievements")]
        public FfxivCharacterAchievements Achievements { get; set; }
    }

    /// <summary>
    /// Per-character achievement summary plus the obtained list (with timestamps
    /// when requested via ?times=true).
    /// </summary>
    internal sealed class FfxivCharacterAchievements
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        /// <summary>
        /// False when the character has hidden achievements on the Lodestone, in
        /// which case <see cref="Obtained"/> is empty.
        /// </summary>
        [JsonProperty("public")]
        public bool Public { get; set; }

        [JsonProperty("obtained")]
        public List<FfxivObtainedAchievement> Obtained { get; set; }
    }

    /// <summary>
    /// A single owned achievement with its unlock timestamp.
    /// </summary>
    internal sealed class FfxivObtainedAchievement
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>
        /// UTC unlock timestamp (FFXIV Collect returns ISO-8601 with a Z suffix).
        /// </summary>
        [JsonProperty("time")]
        public DateTime? Time { get; set; }
    }

    /// <summary>
    /// Non-success response from an FFXIV Collect or Lodestone request. Carries the
    /// status code so callers can act on it; EnsureSuccessStatusCode
    /// discards it into an untranslated message string.
    /// </summary>
    internal sealed class FfxivApiException : Exception
    {
        public FfxivApiException(HttpStatusCode statusCode, Uri requestUri)
            : base($"FFXIV request to {requestUri} failed with HTTP {(int)statusCode}.")
        {
            StatusCode = statusCode;
            RequestUri = requestUri;
        }

        public HttpStatusCode StatusCode { get; }

        public Uri RequestUri { get; }
    }

    /// <summary>
    /// The character resolves on the Lodestone but FFXIV Collect has no record of it.
    /// FFXIV Collect indexes characters on demand: one has to be added there once,
    /// via the Add button on its search result, before the character endpoint answers.
    /// </summary>
    internal sealed class FfxivCharacterNotIndexedException : Exception
    {
        public FfxivCharacterNotIndexedException(long lodestoneId)
            : base($"Character {lodestoneId} is not indexed on FFXIV Collect.")
        {
            LodestoneId = lodestoneId;
        }

        public long LodestoneId { get; }
    }

    /// <summary>
    /// Outcome of a Lodestone character lookup.
    /// </summary>
    internal enum FfxivResolveOutcome
    {
        /// <summary>The search returned a character id.</summary>
        Resolved,

        /// <summary>The search ran and the name, world, and region matched nothing.</summary>
        NoMatch,

        /// <summary>The search could not be completed, so match status is unknown.</summary>
        LookupFailed
    }

    /// <summary>
    /// Result of <c>FfxivApiClient.ResolveCharacterIdAsync</c>. Keeps "matched nothing"
    /// distinct from "could not ask", so a Lodestone outage is not reported as a
    /// mistyped character name.
    /// </summary>
    internal sealed class FfxivCharacterResolution
    {
        private FfxivCharacterResolution(FfxivResolveOutcome outcome, long characterId)
        {
            Outcome = outcome;
            CharacterId = characterId;
        }

        public FfxivResolveOutcome Outcome { get; }

        /// <summary>The resolved id, or 0 for any outcome other than <see cref="FfxivResolveOutcome.Resolved"/>.</summary>
        public long CharacterId { get; }

        public static FfxivCharacterResolution Resolved(long characterId)
        {
            return characterId > 0
                ? new FfxivCharacterResolution(FfxivResolveOutcome.Resolved, characterId)
                : NoMatch();
        }

        public static FfxivCharacterResolution NoMatch()
        {
            return new FfxivCharacterResolution(FfxivResolveOutcome.NoMatch, 0);
        }

        public static FfxivCharacterResolution LookupFailed()
        {
            return new FfxivCharacterResolution(FfxivResolveOutcome.LookupFailed, 0);
        }
    }
}
