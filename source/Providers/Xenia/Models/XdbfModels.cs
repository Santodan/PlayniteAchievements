using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Xenia.Models
{
    struct XdbfHeader
    {
        public UInt32 magic;
        public UInt32 version;
        public UInt32 entry_count;
        public UInt32 entry_used;
        public UInt32 free_count;
        public UInt32 free_used;
    }

    struct XdbfEntry
    {
        public UInt16 section;
        public UInt64 id;
        public UInt32 offset;
        public UInt32 size;

        public byte[] data;
    }

    struct XdbfFileEntry
    {
        public UInt32 offset;
        public UInt32 size;
    }

    struct XdbfAchievement
    {
        public UInt32 magic;
        public UInt32 id;
        public UInt32 gamerscore;
        public UInt32 flags;
        public bool earned;
        public UInt64 unlock_time;
        public string title;
        public string description;
        public string unlockDescription;

        public UInt32 icon_id;
    }
    struct XdbfTitle
    {
        public UInt32 id;
        public Int32 achievement_count;
        public Int32 achievement_unlocked_count;
        public Int32 gamerscore_total;
        public Int32 gamerscore_unlocked;
        public Int64 unknown1;
        public Int32 unknown2;
        public Int64 last_played;
        public string title;
    }
    struct XdbfSettings
    {
        public byte[] contentID; // 8 bytes
        public Int32 settingID;
        public byte[] data;
    }

    struct GPDFile
    {
        public GPDFile()
        {
            Achievements = new List<XdbfAchievement>();
            IconData = new List<KeyValuePair<int, byte[]>>();
            Settings = new XdbfSettings();
            Titles = new List<XdbfTitle>();
            StringData = "";
        }

        public List<XdbfAchievement> Achievements;
        public List<KeyValuePair<int, byte[]>> IconData;
        public XdbfSettings Settings;
        public List<XdbfTitle> Titles;
        public string StringData;
    }

}
