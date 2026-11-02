using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>One game of a collection: a base category and its achievements.</summary>
    internal sealed class MetadataSubGame
    {
        public MetadataSubGame(string name, IReadOnlyList<AchievementDetail> rows)
        {
            Name = name;
            Rows = rows;
        }

        public string Name { get; }

        public IReadOnlyList<AchievementDetail> Rows { get; }

        /// <summary>
        /// The games of a collection: its base categories (a category that is not DLC, a subset or
        /// an update), when it has at least two. Empty for a game that is one game.
        /// </summary>
        public static IReadOnlyList<MetadataSubGame> From(IEnumerable<AchievementDetail> achievements)
        {
            var games = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.Category))
                .GroupBy(row => row.Category.Trim(), CategoryPathHelper.LabelComparer)
                .Where(group =>
                {
                    var kind = group.Select(row => row.CategoryKind).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    return !CategoryKindHelper.IsDlc(kind) && !CategoryKindHelper.IsSubset(kind) && !CategoryKindHelper.IsUpdate(kind);
                })
                .Select(group => new MetadataSubGame(group.Key, group.ToList()))
                .ToList();
            return games.Count >= 2 ? games : (IReadOnlyList<MetadataSubGame>)Array.Empty<MetadataSubGame>();
        }
    }
}
