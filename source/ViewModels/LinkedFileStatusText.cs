using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;
using System.Globalization;
using System.IO;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The one wording of a linked file's state, shared by the Editor header, the Overview line
    /// and the Link File window: "progress.csv · Read 4/11/2026 9:12 PM", "mapping only", and so on.
    /// </summary>
    public static class LinkedFileStatusText
    {
        public static string Describe(LinkedFileReadStatus status, Func<string, string> localize)
        {
            switch (status)
            {
                case LinkedFileReadStatus.FileMissing:
                case LinkedFileReadStatus.NoFilePath:
                    return localize("LOCPlayAch_LinkedFile_FileMissing");
                case LinkedFileReadStatus.FileUnstable:
                    return localize("LOCPlayAch_LinkedFile_Status_Unstable");
                case LinkedFileReadStatus.SchemaInvalid:
                    return localize("LOCPlayAch_LinkedFile_Status_MappingIncomplete");
                case LinkedFileReadStatus.NoAchievements:
                    return localize("LOCPlayAch_LinkedFile_Status_NoAchievements");
                case LinkedFileReadStatus.Ok:
                    return null;
                default:
                    return localize("LOCPlayAch_LinkedFile_Status_ReadFailed");
            }
        }

        /// <summary>The file name followed by the link's state.</summary>
        public static string Summarize(LinkedFileLink link, Func<string, string> localize)
        {
            if (link == null)
            {
                return null;
            }

            if (!link.HasFilePath)
            {
                return localize("LOCPlayAch_LinkedFile_MappingOnly");
            }

            string name;
            bool exists;
            try
            {
                name = Path.GetFileName(link.FilePath);
                exists = File.Exists(link.FilePath);
            }
            catch (ArgumentException)
            {
                name = link.FilePath;
                exists = false;
            }

            string state;
            if (!exists)
            {
                state = localize("LOCPlayAch_LinkedFile_FileMissing");
            }
            else if (Enum.TryParse<LinkedFileReadStatus>(link.LastReadStatus, out var status) && status != LinkedFileReadStatus.Ok)
            {
                state = Describe(status, localize);
            }
            else if (link.LastReadUtc.HasValue)
            {
                state = string.Format(
                    localize("LOCPlayAch_LinkedFile_ReadFormat"),
                    link.LastReadUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            }
            else
            {
                state = localize("LOCPlayAch_LinkedFile_NotReadYet");
            }

            return name + " · " + state;
        }
    }
}
