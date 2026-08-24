using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Xenia.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.Xenia
{
    internal sealed class XeniaAchievementProgress
    {
        public uint Id { get; set; }
        public bool Unlocked { get; set; }
        public ulong UnlockTime { get; set; }
    }

    internal class GPDResolver
    {
        byte[] gpdFile;
        Int32 gpdIndex;

        public GPDResolver()
        {
        }

        UInt16 ReverseEndianness(UInt16 value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return BitConverter.ToUInt16(bytes, 0);
        }
        UInt32 ReverseEndianness(UInt32 value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }
        UInt64 ReverseEndianness(UInt64 value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        // GPD file helper functions
        UInt16 ReadUInt16()
        {
            gpdIndex += 2;
            return ReverseEndianness(BitConverter.ToUInt16(gpdFile, gpdIndex - 2));
        }
        UInt32 ReadUInt32()
        {
            gpdIndex += 4;
            return ReverseEndianness(BitConverter.ToUInt32(gpdFile, gpdIndex - 4));
        }
        UInt64 ReadUInt64()
        {
            gpdIndex += 8;
            return ReverseEndianness(BitConverter.ToUInt64(gpdFile, gpdIndex - 8));
        }

        public GPDFile LoadGPD(string path)
        {
            GPDFile file = new GPDFile(); 

            Int32 freeIndex;
            UInt32 dataIndex;

            XdbfHeader header;
            List<XdbfEntry> entries = new List<XdbfEntry>();
            List<XdbfFileEntry> freeEntries = new List<XdbfFileEntry>();

            gpdFile = File.ReadAllBytes(path);
            gpdIndex = 0;

            header = new XdbfHeader();
            header.magic = ReadUInt32();
            header.version = ReadUInt32();
            header.entry_count = ReadUInt32();
            header.entry_used = ReadUInt32();
            header.free_count = ReadUInt32();
            header.free_used = ReadUInt32();

            //Index to start of data
            freeIndex = gpdIndex + (18 * (Int32)header.free_count);
            dataIndex = (UInt32)freeIndex + (8 * header.free_count);

            //Load Data Entries
            for (var i = 0; i < header.entry_used; i++)
            {
                XdbfEntry entry = new XdbfEntry();
                entry.section = ReadUInt16();
                entry.id = ReadUInt64();
                entry.offset = ReadUInt32();
                entry.size = ReadUInt32();

                entry.data = new byte[entry.size];
                Array.Copy(gpdFile, dataIndex + entry.offset, entry.data, 0, entry.size);

                entries.Add(entry);
            }

            //Load Free Entries
            for (var i = 0; i < header.free_used; i++)
            {
                XdbfFileEntry entry = new XdbfFileEntry();

                entry.offset = ReverseEndianness(BitConverter.ToUInt32(gpdFile, freeIndex));
                entry.size = ReverseEndianness(BitConverter.ToUInt32(gpdFile, freeIndex + 4));

                freeIndex += 8;

                freeEntries.Add(entry);
            }

            foreach (var entry in entries)
            {
                var index = 0;

                switch (entry.section)
                {
                    case 1: // Achievement Data

                        // Some "Achievement" entries from a real xbox 360 gpd have too little data (Probably not real achievements)
                        // and can cause a crash when reading the data in
                        if (entry.size < 28)
                            break;

                        XdbfAchievement achievement = new XdbfAchievement();
                        achievement.magic = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        achievement.id = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        achievement.icon_id = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        achievement.gamerscore = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        achievement.flags = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;

                        // This check is really just for gpd files that come from an actual xbox 360
                        // Check to see if achievements have been synced to file
                        if (achievement.flags == 0)
                            break;

                        // This check is also for achievements from an actual xbox 360 as xenia will always put the date in the file
                        // Check to see if achievement earned flag has been set
                        achievement.earned = false;
                        if ((achievement.flags & 131072) == 131072)
                        {
                            achievement.earned = true;
                        }

                        achievement.unlock_time = ReverseEndianness(BitConverter.ToUInt64(entry.data, index));
                        index += 8;

                        while (BitConverter.ToUInt16(entry.data, index) != 0)
                        {
                            achievement.title += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, index))).ToString();
                            index += 2;
                        }
                        index += 2;

                        while (BitConverter.ToUInt16(entry.data, index) != 0)
                        {
                            achievement.unlockDescription += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, index))).ToString();
                            index += 2;
                        }
                        index += 2;

                        while (BitConverter.ToUInt16(entry.data, index) != 0)
                        {
                            achievement.description += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, index))).ToString();
                            index += 2;
                        }
                        file.Achievements.Add(achievement);
                        break;

                    case 2: // Icon data

                        file.IconData.Add(new KeyValuePair<Int32, byte[]>((Int32)entry.id, entry.data));
                        break;

                    case 3: // Settings data
                        file.Settings.contentID = BitConverter.GetBytes(ReverseEndianness(BitConverter.ToUInt64(entry.data, index)));
                        index += 8;
                        file.Settings.settingID = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        file.Settings.data = new byte[entry.data.Length - index];
                        Array.Copy(entry.data, index, file.Settings.data, 0, entry.data.Length - index);
                        break;

                    case 4: // Title data
                        XdbfTitle title = new XdbfTitle();
                        title.id = ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.achievement_count = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.achievement_unlocked_count = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.gamerscore_total = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.gamerscore_unlocked = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.unknown1 = (Int64)ReverseEndianness(BitConverter.ToUInt64(entry.data, index));
                        index += 8;
                        title.unknown2 = (Int32)ReverseEndianness(BitConverter.ToUInt32(entry.data, index));
                        index += 4;
                        title.last_played = (Int64)ReverseEndianness(BitConverter.ToUInt64(entry.data, index));
                        index += 8;

                        title.title = Encoding.UTF8.GetString(entry.data, index, entry.data.Length - index);
                        file.Titles.Add(title);
                        break;

                    case 5: // String data
                        file.StringData = Encoding.UTF8.GetString(entry.data);
                        break;

                    case 6: // Achievement security
                        break;

                    default:
                        break;
                }
            }

            return file;
        }

        /// <summary>
        /// Reads only section-1 progress records from an XDBF/GPD. No strings, settings, icons,
        /// or title metadata are materialized.
        /// </summary>
        internal static bool TryLoadAchievementProgress(
            string path,
            out List<XeniaAchievementProgress> achievements)
        {
            achievements = new List<XeniaAchievementProgress>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.RandomAccess))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 24)
                    {
                        return false;
                    }

                    var magic = ReadUInt32BigEndian(reader);
                    if (magic != 0x58444246U)
                    {
                        return false;
                    }

                    ReadUInt32BigEndian(reader); // version
                    var entryCapacity = ReadUInt32BigEndian(reader);
                    var entryUsed = ReadUInt32BigEndian(reader);
                    var freeCapacity = ReadUInt32BigEndian(reader);
                    ReadUInt32BigEndian(reader); // free used

                    if (entryCapacity > 1_000_000 ||
                        entryUsed > entryCapacity ||
                        freeCapacity > 1_000_000)
                    {
                        return false;
                    }

                    var dataOffset = checked(
                        24L +
                        (18L * entryCapacity) +
                        (8L * freeCapacity));
                    if (dataOffset < 24 || dataOffset > stream.Length)
                    {
                        return false;
                    }

                    for (var index = 0U; index < entryUsed; index++)
                    {
                        stream.Position = 24L + (18L * index);
                        var section = ReadUInt16BigEndian(reader);
                        ReadUInt64BigEndian(reader); // entry id
                        var offset = ReadUInt32BigEndian(reader);
                        var size = ReadUInt32BigEndian(reader);
                        if (section != 1 || size < 28)
                        {
                            continue;
                        }

                        var absoluteOffset = checked(dataOffset + offset);
                        if (absoluteOffset < dataOffset ||
                            absoluteOffset > stream.Length ||
                            size > stream.Length - absoluteOffset)
                        {
                            return false;
                        }

                        stream.Position = absoluteOffset;
                        ReadUInt32BigEndian(reader); // achievement magic
                        var achievementId = ReadUInt32BigEndian(reader);
                        ReadUInt32BigEndian(reader); // icon id
                        ReadUInt32BigEndian(reader); // gamerscore
                        var flags = ReadUInt32BigEndian(reader);
                        var unlockTime = ReadUInt64BigEndian(reader);
                        if (flags == 0)
                        {
                            continue;
                        }

                        achievements.Add(new XeniaAchievementProgress
                        {
                            Id = achievementId,
                            Unlocked = (flags & 131072U) == 131072U,
                            UnlockTime = unlockTime
                        });
                    }

                    return true;
                }
            }
            catch
            {
                achievements.Clear();
                return false;
            }
        }

        /// <summary>
        /// Reads only the section-5 string entry (the game title) from an XDBF/GPD.
        /// No achievements, settings, icons, or title metadata are materialized.
        /// </summary>
        internal static bool TryReadTitleString(string path, out string title)
        {
            title = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.RandomAccess))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 24)
                    {
                        return false;
                    }

                    var magic = ReadUInt32BigEndian(reader);
                    if (magic != 0x58444246U)
                    {
                        return false;
                    }

                    ReadUInt32BigEndian(reader); // version
                    var entryCapacity = ReadUInt32BigEndian(reader);
                    var entryUsed = ReadUInt32BigEndian(reader);
                    var freeCapacity = ReadUInt32BigEndian(reader);
                    ReadUInt32BigEndian(reader); // free used

                    if (entryCapacity > 1_000_000 ||
                        entryUsed > entryCapacity ||
                        freeCapacity > 1_000_000)
                    {
                        return false;
                    }

                    var dataOffset = checked(
                        24L +
                        (18L * entryCapacity) +
                        (8L * freeCapacity));
                    if (dataOffset < 24 || dataOffset > stream.Length)
                    {
                        return false;
                    }

                    for (var index = 0U; index < entryUsed; index++)
                    {
                        stream.Position = 24L + (18L * index);
                        var section = ReadUInt16BigEndian(reader);
                        ReadUInt64BigEndian(reader); // entry id
                        var offset = ReadUInt32BigEndian(reader);
                        var size = ReadUInt32BigEndian(reader);
                        if (section != 5)
                        {
                            continue;
                        }

                        var absoluteOffset = checked(dataOffset + offset);
                        if (absoluteOffset < dataOffset ||
                            absoluteOffset > stream.Length ||
                            size > stream.Length - absoluteOffset)
                        {
                            return false;
                        }

                        stream.Position = absoluteOffset;
                        var data = reader.ReadBytes((int)size);
                        if (data.Length != size)
                        {
                            return false;
                        }

                        title = Encoding.UTF8.GetString(data);
                        return true;
                    }

                    return false;
                }
            }
            catch
            {
                title = null;
                return false;
            }
        }

        private static ushort ReadUInt16BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            if (bytes.Length != 2)
            {
                throw new EndOfStreamException();
            }

            return (ushort)((bytes[0] << 8) | bytes[1]);
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
            {
                throw new EndOfStreamException();
            }

            return ((uint)bytes[0] << 24) |
                   ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) |
                   bytes[3];
        }

        private static ulong ReadUInt64BigEndian(BinaryReader reader)
        {
            var high = ReadUInt32BigEndian(reader);
            var low = ReadUInt32BigEndian(reader);
            return ((ulong)high << 32) | low;
        }
    }
}
