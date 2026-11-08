using System;
using System.IO;
using Newtonsoft.Json;

namespace PlayniteAchievements.Services.Images.Relocation
{
    public enum IconCacheRelocationPhase
    {
        /// <summary>Files are moving; the next launch finishes the move.</summary>
        Moving = 0,

        /// <summary>
        /// Files and stored paths are at the target; kept until the settings holding the new
        /// folder are saved, so a crash before then still ends at the target.
        /// </summary>
        Committed = 1
    }

    /// <summary>The record of a move in progress, in the plugin data folder.</summary>
    public sealed class IconCacheRelocationJournal
    {
        public const string FileName = "icon_cache_relocation.json";

        public IconCacheRelocationPhase Phase { get; set; }

        /// <summary>The cache folder as the plugin named it before the move.</summary>
        public string CurrentRoot { get; set; }

        /// <summary>The folder the files move out of.</summary>
        public string SourceFolder { get; set; }

        public string TargetFolder { get; set; }

        public string LinkToRemove { get; set; }

        public IconCacheMoveStrategy Strategy { get; set; }

        /// <summary>The setting value for the target: empty for the default folder.</summary>
        public string SettingValue { get; set; }

        public static string PathIn(string pluginDataRoot)
        {
            return Path.Combine(pluginDataRoot, FileName);
        }

        /// <summary>The journal in the folder, or null when there is none or it cannot be read.</summary>
        public static IconCacheRelocationJournal TryRead(string pluginDataRoot)
        {
            var path = PathIn(pluginDataRoot);
            try
            {
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<IconCacheRelocationJournal>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        /// <summary>Writes the journal under a temporary name first, so a crash never leaves half of it.</summary>
        public void Write(string pluginDataRoot)
        {
            var path = PathIn(pluginDataRoot);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static void Delete(string pluginDataRoot)
        {
            var path = PathIn(pluginDataRoot);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
