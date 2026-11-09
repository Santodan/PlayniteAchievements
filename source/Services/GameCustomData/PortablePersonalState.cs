using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// The one definition of what in a game's custom data is the user's own progress rather than
    /// curation: unlock states and times, progress counts, and goals. A .pa package never carries
    /// it, so export strips it, import strips it from packages that still carry it, and an import
    /// keeps the local copy for anything the package updates or replaces.
    /// </summary>
    public static class PortablePersonalState
    {
        public static void Strip(CustomAchievementDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            definition.Unlocked = false;
            definition.UnlockTimeUtc = null;
            definition.ProgressNum = null;
        }

        public static void Strip(GameCustomDataPortableFile portable)
        {
            if (portable == null)
            {
                return;
            }

            portable.GoalAchievementApiNames = null;

            if (portable.ManualLink != null)
            {
                portable.ManualLink.UnlockTimes = new Dictionary<string, DateTime?>();
                portable.ManualLink.UnlockStates = new Dictionary<string, bool>();
                portable.ManualLink.CreatedUtc = default;
                portable.ManualLink.LastModifiedUtc = default;
            }

            if (portable.AchievementOverrides != null)
            {
                foreach (var entry in portable.AchievementOverrides.Values.Where(entry => entry != null))
                {
                    entry.UnlockTimeUtc = null;
                    entry.ClearUnlockTime = false;
                }

                portable.AchievementOverrides = GameCustomDataFile.CloneAchievementOverrideMap(portable.AchievementOverrides);
            }

            portable.CustomAchievements?.ForEach(Strip);

            // The linked file mapping is curation and stays. The package type carries no file
            // path or read status by construction, so there is nothing of the user's own to strip.
        }

        /// <summary>
        /// Copies the local achievement's progress onto the incoming definition that replaces it.
        /// </summary>
        public static void CarryLocal(CustomAchievementDefinition local, CustomAchievementDefinition incoming)
        {
            if (local == null || incoming == null)
            {
                return;
            }

            incoming.Unlocked = local.Unlocked;
            incoming.UnlockTimeUtc = local.UnlockTimeUtc;
            incoming.ProgressNum = local.ProgressNum;
        }

        /// <summary>
        /// Copies the current record's progress onto an imported record that is about to replace it.
        /// A manual link's unlocks carry over only while it still points at the same source game.
        /// </summary>
        public static void CarryLocal(GameCustomDataFile current, GameCustomDataFile imported)
        {
            if (current == null || imported == null)
            {
                return;
            }

            imported.GoalAchievementApiNames = current.GoalAchievementApiNames != null
                ? new List<string>(current.GoalAchievementApiNames)
                : null;

            // Nor whether the game takes metadata, which is this machine's choice.
            imported.ExcludedFromMetadata = current.ExcludedFromMetadata;

            // The package never carries the game's sound pack, so the local one stays.
            imported.UnlockSounds = current.UnlockSounds?.Clone();

            // Nor its notification behavior or capture settings, which are this machine's own.
            imported.NotificationBehaviorOverride = current.NotificationBehaviorOverride?.Clone();
            imported.CaptureOverride = current.CaptureOverride?.Clone();

            // A package carries the file mapping but never the file: the path and read status
            // stay with this machine, and a package without a mapping does not remove the link.
            if (current.LinkedFile != null)
            {
                if (imported.LinkedFile == null)
                {
                    imported.LinkedFile = current.LinkedFile.Clone();
                }
                else
                {
                    imported.LinkedFile.FilePath = current.LinkedFile.FilePath;
                    imported.LinkedFile.LastReadUtc = current.LinkedFile.LastReadUtc;
                    imported.LinkedFile.LastReadStatus = current.LinkedFile.LastReadStatus;
                }
            }

            // Rule ownership belongs with the exclusion flags it describes, which stay local.
            imported.RuleClaims = GameCustomDataFile.CloneRuleClaims(current.RuleClaims);
            imported.UserPinnedFields = current.UserPinnedFields != null
                ? new List<string>(current.UserPinnedFields)
                : null;

            if (current.ManualLink != null && imported.ManualLink != null &&
                string.Equals(current.ManualLink.SourceKey, imported.ManualLink.SourceKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.ManualLink.SourceGameId, imported.ManualLink.SourceGameId, StringComparison.OrdinalIgnoreCase))
            {
                var local = current.ManualLink.Clone();
                imported.ManualLink.UnlockTimes = local.UnlockTimes;
                imported.ManualLink.UnlockStates = local.UnlockStates;
                imported.ManualLink.CreatedUtc = local.CreatedUtc;
            }

            if (current.CustomAchievements != null && imported.CustomAchievements != null)
            {
                var localById = current.CustomAchievements
                    .Where(definition => !string.IsNullOrWhiteSpace(CustomAchievementProjectionService.NormalizeId(definition?.Id)))
                    .GroupBy(definition => CustomAchievementProjectionService.NormalizeId(definition.Id), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var definition in imported.CustomAchievements.Where(definition => definition != null))
                {
                    var id = CustomAchievementProjectionService.NormalizeId(definition.Id);
                    if (id != null && localById.TryGetValue(id, out var local))
                    {
                        CarryLocal(local, definition);
                    }
                }
            }

            if (current.AchievementOverrides != null)
            {
                foreach (var pair in current.AchievementOverrides)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null ||
                        (!pair.Value.UnlockTimeUtc.HasValue && !pair.Value.ClearUnlockTime))
                    {
                        continue;
                    }

                    imported.AchievementOverrides = imported.AchievementOverrides ??
                        new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
                    if (!imported.AchievementOverrides.TryGetValue(pair.Key, out var entry) || entry == null)
                    {
                        entry = new AchievementOverride();
                        imported.AchievementOverrides[pair.Key] = entry;
                    }

                    entry.UnlockTimeUtc = pair.Value.UnlockTimeUtc;
                    entry.ClearUnlockTime = pair.Value.ClearUnlockTime;
                }
            }
        }
    }
}
