using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Brings icons named by a linked file into the game's icon folder, the way the Editor does
    /// on save: a local file is copied there and the copy is stored; a web address is stored as
    /// written and its image downloaded there. A copy takes its source's write time, so a later
    /// read can tell the stored copy still holds the file's icon.
    /// </summary>
    internal sealed class LinkedFileIcons
    {
        private readonly ManagedCustomIconService _icons;
        private readonly ILogger _logger;

        public LinkedFileIcons(ManagedCustomIconService icons, ILogger logger)
        {
            _icons = icons ?? throw new ArgumentNullException(nameof(icons));
            _logger = logger;
        }

        /// <summary>Whether <paramref name="stored"/> is this game's copy of the local file <paramref name="source"/>.</summary>
        public bool IsSameIcon(Guid gameId, string stored, string source)
        {
            if (IsHttpUrl(source) || !_icons.IsManagedCustomIconPath(stored, gameId.ToString("D")))
            {
                return false;
            }

            try
            {
                var copy = new FileInfo(stored);
                var original = new FileInfo(source);
                return copy.Exists && original.Exists && copy.LastWriteTimeUtc == original.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Brings in every icon of <paramref name="definitions"/> that differs from <paramref name="previous"/>.</summary>
        public void Materialize(
            Guid gameId,
            IReadOnlyList<CustomAchievementDefinition> previous,
            IReadOnlyList<CustomAchievementDefinition> definitions)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            var gameIdText = gameId.ToString("D");
            var before = new Dictionary<string, CustomAchievementDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in previous ?? Array.Empty<CustomAchievementDefinition>())
            {
                if (!string.IsNullOrWhiteSpace(definition?.Id) && !before.ContainsKey(definition.Id))
                {
                    before[definition.Id] = definition;
                }
            }

            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                definitions.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id)));

            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition?.Id) ||
                    !fileStems.TryGetValue(CustomAchievementProjectionService.BuildApiName(definition.Id), out var fileStem) ||
                    string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                before.TryGetValue(definition.Id, out var prior);
                if (!string.Equals(prior?.UnlockedIconPath, definition.UnlockedIconPath, StringComparison.OrdinalIgnoreCase))
                {
                    definition.UnlockedIconPath = MaterializeOne(definition.UnlockedIconPath, gameIdText, fileStem, AchievementIconVariant.Unlocked);
                }

                if (!string.Equals(prior?.LockedIconPath, definition.LockedIconPath, StringComparison.OrdinalIgnoreCase))
                {
                    definition.LockedIconPath = MaterializeOne(definition.LockedIconPath, gameIdText, fileStem, AchievementIconVariant.Locked);
                }
            }
        }

        private string MaterializeOne(string value, string gameIdText, string fileStem, AchievementIconVariant variant)
        {
            if (string.IsNullOrWhiteSpace(value) || _icons.IsManagedCustomIconPath(value, gameIdText))
            {
                return value;
            }

            var isUrl = IsHttpUrl(value);
            if (!isUrl && !File.Exists(value))
            {
                return value;
            }

            string copy = null;
            try
            {
                copy = _icons
                    .MaterializeCustomIconAsync(value, gameIdText, fileStem, variant, CancellationToken.None, overwriteExistingTarget: true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[LinkedFile] Failed to bring in icon '{value}' for game {gameIdText}.");
            }

            if (isUrl)
            {
                return value;
            }

            if (string.IsNullOrWhiteSpace(copy) || !File.Exists(copy))
            {
                _logger?.Warn($"[LinkedFile] Icon '{value}' for game {gameIdText} was not copied; the file's path is kept.");
                return value;
            }

            try
            {
                File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(value));
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[LinkedFile] Could not match the write time of '{copy}': {ex.Message}");
            }

            return copy;
        }

        private static bool IsHttpUrl(string value)
        {
            return value != null &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
