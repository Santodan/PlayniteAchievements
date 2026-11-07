using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Header names for achievement fields, and a first-guess mapping for a newly read file:
    /// columns are matched by header name, each guess checked against the column's cells, and
    /// columns left over are placed by what their cells look like. Pure.
    /// </summary>
    public static class LinkedFileColumnGuesser
    {
        /// <summary>The number of non-blank cells a column is judged by.</summary>
        public const int SampleSize = 50;

        /// <summary>
        /// The header names the achievement CSV reads, normalized. Kept exact so a CSV import
        /// recognizes the same columns it always has.
        /// </summary>
        private static readonly Dictionary<string, LinkedFileField> CoreAliases =
            new Dictionary<string, LinkedFileField>(StringComparer.Ordinal)
            {
                ["id"] = LinkedFileField.ApiName,
                ["customid"] = LinkedFileField.ApiName,
                ["api"] = LinkedFileField.ApiName,
                ["apiname"] = LinkedFileField.ApiName,
                ["name"] = LinkedFileField.DisplayName,
                ["title"] = LinkedFileField.DisplayName,
                ["achievement"] = LinkedFileField.DisplayName,
                ["achievementname"] = LinkedFileField.DisplayName,
                ["displayname"] = LinkedFileField.DisplayName,
                ["description"] = LinkedFileField.Description,
                ["desc"] = LinkedFileField.Description,
                ["details"] = LinkedFileField.Description,
                ["points"] = LinkedFileField.Points,
                ["score"] = LinkedFileField.Points,
                ["gamerscore"] = LinkedFileField.Points,
                ["trophy"] = LinkedFileField.TrophyType,
                ["trophytype"] = LinkedFileField.TrophyType,
                ["hidden"] = LinkedFileField.Hidden,
                ["secret"] = LinkedFileField.Hidden,
                ["rarity"] = LinkedFileField.Rarity,
                ["category"] = LinkedFileField.Category,
                ["categorylabel"] = LinkedFileField.Category,
                ["progress"] = LinkedFileField.ProgressNum,
                ["progressnum"] = LinkedFileField.ProgressNum,
                ["current"] = LinkedFileField.ProgressNum,
                ["progressdenom"] = LinkedFileField.ProgressDenom,
                ["progresstotal"] = LinkedFileField.ProgressDenom,
                ["total"] = LinkedFileField.ProgressDenom,
                ["unlocked"] = LinkedFileField.Unlocked,
                ["earned"] = LinkedFileField.Unlocked,
                ["unlocktime"] = LinkedFileField.UnlockTime,
                ["unlockedat"] = LinkedFileField.UnlockTime,
                ["earnedat"] = LinkedFileField.UnlockTime,
                ["dateunlocked"] = LinkedFileField.UnlockTime,
                ["unlockedicon"] = LinkedFileField.UnlockedIconPath,
                ["lockedicon"] = LinkedFileField.LockedIconPath
            };

        /// <summary>
        /// Further header names a guess accepts, normalized. A name listed under two fields is
        /// tried in the order the fields appear here.
        /// </summary>
        private static readonly (LinkedFileField Field, string[] Aliases)[] ExtendedAliasGroups =
        {
            (LinkedFileField.ApiName, new[]
            {
                "apikey", "key", "nameid", "internalname", "codename", "identifier", "uid", "guid",
                "achid", "achievementid", "trophyid", "cheevoid", "statname", "slug", "code"
            }),
            (LinkedFileField.DisplayName, new[]
            {
                "label", "caption", "heading", "localizedname", "achievementtitle", "trophyname", "cheevo"
            }),
            (LinkedFileField.Description, new[]
            {
                "text", "body", "detail", "summary", "info", "hint", "howto", "requirement", "requirements",
                "condition", "objective", "flavor", "flavortext", "localizeddescription",
                "lockeddescription", "unlockeddescription"
            }),
            (LinkedFileField.Unlocked, new[]
            {
                "achieved", "completed", "complete", "obtained", "got", "gotten", "owned", "done", "state",
                "status", "isunlocked", "isachieved", "isearned", "unlockstate", "achievedstate", "flag", "cleared"
            }),
            (LinkedFileField.UnlockTime, new[]
            {
                "unlockdate", "earnedtime", "earneddate", "achievedat", "achievedtime", "unlocktimestamp",
                "timestamp", "time", "date", "datetime", "when", "unlockedon", "earnedon", "obtainedat",
                "completedat", "completiondate", "achieveddate", "unlockedtime", "unlockeddate"
            }),
            (LinkedFileField.ProgressNum, new[]
            {
                "curprogress", "currentprogress", "count", "value", "amount", "have", "done", "completed",
                "stat", "statvalue", "progressvalue", "achievedcount", "curval", "cur", "progresscurrent"
            }),
            (LinkedFileField.ProgressDenom, new[]
            {
                "max", "maxprogress", "maxvalue", "goal", "target", "required", "needed", "threshold",
                "outof", "limit", "maxval", "progressmax", "steps", "progresstarget", "progressgoal"
            }),
            (LinkedFileField.Points, new[]
            {
                "gs", "value", "reward", "xp", "exp", "worth", "pts", "credits"
            }),
            (LinkedFileField.TrophyType, new[]
            {
                "type", "grade", "tier", "medal", "badge", "level", "quality", "color"
            }),
            (LinkedFileField.Hidden, new[]
            {
                "ishidden", "issecret", "spoiler", "concealed", "invisible", "hide", "private"
            }),
            (LinkedFileField.Rarity, new[]
            {
                "percent", "percentage", "pct", "globalpercent", "unlockpercent", "unlockrate",
                "unlockedpercent", "achievedpercent", "earnedpercent", "completionrate", "rate",
                "ownersrate", "raritypercent", "raritytier", "scarcity"
            }),
            (LinkedFileField.Category, new[]
            {
                "categories", "group", "section", "set", "collection", "chapter", "dlc", "pack", "folder",
                "tag", "tags", "kind"
            }),
            (LinkedFileField.UnlockedIconPath, new[]
            {
                "icon", "image", "img", "picture", "thumbnail", "thumb", "iconurl", "imageurl", "iconpath",
                "iconunlocked", "iconachieved", "coloricon", "badgeicon", "art", "logo"
            }),
            (LinkedFileField.LockedIconPath, new[]
            {
                "icongray", "icongrey", "iconlocked", "lockedimage", "grayicon", "greyicon", "icondisabled",
                "disabledicon", "unachievedicon", "lockedimg", "icongrayscale"
            })
        };

        /// <summary>Leading words a header may carry before a known name, longest first.</summary>
        private static readonly string[] Prefixes = { "achievement", "cheevo", "trophy", "global", "ach", "has", "is" };

        /// <summary>Trailing words a header may carry after a known name, longest first.</summary>
        private static readonly string[] Suffixes = { "string", "local", "value", "text", "path", "file", "utc", "str", "url" };

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tga" };

        private static readonly Dictionary<string, List<LinkedFileField>> AllAliases = BuildAllAliases();

        /// <summary>
        /// Lowercase letters and digits only, so "Unlock Time", "unlock_time" and "unlockTime"
        /// read the same. Null for a header with none.
        /// </summary>
        public static string NormalizeHeader(string header)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                return null;
            }

            var builder = new StringBuilder(header.Length);
            foreach (var c in header)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        /// <summary>The field an achievement CSV header names, or Ignore for an unknown column.</summary>
        public static LinkedFileField ResolveCsvHeader(string header)
        {
            var normalized = NormalizeHeader(header);
            return normalized != null && CoreAliases.TryGetValue(normalized, out var field)
                ? field
                : LinkedFileField.Ignore;
        }

        /// <summary>
        /// The fields a header could name, most likely first: an exact name, then the name with a
        /// common leading or trailing word removed. Empty when nothing fits.
        /// </summary>
        public static IReadOnlyList<LinkedFileField> ResolveHeaderCandidates(string header)
        {
            // Synthetic reader columns: the key or section of a key/value layout names the row.
            if (string.Equals(header, IniTableReader.KeyColumn, StringComparison.Ordinal) ||
                string.Equals(header, IniTableReader.SectionColumn, StringComparison.Ordinal))
            {
                return new[] { LinkedFileField.ApiName };
            }

            if (header != null && header.StartsWith("$", StringComparison.Ordinal))
            {
                return Array.Empty<LinkedFileField>();
            }

            var normalized = NormalizeHeader(header);
            if (normalized == null)
            {
                return Array.Empty<LinkedFileField>();
            }

            foreach (var candidate in Variants(normalized))
            {
                if (AllAliases.TryGetValue(candidate, out var fields))
                {
                    return fields;
                }
            }

            return Array.Empty<LinkedFileField>();
        }

        /// <summary>
        /// A first-guess mapping for a table: each column by its header when its cells fit that
        /// field, then the columns left over by their cells. One column per field; columns that
        /// fit nothing are left out.
        /// </summary>
        public static List<LinkedFileColumnMapping> Guess(LinkedFileTable table)
        {
            var mappings = new List<LinkedFileColumnMapping>();
            if (table == null || table.Columns.Count == 0)
            {
                return mappings;
            }

            var samples = Enumerable.Range(0, table.Columns.Count).Select(index => Sample(table, index)).ToList();
            var fields = new LinkedFileField[table.Columns.Count];
            var claimed = new HashSet<LinkedFileField>();

            // Real headers before synthetic ones, so a named ID column outranks a $key column.
            var headerOrder = Enumerable.Range(0, table.Columns.Count)
                .OrderBy(index => (table.Columns[index] ?? string.Empty).StartsWith("$", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(index => index);
            foreach (var index in headerOrder)
            {
                foreach (var field in ResolveHeaderCandidates(table.Columns[index]))
                {
                    if (!claimed.Contains(field) && CellsFit(field, samples[index]))
                    {
                        fields[index] = field;
                        claimed.Add(field);
                        break;
                    }
                }
            }

            for (var index = 0; index < table.Columns.Count; index++)
            {
                if (fields[index] != LinkedFileField.Ignore || samples[index].Count == 0)
                {
                    continue;
                }

                var field = SniffField(samples[index], claimed);
                if (field != LinkedFileField.Ignore)
                {
                    fields[index] = field;
                    claimed.Add(field);
                }
            }

            for (var index = 0; index < table.Columns.Count; index++)
            {
                if (fields[index] != LinkedFileField.Ignore)
                {
                    mappings.Add(new LinkedFileColumnMapping { Column = table.Columns[index], Field = fields[index] });
                }
            }

            return mappings;
        }

        private static Dictionary<string, List<LinkedFileField>> BuildAllAliases()
        {
            var all = new Dictionary<string, List<LinkedFileField>>(StringComparer.Ordinal);
            void Add(string alias, LinkedFileField field)
            {
                if (!all.TryGetValue(alias, out var fields))
                {
                    fields = new List<LinkedFileField>();
                    all[alias] = fields;
                }

                if (!fields.Contains(field))
                {
                    fields.Add(field);
                }
            }

            foreach (var pair in CoreAliases)
            {
                Add(pair.Key, pair.Value);
            }

            foreach (var group in ExtendedAliasGroups)
            {
                foreach (var alias in group.Aliases)
                {
                    Add(alias, group.Field);
                }
            }

            return all;
        }

        /// <summary>The header as written, then without one leading word, one trailing word, and both.</summary>
        private static IEnumerable<string> Variants(string normalized)
        {
            yield return normalized;

            var withoutPrefix = Prefixes
                .Where(prefix => normalized.Length > prefix.Length && normalized.StartsWith(prefix, StringComparison.Ordinal))
                .Select(prefix => normalized.Substring(prefix.Length))
                .ToList();
            foreach (var variant in withoutPrefix)
            {
                yield return variant;
            }

            var bases = new List<string> { normalized };
            bases.AddRange(withoutPrefix);
            foreach (var text in bases)
            {
                foreach (var suffix in Suffixes)
                {
                    if (text.Length > suffix.Length && text.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        yield return text.Substring(0, text.Length - suffix.Length);
                    }
                }
            }
        }

        private static List<string> Sample(LinkedFileTable table, int columnIndex)
        {
            var sample = new List<string>();
            for (var row = 0; row < table.Rows.Count && sample.Count < SampleSize; row++)
            {
                var cell = table.Cell(row, columnIndex)?.Trim();
                if (!string.IsNullOrEmpty(cell))
                {
                    sample.Add(cell);
                }
            }

            return sample;
        }

        /// <summary>Whether every sampled cell reads as <paramref name="field"/>. An empty column fits anything.</summary>
        private static bool CellsFit(LinkedFileField field, IReadOnlyList<string> cells)
        {
            switch (field)
            {
                case LinkedFileField.Unlocked:
                case LinkedFileField.Hidden:
                    return cells.All(cell => LinkedFileValueParser.TryParseBoolean(cell, null, out _));
                case LinkedFileField.UnlockTime:
                    return cells.All(cell => LinkedFileValueParser.TryParseUnlockTimeUtc(cell, null, out _));
                case LinkedFileField.ProgressNum:
                case LinkedFileField.Points:
                    return cells.All(cell => LinkedFileValueParser.TryParseInt(cell, out var value) && value >= 0);
                case LinkedFileField.ProgressDenom:
                    return cells.All(cell => LinkedFileValueParser.TryParseInt(cell, out var value) && value > 0);
                case LinkedFileField.TrophyType:
                    return cells.All(cell => CustomAchievementTextImportService.NormalizeTrophyType(cell) != null);
                case LinkedFileField.Rarity:
                    return cells.All(cell => CustomAchievementTextImportService.TryParseRarity(cell, out _, out _));
                case LinkedFileField.UnlockedIconPath:
                case LinkedFileField.LockedIconPath:
                    return cells.All(LooksLikeIcon);
                default:
                    return true;
            }
        }

        /// <summary>
        /// The field a column's cells clearly hold, among fields no column has claimed. Each rule
        /// needs every sampled cell to qualify, and is stricter than the parser, so a column of
        /// counters or free text is not mistaken for a state or a time.
        /// </summary>
        private static LinkedFileField SniffField(IReadOnlyList<string> cells, HashSet<LinkedFileField> claimed)
        {
            bool Free(LinkedFileField field) => !claimed.Contains(field);

            if (Free(LinkedFileField.TrophyType) && CellsFit(LinkedFileField.TrophyType, cells))
            {
                return LinkedFileField.TrophyType;
            }

            if (Free(LinkedFileField.Rarity) &&
                cells.All(cell => cell.EndsWith("%", StringComparison.Ordinal)) &&
                CellsFit(LinkedFileField.Rarity, cells))
            {
                return LinkedFileField.Rarity;
            }

            if (Free(LinkedFileField.Unlocked) && LooksLikeUnlockState(cells))
            {
                return LinkedFileField.Unlocked;
            }

            if (Free(LinkedFileField.UnlockTime) && LooksLikeTimes(cells))
            {
                return LinkedFileField.UnlockTime;
            }

            if (cells.All(LooksLikeIcon))
            {
                if (Free(LinkedFileField.UnlockedIconPath))
                {
                    return LinkedFileField.UnlockedIconPath;
                }

                if (Free(LinkedFileField.LockedIconPath))
                {
                    return LinkedFileField.LockedIconPath;
                }

                return LinkedFileField.Ignore;
            }

            var distinct = cells.Distinct(StringComparer.OrdinalIgnoreCase).Count() == cells.Count;
            var numeric = cells.All(cell => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (!distinct || numeric)
            {
                return LinkedFileField.Ignore;
            }

            var noNameColumn = Free(LinkedFileField.ApiName) && Free(LinkedFileField.DisplayName);
            if (noNameColumn && cells.All(cell => !cell.Any(char.IsWhiteSpace)))
            {
                return LinkedFileField.ApiName;
            }

            if (Free(LinkedFileField.DisplayName) && cells.Count(cell => cell.Any(char.IsWhiteSpace)) * 2 >= cells.Count)
            {
                return LinkedFileField.DisplayName;
            }

            return LinkedFileField.Ignore;
        }

        /// <summary>
        /// Words that mean unlocked or locked, or 0 and 1. A column of a single number is a
        /// counter as often as a state, so numbers need both values present.
        /// </summary>
        private static bool LooksLikeUnlockState(IReadOnlyList<string> cells)
        {
            var known = cells.All(cell =>
                LinkedFileSchema.DefaultTrueValues.Contains(cell.ToLowerInvariant()) ||
                LinkedFileSchema.DefaultFalseValues.Contains(cell.ToLowerInvariant()));
            if (!known)
            {
                return false;
            }

            var hasWord = cells.Any(cell => !char.IsDigit(cell[0]));
            return hasWord || cells.Distinct(StringComparer.Ordinal).Count() > 1;
        }

        /// <summary>
        /// Dates written out, or epoch stamps from 2001 on (with zero for never). Small numbers
        /// are counts, not times.
        /// </summary>
        private static bool LooksLikeTimes(IReadOnlyList<string> cells)
        {
            var anyTime = false;
            foreach (var cell in cells)
            {
                if (long.TryParse(cell, NumberStyles.None, CultureInfo.InvariantCulture, out var stamp))
                {
                    if (stamp == 0)
                    {
                        continue;
                    }

                    if (stamp < 1000000000L)
                    {
                        return false;
                    }

                    anyTime = true;
                    continue;
                }

                if (!cell.Any(char.IsDigit) ||
                    cell.IndexOfAny(new[] { '-', '/', ':', '.' }) < 0 ||
                    !LinkedFileValueParser.TryParseUnlockTimeUtc(cell, null, out _))
                {
                    return false;
                }

                anyTime = true;
            }

            return anyTime;
        }

        private static bool LooksLikeIcon(string cell)
        {
            if (cell.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                cell.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(cell);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
    }
}
