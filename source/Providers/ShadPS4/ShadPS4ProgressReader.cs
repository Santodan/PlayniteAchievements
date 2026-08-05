using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal static class ShadPS4ProgressReader
    {
        private static readonly DateTime Ps4Epoch =
            new DateTime(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const ulong UnixTimestampMaxReasonableSeconds = 4102444800UL;

        public static bool TryRead(
            string path,
            out Dictionary<string, DateTime?> unlockedByApiName)
        {
            unlockedByApiName = new Dictionary<string, DateTime?>(
                StringComparer.OrdinalIgnoreCase);
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
                    FileOptions.SequentialScan))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CloseInput = false
                }))
                {
                    var sawTrophy = false;
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element ||
                            !string.Equals(reader.LocalName, "trophy", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        sawTrophy = true;
                        var id = reader.GetAttribute("id")?.Trim();
                        var unlockState = reader.GetAttribute("unlockstate")?.Trim();
                        if (string.IsNullOrWhiteSpace(id) ||
                            !(string.Equals(unlockState, "true", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(unlockState, "1", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        unlockedByApiName[id] = ParseTimestamp(reader.GetAttribute("timestamp"));
                    }

                    return sawTrophy;
                }
            }
            catch
            {
                unlockedByApiName.Clear();
                return false;
            }
        }

        private static DateTime? ParseTimestamp(string value)
        {
            if (!ulong.TryParse((value ?? string.Empty).Trim(), out var raw) || raw == 0)
            {
                return null;
            }

            try
            {
                if (raw <= UnixTimestampMaxReasonableSeconds)
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)raw).UtcDateTime;
                }

                return Ps4Epoch.AddMilliseconds((long)(raw / 1000UL));
            }
            catch
            {
                return null;
            }
        }
    }
}
