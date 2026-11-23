using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Local
{
    public sealed class NativeSourceConfiguration
    {
        public string Source { get; set; } = "auto";
        public string InstallPath { get; set; } = "";
        public string SavePath { get; set; } = "";
        public string SchemaPath { get; set; } = "";
        public string Profile { get; set; } = "";
        public string CollectionGame { get; set; } = "";
        public string UserId { get; set; } = "";
    }

    public sealed class NativeSourceOption
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Files { get; set; }
        public string IconProviderKey
        {
            get
            {
                switch (Key)
                {
                    case "kytyps5": case "ps3recomp": case "ps4recomp":
                    case "sanctuary": case "issn": case "issnfl": case "deadnation": return "PSN";
                    case "x360recomp": case "fh2azimuth": case "xlln": return "Xbox";
                    case "markerpatch": case "madnesspatch": return "EA";
                    case "ladxhd": case "dusklight": case "ghostship": case "dkrr":
                    case "smb1r": case "sms": case "smseclipse":
                    case "smas": case "rr64": case "gdiffuser": case "nectar": case "nectar2": return "RetroAchievements";
                    default: return "Local";
                }
            }
        }
        public string IconColorHex => IconProviderKey == "PSN" ? "#0070D1" : IconProviderKey == "Xbox" ? "#107C10" :
            IconProviderKey == "EA" ? "#E11D48" : IconProviderKey == "RetroAchievements" ? "#FFD700" : "#FF4084D6";
        public string OriginalConsoleIcon
        {
            get
            {
                switch (Key)
                {
                    case "ladxhd": return "gameboycolor";
                    case "dusklight": case "sms": case "smseclipse": case "nectar": case "nectar2": return "gamecube";
                    case "ghostship": case "dkrr": case "rr64": case "gdiffuser": return "n64";
                    case "smb1r": return "nes";
                    default: return null;
                }
            }
        }
        public string OriginalConsoleName => OriginalConsoleIcon == "gameboycolor" ? "Nintendo Game Boy Color" :
            OriginalConsoleIcon == "gamecube" ? "Nintendo GameCube" : OriginalConsoleIcon == "n64" ? "Nintendo 64" :
            OriginalConsoleIcon == "nes" ? "Nintendo Entertainment System" : IconProviderKey == "PSN" ? "PlayStation" : IconProviderKey;
        public override string ToString() => Name;
    }

    public static class NativeSourceCatalog
    {
        public static IReadOnlyList<NativeSourceOption> Sources { get; } = new[]
        {
            Option("auto", "Automatic detection", "Detect from this game's install folder. Select a source and paths below when detection is ambiguous."),
            Option("kytyps5", "KytyPS5", "Kyty root: _Trophies/<title>/trophies_<user>_<service>.json. Schema: game/sce_sys/trophy2/trophyNN.ucp. Select the exact save and UCP if several users or sets exist."),
            Option("ps3recomp", "PS3 Recomp", "Save: gamedata/trophies/NPWRnnnnn.json. Schema: PS3_GAME/TROPDIR/<NPWR>/TROPHY.TRP. A moved gamedata folder needs a save override."),
            Option("ps4recomp", "PS4 Recomp", "Save: trophies.log, last_run.log, unlocked.txt, or trophy/trophyNN.txt. Schema: sce_sys/trophy/trophyNN.trp, extracted TROP.XML, or trophies.tsv. Select one trophy set per Playnite game."),
            Option("x360recomp", "Xbox 360 Recomp / ReXGlue", "Save: achievements/<TITLEID>.toml, Achievements/<TITLEID>/<XUID>.tsv, or SaveData/Achievements.json. Also searches Documents/<game>/achievements. Select a profile file when several exist. Schema: JSON achievement definitions or an extracted trophy/achievement list."),
            Option("xlln", "Games for Windows - LIVE — XLiveLessNess", "Install: xlive.dll and <game>.exe.cfg naming <titleid>. Definitions and icons: the SPAFILE resource inside <game>.exe. Save: XLiveLessNess/profile/title/<TITLEID>/<profile>/achievements.dat beside the game, one folder up, or in %LOCALAPPDATA%; all profiles are merged."),
            Option("markerpatch", "Dead Space 2 — MarkerPatch", "Install: MarkerPatch.ini and achievements/txt/en.txt. Save: %LOCALAPPDATA%/EA Games/Dead Space 2/settings.txt (Controls.AcL.X/Y)."),
            Option("madnesspatch", "Alice — MadnessPatch", "Install: Binaries/Win32/MadnessPatch.ini and Achievements/txt/en.txt. Save: Documents/My Games/Alice Madness Returns/AliceGame/CheckPoint/<profile>/Achievements.txt. Choose the active profile if more than one exists."),
            Option("ladxhd", "Link's Awakening DX HD", "Install: Data/Languages/achieve_eng.lng and Data/Achievements/*.png. Save: %LOCALAPPDATA%/Zelda_LA/achievements, or achievements beside portable.txt."),
            Option("dusklight", "Twilight Princess — Dusklight", "Save: %APPDATA%/TwilitRealm/Dusklight/achievements.json. data_location.json can select portable data/ or a custom folder."),
            Option("ghostship", "Super Mario 64 — Ghostship", "Save: <install>/saves/save_0.json through save_3.json. Select the active slot; slots are never combined."),
            Option("dkrr", "Diddy Kong Racing — DKR-R", "Save: %APPDATA%/DKRPort/achievements/progress-v1.json, or dkr-runtime-data/achievements/progress-v1.json with portable.txt."),
            Option("smb1r", "Super Mario Bros. Remastered", "Save: %APPDATA%/SMB1R/achievements.sav, or config/achievements.sav with portable.txt (a text string of 0/1 flags)."),
            Option("sms", "Super Mario Sunshine — SMS Launcher", "Save: %APPDATA%/sms-launcher/achievements.txt, or SMS-Launcher-Portable/achievements.txt."),
            Option("smseclipse", "Super Mario Eclipse — SMS Launcher", "Save: %APPDATA%/sms-launcher/achievements-eclipse.txt, or SMS-Launcher-Portable/achievements-eclipse.txt. Select this source explicitly for Eclipse."),
            Option("smas", "Super Mario All-Stars — PC port", "Save: <install>/achievements.ini ([unlocked] id=date, [progress] id=count)."),
            Option("rr64", "Road Rash 64 — Recompiled", "Save: %LOCALAPPDATA%/RoadRash64Recompiled/achievements.txt, or achievements.txt beside portable.txt."),
            Option("gdiffuser", "F-Zero X — G-Diffuser", "Save: <install>/saves/achievements.txt (version=1, unlock=<id> <time>)."),
            Option("nectar", "Pikmin — Open Nectar", "Save: <install>/pikmin_achievements.txt."),
            Option("nectar2", "Pikmin 2 — Open Nectar", "Save: <install>/pikmin2_achievements.txt, or pikmin2/pikmin2_achievements.txt."),
            Option("fh2azimuth", "Forza Horizon 2 — Azimuth achievements mod", "Save: <azimuth>/fh2-mods/Achievements/User_<id>.ini; Achievements.txt beside it adds progress. Select the profile when several exist."),
            Option("sanctuary", "Diablo III — SanctuaryPort", "Save: <install>/user/trophies.txt."),
            Option("issn", "inFAMOUS Second Son — issn_pc", "Save: <install>/user/trophies-CUSA00004.txt or trophies-CUSA00223.txt."),
            Option("issnfl", "inFAMOUS First Light — issn_pc", "Save: <install>/user/trophies-CUSA00575.txt, -CUSA00897.txt or -CUSA01064.txt. Select this source explicitly when both games share the launcher."),
            Option("deadnation", "Dead Nation — PS4 Recompiled", "Save: <install>/save/trophies.dat, or the save= folder of paths.ini (16-byte trophy flags).")
        };
        private static NativeSourceOption Option(string key, string name, string files) => new NativeSourceOption { Key = key, Name = name, Files = files };
        public static NativeSourceOption Find(string key) => Sources.FirstOrDefault(s => s.Key == key);

        // Console emulators/recompilers, listed in their own group before the PC ports.
        public static bool IsEmulator(string key) => key == "kytyps5" || key == "ps3recomp" || key == "ps4recomp" || key == "x360recomp";

        // Automatic detection, the emulators as declared, then the PC ports and patches A-Z.
        public static IReadOnlyList<NativeSourceOption> DisplayOrder { get; } =
            Sources.Where(s => s.Key == "auto")
                .Concat(Sources.Where(s => IsEmulator(s.Key)))
                .Concat(Sources.Where(s => s.Key != "auto" && !IsEmulator(s.Key)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                .ToList();
    }
}
