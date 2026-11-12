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
            try
            {
                name = Path.GetFileName(link.FilePath);
            }
            catch (ArgumentException)
            {
                name = link.FilePath;
            }

            return name + " · " + DescribeState(link, localize);
        }

        /// <summary>
        /// The link's state alone: when it was last read, that it was not read yet, or what went wrong.
        /// </summary>
        public static string DescribeState(LinkedFileLink link, Func<string, string> localize)
        {
            if (link == null)
            {
                return null;
            }

            if (!link.HasFilePath)
            {
                return localize("LOCPlayAch_LinkedFile_MappingOnly");
            }

            if (!FileExists(link))
            {
                return localize("LOCPlayAch_LinkedFile_FileMissing");
            }

            if (TryGetFailedStatus(link, out var status))
            {
                return Describe(status, localize);
            }

            if (link.LastReadUtc.HasValue)
            {
                return string.Format(
                    localize("LOCPlayAch_LinkedFile_ReadFormat"),
                    link.LastReadUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            }

            return localize("LOCPlayAch_LinkedFile_NotReadYet");
        }

        /// <summary>Whether the link has no file, its file is missing, or its last read failed.</summary>
        public static bool HasProblem(LinkedFileLink link) =>
            link != null && (!link.HasFilePath || !FileExists(link) || TryGetFailedStatus(link, out _));

        private static bool FileExists(LinkedFileLink link)
        {
            try
            {
                return File.Exists(link.FilePath);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryGetFailedStatus(LinkedFileLink link, out LinkedFileReadStatus status) =>
            Enum.TryParse(link.LastReadStatus, out status) && status != LinkedFileReadStatus.Ok;
    }
}
