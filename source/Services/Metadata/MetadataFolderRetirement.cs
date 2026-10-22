using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Removes the <c>metadata</c> folder that held metadata state before it moved into the
    /// databases. Only the capstone art records in its <c>links.json</c> are carried over, into the
    /// custom data database: without them the plugin would take art and text it wrote for an auto
    /// capstone for the user's and stop updating it. The rest (applied state, match counts, source
    /// lists, TrueGaming pages) is rebuilt by each game's next refresh.
    /// </summary>
    internal static class MetadataFolderRetirement
    {
        internal const string FolderName = "metadata";

        /// <summary>
        /// Imports the capstone art records and deletes the folder. A <c>links.json</c> that cannot be
        /// read is moved aside first; when the records cannot be saved, the folder is kept for the
        /// next start.
        /// </summary>
        public static void Run(string pluginUserDataPath, GameCustomDataStore customData, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                return;
            }

            var directory = Path.Combine(pluginUserDataPath, FolderName);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var links = Path.Combine(directory, "links.json");
            if (File.Exists(links))
            {
                List<CapstoneArtRecordRow> rows;
                try
                {
                    rows = ReadCapstoneRecords(File.ReadAllText(links));
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"[Metadata] {links} could not be read; it is moved aside and its capstone art records are not carried over.");
                    MoveAside(links, pluginUserDataPath, logger);
                    rows = new List<CapstoneArtRecordRow>();
                }

                if (rows.Count > 0)
                {
                    try
                    {
                        customData.ImportCapstoneArtRecords(rows);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, $"[Metadata] Saving {rows.Count} capstone art records failed; {directory} is kept for the next start.");
                        return;
                    }
                }

                logger?.Info($"[Metadata] Carried {rows.Count} capstone art records over from {links}.");
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                logger?.Info($"[Metadata] Removed {directory}; its state is rebuilt by refreshes.");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Removing {directory} failed.");
            }
        }

        /// <summary>The capstone art records of a <c>links.json</c>, by game and achievement.</summary>
        internal static List<CapstoneArtRecordRow> ReadCapstoneRecords(string json)
        {
            var rows = new List<CapstoneArtRecordRow>();
            if (!(JToken.Parse(json) is JObject root) || !(root["capstoneArt"] is JObject games))
            {
                return rows;
            }

            foreach (var game in games.Properties())
            {
                if (!Guid.TryParse(game.Name, out var gameId) || gameId == Guid.Empty || !(game.Value is JObject records))
                {
                    continue;
                }

                foreach (var entry in records.Properties())
                {
                    var record = entry.Value?.ToObject<CapstoneArtRecord>();
                    if (record != null && !string.IsNullOrWhiteSpace(entry.Name))
                    {
                        rows.Add(MetadataStateStore.ToRow(gameId, entry.Name.Trim(), record));
                    }
                }
            }

            return rows;
        }

        private static void MoveAside(string path, string pluginUserDataPath, ILogger logger)
        {
            try
            {
                File.Move(path, Path.Combine(pluginUserDataPath, $"metadata-links.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}.json"));
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Moving {path} aside failed.");
            }
        }
    }
}
