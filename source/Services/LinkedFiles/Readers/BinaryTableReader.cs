using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// A binary save file as a table, with no layout settings. Hex or base64 text is decoded
    /// first; the bytes are then tried against a few record layouts in order, and the first one
    /// that accounts for the file exactly is used. Nothing is guessed: when no layout fits, the
    /// read fails. Numbers are little-endian. Value columns are named by their byte offset in the
    /// record, so a mapping keeps working as the values in the file change.
    /// <list type="bullet">
    /// <item>Flag text: a text file of single digits drawn from at most three values, one per
    /// achievement in order, such as <c>1001</c>.</item>
    /// <item>Named records: a length-prefixed text name per record, the same number of bytes
    /// between every pair of names, and the last record ending at the end of the file.</item>
    /// <item>Counted records: a record count followed by exactly that many equal-size records
    /// to the end of the file, each starting with a distinct id.</item>
    /// <item>Fixed slots: equal-size records after a header, where every filled record holds a
    /// Unix time no later than today at the same position plus at least one zero byte, and
    /// empty records are all zero.</item>
    /// </list>
    /// </summary>
    internal static class BinaryTableReader
    {
        public const string NameColumn = "name";
        public const string IdColumn = "id";
        public const string TimeColumn = "time";
        public const string IndexColumn = "index";
        public const string ValueColumn = "value";

        private const int MinNameLength = 2;
        private const int MaxNameLength = 256;
        private const int MinRecords = 2;
        private const int MaxRecords = 100000;
        private const int MinStride = 4;
        private const int MaxStride = 64;
        private const int MaxHeader = 512;
        private const int MaxSlotFileBytes = 256 * 1024;
        private const int MinEncodedLength = 16;

        // 2000-01-01 in Unix seconds; a slot time must also not be later than tomorrow.
        private const uint EarliestTime = 946684800U;

        // 2000-01-01 and 2100-01-01 as Windows file times.
        private const ulong EarliestFileTime = 125911584000000000UL;
        private const ulong LatestFileTime = 157469184000000000UL;

        public static LinkedFileReadResult Read(byte[] bytes)
        {
            var flags = TryFlagText(bytes);
            if (flags != null)
            {
                return LinkedFileReadResult.Ok(flags);
            }

            var data = DecodeText(bytes) ?? bytes ?? new byte[0];
            var table = TryNamedRecords(data) ?? TryCountedRecords(data) ?? TryFixedSlots(data);
            return table != null
                ? LinkedFileReadResult.Ok(table)
                : LinkedFileReadResult.Failed(LinkedFileReadStatus.ReadFailed, "No supported binary layout was recognized.");
        }

        /// <summary>Whether the bytes are text this reader decodes: flag digits, hex or base64.</summary>
        public static bool IsEncodedText(byte[] bytes)
        {
            return TryFlagText(bytes) != null || DecodeText(bytes) != null;
        }

        /// <summary>
        /// One row per digit with its zero-based position, when the file is nothing but single
        /// digits (whitespace ignored) taking at most three distinct values; null otherwise.
        /// </summary>
        private static LinkedFileTable TryFlagText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxRecords * 2)
            {
                return null;
            }

            var digits = new List<char>();
            foreach (var b in bytes)
            {
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n')
                {
                    continue;
                }

                if (b < (byte)'0' || b > (byte)'9')
                {
                    return null;
                }

                digits.Add((char)b);
            }

            if (digits.Count == 0 || digits.Distinct().Count() > 3)
            {
                return null;
            }

            return LinkedFileTable.Build(
                new[] { IndexColumn, ValueColumn },
                digits.Select((digit, index) => new[] { index.ToString(CultureInfo.InvariantCulture), digit.ToString() }));
        }

        /// <summary>
        /// The bytes a hex or base64 text file encodes, or null when the file is not entirely one
        /// of those as one unbroken run (surrounding whitespace is ignored).
        /// </summary>
        internal static byte[] DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            // Surrounding whitespace is allowed; whitespace inside means the text is not one
            // encoded run (a list of numbers, say).
            var first = 0;
            var last = bytes.Length - 1;
            while (first <= last && IsSpace(bytes[first]))
            {
                first++;
            }

            while (last >= first && IsSpace(bytes[last]))
            {
                last--;
            }

            var count = last - first + 1;
            var chars = new char[Math.Max(count, 0)];
            for (var i = 0; i < count; i++)
            {
                var b = bytes[first + i];
                if (b < 0x21 || b > 0x7E)
                {
                    return null;
                }

                chars[i] = (char)b;
            }

            if (count < MinEncodedLength)
            {
                return null;
            }

            var text = new string(chars, 0, count);
            if (count % 2 == 0 && text.All(Uri.IsHexDigit))
            {
                var decoded = new byte[count / 2];
                for (var i = 0; i < decoded.Length; i++)
                {
                    decoded[i] = (byte)((HexValue(text[i * 2]) << 4) | HexValue(text[i * 2 + 1]));
                }

                return decoded;
            }

            if (count % 4 == 0 && text.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
            {
                try
                {
                    return Convert.FromBase64String(text);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        private static LinkedFileTable TryNamedRecords(byte[] data)
        {
            var names = new List<(int Prefix, int Start, int Length)>();
            var position = 0;
            while (position + 4 <= data.Length)
            {
                var length = U32(data, position);
                if (length >= MinNameLength && length <= MaxNameLength &&
                    position + 4 + (long)length <= data.Length &&
                    IsName(data, position + 4, (int)length))
                {
                    names.Add((position, position + 4, (int)length));
                    position += 4 + (int)length;
                    continue;
                }

                position++;
            }

            if (names.Count < MinRecords || names.Count > MaxRecords)
            {
                return null;
            }

            // Every record has the same bytes between one name and the next record's length.
            var gap = names[1].Prefix - (names[0].Start + names[0].Length);
            for (var i = 1; i < names.Count - 1; i++)
            {
                if (names[i + 1].Prefix - (names[i].Start + names[i].Length) != gap)
                {
                    return null;
                }
            }

            // The last record's value block runs to the end of the file; whatever of the gap it
            // does not cover is a fixed tag in front of each length, identical in every record.
            var last = names[names.Count - 1];
            var valueSize = data.Length - (last.Start + last.Length);
            var tagSize = gap - valueSize;
            if (gap < 0 || valueSize < 0 || tagSize < 0 || names[0].Prefix < tagSize)
            {
                return null;
            }

            for (var i = 1; i < names.Count; i++)
            {
                for (var k = 1; k <= tagSize; k++)
                {
                    if (data[names[i].Prefix - k] != data[names[0].Prefix - k])
                    {
                        return null;
                    }
                }
            }

            var blocks = names.Select(name => name.Start + name.Length).ToList();
            var columns = new List<string> { NameColumn };
            var cells = new List<string[]> { names.Select(name => Ascii(data, name.Start, name.Length)).ToArray() };
            AddValueColumns(data, blocks, 0, valueSize, columns, cells, firstName: null);
            return Build(columns, cells, names.Count);
        }

        private static LinkedFileTable TryCountedRecords(byte[] data)
        {
            for (var countAt = 0; countAt + 4 <= data.Length; countAt++)
            {
                var count = U32(data, countAt);
                if (count < MinRecords || count > MaxRecords)
                {
                    continue;
                }

                var rest = data.Length - (countAt + 4);
                if (rest % count != 0)
                {
                    continue;
                }

                var stride = (int)(rest / count);
                if (stride < MinStride || stride > MaxStride || stride % 4 != 0)
                {
                    continue;
                }

                var starts = Enumerable.Range(0, (int)count).Select(i => countAt + 4 + i * stride).ToList();
                var ids = new HashSet<uint>();
                if (!starts.All(start => ids.Add(U32(data, start))))
                {
                    continue;
                }

                var columns = new List<string>();
                var cells = new List<string[]>();
                AddValueColumns(data, starts, 0, stride, columns, cells, firstName: IdColumn);
                return Build(columns, cells, starts.Count);
            }

            return null;
        }

        private static LinkedFileTable TryFixedSlots(byte[] data)
        {
            if (data.Length > MaxSlotFileBytes)
            {
                return null;
            }

            var lastNonZero = data.Length - 1;
            while (lastNonZero >= 0 && data[lastNonZero] == 0)
            {
                lastNonZero--;
            }

            var latestTime = (uint)Math.Min(uint.MaxValue, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
            // The layout that explains the most filled records wins; a longer header would
            // swallow records, and a larger record size would read neighbours as one. Ties go to
            // the smaller record size, then the shorter header.
            List<int> best = null;
            var bestStride = 0;
            var bestTimeAt = 0;
            for (var stride = MinStride; stride <= MaxStride; stride++)
            {
                for (var header = 0; header <= MaxHeader && header <= lastNonZero; header++)
                {
                    var span = data.Length - header;
                    if (span % stride != 0)
                    {
                        continue;
                    }

                    var count = span / stride;
                    if (count < MinRecords || count > MaxRecords)
                    {
                        continue;
                    }

                    var filled = FilledRecordsWithTime(data, header, stride, count, lastNonZero, latestTime, out var timeAt);
                    if (filled != null && (best == null || filled.Count > best.Count))
                    {
                        best = filled;
                        bestStride = stride;
                        bestTimeAt = timeAt;
                    }
                }
            }

            if (best == null)
            {
                return null;
            }

            var columns = new List<string>();
            var cells = new List<string[]>();
            if (bestTimeAt > 0 && bestTimeAt <= 8)
            {
                columns.Add(IdColumn);
                cells.Add(best.Select(start => UInt(data, start, bestTimeAt).ToString(CultureInfo.InvariantCulture)).ToArray());
            }

            columns.Add(TimeColumn);
            cells.Add(best.Select(start => U32(data, start + bestTimeAt).ToString(CultureInfo.InvariantCulture)).ToArray());
            AddValueColumns(data, best, bestTimeAt + 4, bestStride - bestTimeAt - 4, columns, cells, firstName: null);
            return Build(columns, cells, best.Count);
        }

        /// <summary>
        /// The starts of the non-zero records when every one of them holds a Unix time at the same
        /// offset, which is returned in <paramref name="timeAt"/>; null otherwise. Stops at the
        /// first record that leaves no common offset.
        /// </summary>
        private static List<int> FilledRecordsWithTime(
            byte[] data, int header, int stride, int count, int lastNonZero, uint latestTime, out int timeAt)
        {
            timeAt = -1;
            var offsets = stride - 3;
            bool[] candidates = null;
            var filled = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var start = header + i * stride;
                if (start > lastNonZero)
                {
                    break;
                }

                if (IsZero(data, start, stride))
                {
                    continue;
                }

                // A record of text has no zero byte; a record of numbers always has one.
                if (Array.IndexOf(data, (byte)0, start, stride) < 0)
                {
                    return null;
                }

                if (candidates == null)
                {
                    candidates = Enumerable.Repeat(true, offsets).ToArray();
                }

                var any = false;
                for (var offset = 0; offset < offsets; offset++)
                {
                    if (!candidates[offset])
                    {
                        continue;
                    }

                    var value = U32(data, start + offset);
                    candidates[offset] = value >= EarliestTime && value <= latestTime;
                    any |= candidates[offset];
                }

                if (!any)
                {
                    return null;
                }

                filled.Add(start);
            }

            if (candidates == null)
            {
                return null;
            }

            timeAt = Array.IndexOf(candidates, true);
            return filled;
        }

        /// <summary>
        /// Splits a block of <paramref name="size"/> bytes at <paramref name="offset"/> in each
        /// record into number columns: a double where every record holds a plain one, a Windows
        /// file time where every record holds one, else 32-bit, then 16-bit, then 8-bit integers.
        /// </summary>
        private static void AddValueColumns(
            byte[] data, IReadOnlyList<int> starts, int offset, int size,
            List<string> columns, List<string[]> cells, string firstName)
        {
            var at = 0;
            while (at < size)
            {
                var remaining = size - at;
                var position = offset + at;
                string[] values;
                int width;
                if (remaining >= 8 && starts.All(start => IsPlainDouble(U64(data, start + position))) &&
                    starts.Any(start => U64(data, start + position) != 0))
                {
                    width = 8;
                    values = starts.Select(start => BitConverter.Int64BitsToDouble((long)U64(data, start + position)).ToString("R", CultureInfo.InvariantCulture)).ToArray();
                }
                else if (remaining >= 8 && starts.All(start => IsFileTimeOrZero(U64(data, start + position))) &&
                         starts.Any(start => U64(data, start + position) != 0))
                {
                    width = 8;
                    values = starts.Select(start => U64(data, start + position).ToString(CultureInfo.InvariantCulture)).ToArray();
                }
                else
                {
                    width = remaining >= 4 ? 4 : remaining >= 2 ? 2 : 1;
                    values = starts.Select(start => UInt(data, start + position, width).ToString(CultureInfo.InvariantCulture)).ToArray();
                }

                columns.Add(at == 0 && firstName != null
                    ? firstName
                    : "value@" + position.ToString(CultureInfo.InvariantCulture));
                cells.Add(values);
                at += width;
            }
        }

        private static LinkedFileTable Build(List<string> columns, List<string[]> cells, int rowCount)
        {
            var rows = Enumerable.Range(0, rowCount).Select(row => cells.Select(column => column[row]));
            return LinkedFileTable.Build(columns, rows);
        }

        private static bool IsPlainDouble(ulong bits)
        {
            if (bits == 0)
            {
                return true;
            }

            var value = BitConverter.Int64BitsToDouble((long)bits);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            var magnitude = Math.Abs(value);
            return magnitude >= 1e-6 && magnitude <= 1e15;
        }

        private static bool IsFileTimeOrZero(ulong value)
        {
            return value == 0 || (value >= EarliestFileTime && value <= LatestFileTime);
        }

        private static bool IsName(byte[] data, int start, int length)
        {
            var hasLetterOrDigit = false;
            for (var i = 0; i < length; i++)
            {
                var b = data[start + i];
                if (b < 0x20 || b > 0x7E || (i == 0 && b == 0x20))
                {
                    return false;
                }

                hasLetterOrDigit |= char.IsLetterOrDigit((char)b);
            }

            return hasLetterOrDigit;
        }

        private static bool IsSpace(byte b)
        {
            return b == 0x20 || b == 0x09 || b == 0x0D || b == 0x0A;
        }

        private static bool IsZero(byte[] data, int start, int length)
        {
            for (var i = 0; i < length; i++)
            {
                if (data[start + i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string Ascii(byte[] data, int start, int length)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = (char)data[start + i];
            }

            return new string(chars);
        }

        private static int HexValue(char c)
        {
            return c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
        }

        private static uint U32(byte[] data, int at)
        {
            return (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));
        }

        private static ulong U64(byte[] data, int at)
        {
            return U32(data, at) | ((ulong)U32(data, at + 4) << 32);
        }

        private static ulong UInt(byte[] data, int at, int width)
        {
            ulong value = 0;
            for (var i = width - 1; i >= 0; i--)
            {
                value = (value << 8) | data[at + i];
            }

            return value;
        }
    }
}
