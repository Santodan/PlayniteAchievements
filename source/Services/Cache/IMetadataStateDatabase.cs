using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.Database.Rows;

namespace PlayniteAchievements.Services.Cache
{
    /// <summary>
    /// Read and write access to the metadata state tables in the achievement cache database:
    /// MetadataApplied, MetadataCoverage, MetadataSourceSnapshots and MetadataSourcePages.
    /// Implemented only by <see cref="CacheManager"/>; callers reach it with an <c>as</c> cast.
    /// </summary>
    /// <remarks>
    /// PlayniteGameId values are <see cref="Guid.ToString()"/> text, the same format the cache
    /// database uses for PlayniteGameId in Games and AchievementOverrides. Exceptions from the
    /// database propagate to the caller, so a failed load is distinguishable from an empty one.
    /// Kept in its own file so the test project, which links individual source files, can
    /// compile the sources that consume this seam.
    /// </remarks>
    internal interface IMetadataStateDatabase
    {
        /// <summary>
        /// Raised after <see cref="ICacheManager.ClearCache"/> has deleted and recreated the
        /// database, which removes every metadata state row.
        /// </summary>
        event EventHandler MetadataStateCleared;

        /// <summary>Loads every MetadataApplied row.</summary>
        List<MetadataAppliedRow> LoadMetadataApplied();

        /// <summary>Loads every MetadataCoverage row.</summary>
        List<MetadataCoverageRow> LoadMetadataCoverage();

        /// <summary>
        /// Upserts the applied row and replaces that game's coverage rows with
        /// <paramref name="coverage"/>, in one transaction.
        /// </summary>
        void SaveMetadataApplied(MetadataAppliedRow applied, IReadOnlyList<MetadataCoverageRow> coverage);

        /// <summary>Deletes a game's applied row and coverage rows.</summary>
        void DeleteMetadataApplied(string playniteGameId);

        /// <summary>Returns the snapshot for a game and source, or null when none is stored.</summary>
        MetadataSourceSnapshotRow GetMetadataSourceSnapshot(string playniteGameId, string sourceKey);

        /// <summary>Loads every snapshot, across all games and sources.</summary>
        List<MetadataSourceSnapshotRow> LoadMetadataSourceSnapshots();

        /// <summary>Upserts a snapshot keyed by its PlayniteGameId and SourceKey.</summary>
        void SaveMetadataSourceSnapshot(MetadataSourceSnapshotRow row);

        /// <summary>Deletes the snapshot for a game and source.</summary>
        void DeleteMetadataSourceSnapshot(string playniteGameId, string sourceKey);

        /// <summary>Loads every page row for one source, across all games.</summary>
        List<MetadataSourcePageRow> LoadMetadataSourcePages(string sourceKey);

        /// <summary>
        /// Replaces a game's page rows for one source with <paramref name="rows"/>, in one
        /// transaction. An empty list only deletes.
        /// </summary>
        void ReplaceMetadataSourcePages(string playniteGameId, string sourceKey, IReadOnlyList<MetadataSourcePageRow> rows);
    }
}
