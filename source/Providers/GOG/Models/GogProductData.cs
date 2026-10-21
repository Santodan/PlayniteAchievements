using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Playnite.SDK.Data;

namespace PlayniteAchievements.Providers.GOG.Models
{
    /// <summary>
    /// Product data from GOGDB.
    /// Used to lookup the GOG client_id for a given product_id.
    /// API: https://www.gogdb.org/data/products/{product_id}/product.json
    /// </summary>
    [DataContract]
    internal sealed class GogProductData
    {
        /// <summary>
        /// GOG product ID.
        /// </summary>
        [DataMember(Name = "id")]
        public long Id { get; set; }

        /// <summary>
        /// GOG client ID (used for achievements API).
        /// </summary>
        [DataMember(Name = "client_id")]
        public string ClientId { get; set; }

        // Newer payloads may use camelCase for this field.
        [DataMember(Name = "clientId")]
        public string ClientIdCamel { get; set; }

        [DataMember(Name = "builds")]
        public List<GogProductBuild> Builds { get; set; }

        [IgnoreDataMember]
        public string ResolvedClientId =>
            !string.IsNullOrWhiteSpace(ClientId) ? ClientId : ClientIdCamel;

        /// <summary>
        /// Maximum number of build metadata links tried when resolving a client_id.
        /// </summary>
        internal const int MaxBuildMetaCandidates = 5;

        /// <summary>
        /// Build metadata links to try for a client_id, listed builds first, then newest published.
        /// All builds of a product share one client_id, so later links back up a failing one.
        /// </summary>
        [IgnoreDataMember]
        public IReadOnlyList<string> CandidateBuildMetaUrls
        {
            get
            {
                if (Builds == null || Builds.Count == 0)
                {
                    return new List<string>();
                }

                return Builds
                    .Where(b => b != null && !string.IsNullOrWhiteSpace(b.Link))
                    .OrderByDescending(b => b.Listed)
                    .ThenByDescending(b => b.PublishedUtc ?? DateTime.MinValue)
                    .Select(b => b.Link.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxBuildMetaCandidates)
                    .ToList();
            }
        }
    }

    [DataContract]
    internal sealed class GogProductBuild
    {
        [DataMember(Name = "link")]
        public string Link { get; set; }

        [DataMember(Name = "listed")]
        public bool Listed { get; set; }

        [DataMember(Name = "date_published")]
        public string DatePublished { get; set; }

        [IgnoreDataMember]
        public DateTime? PublishedUtc
        {
            get
            {
                if (string.IsNullOrWhiteSpace(DatePublished))
                {
                    return null;
                }

                if (DateTime.TryParse(DatePublished, out var value))
                {
                    return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
                }

                return null;
            }
        }
    }

    [DataContract]
    internal sealed class GogBuildMetaResponse
    {
        [DataMember(Name = "clientId")]
        public string ClientId { get; set; }
    }
}
