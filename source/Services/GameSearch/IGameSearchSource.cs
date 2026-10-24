using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.GameSearch
{
    /// <summary>One game a search found, or the game a pasted id or address names.</summary>
    public sealed class GameSearchResult
    {
        public GameSearchResult(string id, string name, string iconUrl = null, string platforms = null)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
            IconUrl = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl;
            Platforms = string.IsNullOrWhiteSpace(platforms) ? null : platforms;
        }

        /// <summary>What the source stores for the game (an appId, a page slug); never shown.</summary>
        public string Id { get; }

        public string Name { get; }

        public string IconUrl { get; }

        /// <summary>The platforms the source lists the game on ("Steam, PS4"); null when it lists none.</summary>
        public string Platforms { get; }

        /// <summary>True for the game named by an id or address typed into the search box.</summary>
        public bool IsDirect { get; internal set; }
    }

    /// <summary>
    /// A place games can be looked up by name, for a picker that stores the chosen game's id: a
    /// manual tracking source, a metadata source's pages, a provider's catalog for an override.
    /// </summary>
    public interface IGameSearchSource
    {
        /// <summary>The source's name, for the picker's title.</summary>
        string DisplayName { get; }

        /// <summary>True when one link can name several games (a collection's), so the picker takes several.</summary>
        bool AllowsMultiple { get; }

        /// <summary>
        /// The games matching <paramref name="query"/>, best first or in the source's own order.
        /// Throws <see cref="GameSearchUnavailableException"/> when the source could not be read.
        /// </summary>
        Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct);

        /// <summary>The id an address or id typed into the search box names; null when it names none.</summary>
        string TryParseId(string text);

        /// <summary>The game an id names, with its title when the source can tell; the id as its name otherwise.</summary>
        Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct);
    }

    /// <summary>A search the source could not answer, shown as a failure rather than as no games found.</summary>
    public sealed class GameSearchUnavailableException : Exception
    {
        public GameSearchUnavailableException(string message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
