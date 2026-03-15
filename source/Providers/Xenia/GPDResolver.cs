using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Xenia.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAchievements.Providers.Xenia
{
    internal class GPDResolver
    {
        private readonly string _pluginUserDataPath;
        byte[] gpdFile;
        Int32 gpdIndex;

        public GPDResolver(string pluginUserDataPath)
        {
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
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

        public List<AchievementDetail> LoadGPD(string xeniaAccountPath, string TitleID)
        {
            Int32 freeIndex;
            UInt32 dataIndex;

            XdbfHeader header;
            List<XdbfEntry> entries = new List<XdbfEntry>();
            List<XdbfFileEntry> freeEntries = new List<XdbfFileEntry>();

            gpdFile = File.ReadAllBytes($"{xeniaAccountPath}{TitleID}.gpd");
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

            List<AchievementDetail> achievements = new List<AchievementDetail>();

            foreach (var entry in entries)
            {
                if (entry.section == 2)
                {
                    using (var fs = new FileStream($"{_pluginUserDataPath}\\xenia\\{TitleID}\\{entry.id}.png", FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(entry.data, 0, (Int32)entry.size);
                    }
                }

                if (entry.section == 1)
                {
                    var cheevoindex = 0;

                    XdbfAchievement achievement = new XdbfAchievement();
                    achievement.magic = ReverseEndianness(BitConverter.ToUInt32(entry.data, cheevoindex));
                    cheevoindex += 4;
                    achievement.id = ReverseEndianness(BitConverter.ToUInt32(entry.data, cheevoindex));
                    cheevoindex += 4;
                    achievement.icon_id = ReverseEndianness(BitConverter.ToUInt32(entry.data, cheevoindex));
                    cheevoindex += 4;
                    achievement.gamerscore = ReverseEndianness(BitConverter.ToUInt32(entry.data, cheevoindex));
                    cheevoindex += 4;
                    achievement.flags = ReverseEndianness(BitConverter.ToUInt32(entry.data, cheevoindex));
                    cheevoindex += 4;
                    achievement.unlock_time = ReverseEndianness(BitConverter.ToUInt64(entry.data, cheevoindex));
                    cheevoindex += 8;

                    while (BitConverter.ToUInt16(entry.data, cheevoindex) != 0)
                    {
                        achievement.title += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, cheevoindex))).ToString();
                        cheevoindex += 2;
                    }
                    cheevoindex += 2;

                    while (BitConverter.ToUInt16(entry.data, cheevoindex) != 0)
                    {
                        achievement.unlockDescription += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, cheevoindex))).ToString();
                        cheevoindex += 2;
                    }
                    cheevoindex += 2;

                    while (BitConverter.ToUInt16(entry.data, cheevoindex) != 0)
                    {
                        achievement.description += ((char)ReverseEndianness(BitConverter.ToUInt16(entry.data, cheevoindex))).ToString();
                        cheevoindex += 2;
                    }

                    achievements.Add(new AchievementDetail
                    {
                        ApiName = achievement.id.ToString(),
                        DisplayName = achievement.title,
                        Description = achievement.unlock_time == 0 ? achievement.description : achievement.unlockDescription,
                        UnlockedIconPath = $"{_pluginUserDataPath}\\xenia\\{TitleID}\\{entry.id}.png",
                        LockedIconPath = "https://img.icons8.com/?size=64&id=83187&format=png",
                        Points = (int?)achievement.gamerscore,
                        Unlocked = achievement.unlock_time != 0,
                        UnlockTimeUtc = DateTime.FromFileTime((Int64)achievement.unlock_time),
                    });
                }
            }


            return achievements;
        }
    }
}
