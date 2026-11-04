using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>The metadata state tables in memory, as the cache database would keep them.</summary>
    internal sealed class FakeMetadataStateDatabase : IMetadataStateDatabase
    {
        private readonly Dictionary<string, MetadataAppliedRow> _applied =
            new Dictionary<string, MetadataAppliedRow>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MetadataCoverageRow> _coverage = new List<MetadataCoverageRow>();
        private readonly Dictionary<string, MetadataSourceSnapshotRow> _snapshots =
            new Dictionary<string, MetadataSourceSnapshotRow>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MetadataSourcePageRow> _pages = new List<MetadataSourcePageRow>();

        public event EventHandler MetadataStateCleared;

        public int SnapshotWrites { get; private set; }

        public int PageWrites { get; private set; }

        public List<MetadataAppliedRow> LoadMetadataApplied() => _applied.Values.ToList();

        public List<MetadataCoverageRow> LoadMetadataCoverage() => _coverage.ToList();

        public void SaveMetadataApplied(MetadataAppliedRow applied, IReadOnlyList<MetadataCoverageRow> coverage)
        {
            _applied[applied.PlayniteGameId] = applied;
            _coverage.RemoveAll(row => row.PlayniteGameId == applied.PlayniteGameId);
            _coverage.AddRange(coverage ?? Array.Empty<MetadataCoverageRow>());
        }

        public void DeleteMetadataApplied(string playniteGameId)
        {
            _applied.Remove(playniteGameId);
            _coverage.RemoveAll(row => row.PlayniteGameId == playniteGameId);
        }

        public MetadataSourceSnapshotRow GetMetadataSourceSnapshot(string playniteGameId, string sourceKey) =>
            _snapshots.TryGetValue(playniteGameId + "|" + sourceKey, out var row) ? row : null;

        public List<MetadataSourceSnapshotRow> LoadMetadataSourceSnapshots() => _snapshots.Values.ToList();

        public void SaveMetadataSourceSnapshot(MetadataSourceSnapshotRow row)
        {
            SnapshotWrites++;
            _snapshots[row.PlayniteGameId + "|" + row.SourceKey] = row;
        }

        public void DeleteMetadataSourceSnapshot(string playniteGameId, string sourceKey) =>
            _snapshots.Remove(playniteGameId + "|" + sourceKey);

        public List<MetadataSourcePageRow> LoadMetadataSourcePages(string sourceKey) =>
            _pages.Where(row => string.Equals(row.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase)).ToList();

        public void ReplaceMetadataSourcePages(string playniteGameId, string sourceKey, IReadOnlyList<MetadataSourcePageRow> rows)
        {
            PageWrites++;
            _pages.RemoveAll(row => row.PlayniteGameId == playniteGameId &&
                                    string.Equals(row.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase));
            _pages.AddRange(rows ?? Array.Empty<MetadataSourcePageRow>());
        }

        /// <summary>Empties every table and raises the event, as Clear Cache does.</summary>
        public void Clear()
        {
            _applied.Clear();
            _coverage.Clear();
            _snapshots.Clear();
            _pages.Clear();
            MetadataStateCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Link and state stores wired as the plugin wires them, over a test directory and fake tables.</summary>
    internal static class MetadataTestStores
    {
        public static MetadataStateStore State(string directory, FakeMetadataStateDatabase database)
        {
            var state = new MetadataStateStore(null);
            state.AttachCustomData(new GameCustomDataStore(directory));
            state.AttachDatabase(database);
            return state;
        }

        public static MetadataLinkStore Links(string directory, FakeMetadataStateDatabase database) =>
            new MetadataLinkStore(new GameCustomDataStore(directory), State(directory, database), null);
    }
}
