using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Local
{
    public sealed class NativeSourceLocations
    {
        public string InstallFolders { get; set; } = "";
        public string SaveFolders { get; set; } = "";
        public string DefinitionFolders { get; set; } = "";
        public string EmulatorFolders { get; set; } = "";
        public string SettingsFile { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Profile { get; set; } = "";
        public bool UseDefaultLocations { get; set; } = true;
        public NativeSourceLocations Copy() => (NativeSourceLocations)MemberwiseClone();
        internal static IEnumerable<string> Split(string paths) => (paths ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class NativeSourceReadContext
    {
        internal NativeSourceLocations Locations = new NativeSourceLocations();
        internal string GameName, GameId;
    }
}
