using System;

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
        public UInt64 unlock_time;
        public string title;
        public string description;
        public string unlockDescription;

        public UInt32 icon_id;
    }
}
