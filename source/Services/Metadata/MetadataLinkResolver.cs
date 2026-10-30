using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Which page of a metadata source a game links to: the stored link, or one the source looks
    /// up. An automatic link keeps what its lookup searched from (its basis); when that changes, the
    /// source looks again, and the new pages replace the link only when they match more.
    /// </summary>
    internal sealed class MetadataLinkResolver
    {
        private readonly MetadataLinkStore _links;
        private readonly ILogger _logger;
        private readonly string _language;

        /// <param name="language">The plugin's language setting, which the game's titles are in.</param>
        public MetadataLinkResolver(MetadataLinkStore links, ILogger logger, string language = null)
        {
            _links = links;
            _logger = logger;
            _language = language;
        }

        /// <summary>
        /// The stored link, or a newly resolved one. One resolved from an id the game carries is
        /// stored at once; one found by name comes back as <c>Unconfirmed</c> and is stored only
        /// after its achievements match.
        /// </summary>
        /// <remarks>
        /// Unavailable is true when the source could not read what the lookup needed: nothing is
        /// stored, so the next refresh looks again, and the game keeps its previous values.
        /// </remarks>
        public async Task<(string Id, MetadataSourceLink Unconfirmed, bool Unavailable)> ResolveAsync(
            IMetadataProvider provider,
            Guid gameId,
            MetadataLinkRequest request,
            CancellationToken ct)
        {
            request.LinkIsUserSet = false;

            // A link the game implies is used as is and never stored; only a link the user set
            // (or cleared, which turns the source off for the game) takes its place.
            if (provider is IMetadataImplicitLink implicitLink &&
                implicitLink.ImplicitLinkId(request) is string implicitId &&
                !string.IsNullOrWhiteSpace(implicitId))
            {
                if (_links != null && _links.TryGet(gameId, provider.Key, out var own) && own.Origin == MetadataLinkOrigin.User)
                {
                    request.LinkIsUserSet = true;
                    return (own.IsNoMatch ? null : own.Id, null, false);
                }

                return (implicitId.Trim(), null, false);
            }

            var preference = (provider as IMetadataLookupPreference)?.LookupPreference;
            var basis = WithPreference(Basis(gameId, provider.Key, request, withLanguage: true), preference);
            if (_links != null && _links.TryGet(gameId, provider.Key, out var stored))
            {
                request.LinkIsUserSet = stored.Origin == MetadataLinkOrigin.User;
                var recorded = _links.GetLinkBasis(gameId, provider.Key);
                // A link that found pages keeps them when only the language changed: pairing in
                // English rescues it at fetch. One that found nothing is looked for again.
                var sameSearch = recorded == basis ||
                                 (!stored.IsNoMatch && recorded != null &&
                                  recorded == WithPreference(Basis(gameId, provider.Key, request, withLanguage: false), preference));
                if (request.LinkIsUserSet || recorded == null || sameSearch)
                {
                    // A link found before its basis was kept takes the current one, without a search.
                    if (!request.LinkIsUserSet && recorded != basis)
                    {
                        _links.SetLinkBasis(gameId, provider.Key, basis);
                    }

                    return (stored.IsNoMatch ? null : stored.Id, null, false);
                }

                if (!stored.IsNoMatch)
                {
                    var preferenceOnly = recorded != null && SearchPart(recorded) == SearchPart(basis);
                    return await ReviseAsync(provider, gameId, request, stored, basis, preferenceOnly, ct).ConfigureAwait(false);
                }

                _logger?.Debug($"[Metadata] {provider.Key}: what '{request.Game?.Name}' is searched by changed since it found no match; looking again.");
            }

            var resolved = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (resolved?.IsUnavailable == true)
            {
                _logger?.Debug($"[Metadata] {provider.Key} could not read what it needed to find '{request.Game?.Name}'; it looks again next refresh.");
                return (null, null, true);
            }

            _links?.SetLinkBasis(gameId, provider.Key, basis);

            if (resolved == null || string.IsNullOrWhiteSpace(resolved.Id))
            {
                // Looked for and not found: later refreshes skip the search until the link is reset
                // or what the game is searched by changes.
                _links?.SetAutoNoMatch(gameId, provider.Key);
                return (null, null, false);
            }

            if (resolved.FoundByName)
            {
                return (resolved.Id.Trim(), resolved, false);
            }

            _links?.SetAuto(gameId, provider.Key, resolved.Id, resolved.Label);
            return (resolved.Id.Trim(), null, false);
        }

        /// <summary>
        /// Looks again for a game whose automatic link was found from other search inputs (a
        /// category added or renamed, a link the user set on another source). The new pages
        /// replace the link only when they match more of the game's achievements than the link
        /// did at the last refresh, or as many when only the source's lookup preference changed
        /// (<paramref name="preferenceOnly"/>), since the new pages are the ones it now prefers;
        /// otherwise the link stays. Either way the new inputs are recorded, so the next refresh
        /// does not look again, unless the pages could not be read.
        /// </summary>
        private async Task<(string Id, MetadataSourceLink Unconfirmed, bool Unavailable)> ReviseAsync(
            IMetadataProvider provider,
            Guid gameId,
            MetadataLinkRequest request,
            MetadataLinkRecord stored,
            string basis,
            bool preferenceOnly,
            CancellationToken ct)
        {
            var resolved = await provider.ResolveLinkAsync(request, ct).ConfigureAwait(false);
            if (resolved?.IsUnavailable == true)
            {
                return (stored.Id, null, false);
            }

            _links.SetLinkBasis(gameId, provider.Key, basis);
            var before = _links.GetCoverage(gameId, provider.Key)?.Matched ?? 0;
            if (string.IsNullOrWhiteSpace(resolved?.Id) ||
                MetadataLinkIds.SameIds(resolved.Id, stored.Id) ||
                !(resolved.Matched > before || (preferenceOnly && resolved.Matched >= before)))
            {
                _logger?.Debug(
                    $"[Metadata] {provider.Key}: looked again for '{request.Game?.Name}' after its search inputs changed; " +
                    $"kept {stored.Id} ({before} matched; found {resolved?.Id ?? "nothing"}{(resolved?.Matched is int found ? $", {found} matched" : string.Empty)}).");
                return (stored.Id, null, false);
            }

            _logger?.Debug($"[Metadata] {provider.Key}: '{request.Game?.Name}' relinked from {stored.Id} ({before} matched) to {resolved.Id} ({resolved.Matched} matched).");
            _links.SetAuto(gameId, provider.Key, resolved.Id, resolved.Label);
            return (resolved.Id.Trim(), null, false);
        }

        /// <summary>
        /// What a lookup for the game searches from, as a short fingerprint: the game's name, its
        /// categories with their kinds, the links the user set on other sources, and the language
        /// its titles are in when that is not English (pairing differs by language, and English
        /// pairs as it always has). Links other sources found on their own are left out: they come
        /// and go with those sources' lookups, and what they found is already shared within the
        /// refresh that found it.
        /// </summary>
        /// <summary>
        /// The basis followed by the source's lookup preference, left readable so a change of the
        /// preference alone can be told apart; the basis alone at the preference's default.
        /// </summary>
        private static string WithPreference(string basis, string preference) =>
            string.IsNullOrWhiteSpace(preference) ? basis : basis + "|" + preference.Trim();

        /// <summary>The part of a basis that the game's search inputs make, without a lookup preference.</summary>
        private static string SearchPart(string basis)
        {
            var bar = basis?.IndexOf('|') ?? -1;
            return bar < 0 ? basis : basis.Substring(0, bar);
        }

        private string Basis(Guid gameId, string sourceKey, MetadataLinkRequest request, bool withLanguage)
        {
            var others = (_links?.GetAll(gameId) ?? new Dictionary<string, MetadataLinkRecord>())
                .Where(pair => pair.Value?.Origin == MetadataLinkOrigin.User && !pair.Value.IsNoMatch &&
                               !string.Equals(pair.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            var parts = new List<string> { "name:" + request.Game?.Name?.Trim() };
            if (withLanguage && !EnglishTitlePairing.IsEnglish(_language))
            {
                parts.Add("language:" + _language.Trim());
            }

            parts.AddRange((request.Data?.Achievements ?? new List<AchievementDetail>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.Category))
                .Select(row => "category:" + row.Category.Trim() + "|" + row.CategoryKind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(part => part, StringComparer.OrdinalIgnoreCase));
            parts.AddRange(others.Select(pair => "link:" + pair.Key + "=" + pair.Value.Id.Trim() + "|" + pair.Value.Label));
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts).ToLowerInvariant()));
                return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
            }
        }
    }
}
