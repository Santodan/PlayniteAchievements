using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Xenia.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.Xenia
{
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
    }
}
