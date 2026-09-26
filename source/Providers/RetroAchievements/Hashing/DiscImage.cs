using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal enum DiscTrackSelectorKind
    {
        Number,
        FirstData,
        Last,
        Largest,
        FirstOfSecondSession
    }

    /// <summary>Which track to open, mirroring rcheevos's RC_HASH_CDTRACK_* values (rc_hash.h).</summary>
    internal readonly struct DiscTrackSelector
    {
        private DiscTrackSelector(DiscTrackSelectorKind kind, int number)
        {
            Kind = kind;
            Number = number;
        }

        public DiscTrackSelectorKind Kind { get; }
        public int Number { get; }

        public static DiscTrackSelector Track(int number) => new DiscTrackSelector(DiscTrackSelectorKind.Number, number);
        public static DiscTrackSelector FirstData => new DiscTrackSelector(DiscTrackSelectorKind.FirstData, 0);
        public static DiscTrackSelector Last => new DiscTrackSelector(DiscTrackSelectorKind.Last, 0);
        public static DiscTrackSelector Largest => new DiscTrackSelector(DiscTrackSelectorKind.Largest, 0);
        public static DiscTrackSelector FirstOfSecondSession => new DiscTrackSelector(DiscTrackSelectorKind.FirstOfSecondSession, 0);
    }

    /// <summary>
    /// A disc image that tracks are opened from: a cue sheet, a .gdi, a plain image file, or a
    /// seekable stream such as the ISO inside a CSO/RVZ container. Track selection, sector size
    /// detection, and LBA layout are ported from rcheevos cdreader.c.
    /// </summary>
    internal sealed class DiscImage
    {
        private static readonly byte[] SyncPattern = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        private readonly RaHashSource _source;
        private readonly CueSheet _cue;
        private readonly string _gdiPath;
        private string[] _gdiLines;
        private readonly Dictionary<string, long> _fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private DiscImage(RaHashSource source, CueSheet cue, string gdiPath)
        {
            _source = source;
            _cue = cue;
            _gdiPath = gdiPath;
        }

        public string Path => _source.Path;

        /// <summary>True for a cue sheet or .gdi, which can describe more than one track.</summary>
        public bool IsMultiTrack => _cue != null || _gdiPath != null;

        public static bool IsGdiPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(System.IO.Path.GetExtension(path), ".gdi", StringComparison.OrdinalIgnoreCase);
        }

        public static DiscImage Open(RaHashSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            if (source.IsFile && CueTrackReader.IsCuePath(source.Path))
            {
                if (!CueSheetParser.TryParseFile(source.Path, out var sheet, out var error))
                {
                    throw new InvalidDataException(error ?? "Unable to parse cue sheet.");
                }

                return new DiscImage(source, sheet, null);
            }

            if (source.IsFile && IsGdiPath(source.Path))
            {
                return new DiscImage(source, null, source.Path);
            }

            return new DiscImage(source, null, null);
        }

        /// <summary>Opens the selected track, or returns null when the image has no such track.</summary>
        public DiscTrack OpenTrack(DiscTrackSelector selector)
        {
            if (_cue != null)
            {
                return OpenCueTrack(selector);
            }

            if (_gdiPath != null)
            {
                return OpenGdiTrack(selector);
            }

            return OpenSingleTrack(selector);
        }

        /// <summary>
        /// Opens a plain image as one track of fixed-size raw sectors with no header. Used for
        /// cue-less Atari Jaguar CD images, which rcheevos does not open at all.
        /// </summary>
        public DiscTrack OpenRawTrack(int sectorSize)
        {
            var stream = OpenSourceStream();
            return new DiscTrack(stream, 1, sectorSize, 0, sectorSize, 0, 0, 0);
        }

        // cdreader_open_bin_track (cdreader.c:84-147). rcheevos opens only track 1 of a cue-less
        // image; the special selectors also resolve to that track so a plain image keeps hashing.
        private DiscTrack OpenSingleTrack(DiscTrackSelector selector)
        {
            if (selector.Kind == DiscTrackSelectorKind.Number && selector.Number > 1)
            {
                return null;
            }

            var stream = OpenSourceStream();
            try
            {
                // Container streams (CSO/RVZ) always hold 2048-byte sectors.
                if (!_source.IsFile)
                {
                    return new DiscTrack(stream, 1, 2048, 0, 2048, 0, 0, 0);
                }

                if (!TryDetectSectorSize(stream, 0, 0, out var sectorSize, out var headerSize, out var firstSector))
                {
                    firstSector = 0;
                    var size = stream.Length;
                    if (size % 2352 == 0)
                    {
                        sectorSize = 2352;
                        headerSize = 24;
                    }
                    else if (size % 2048 == 0)
                    {
                        sectorSize = 2048;
                        headerSize = 0;
                    }
                    else if (size % 2336 == 0)
                    {
                        sectorSize = 2336;
                        headerSize = 8;
                    }
                    else
                    {
                        // rcheevos gives up here; a plain image of any other size reads as 2048-byte sectors.
                        sectorSize = 2048;
                        headerSize = 0;
                    }
                }

                return new DiscTrack(stream, 1, sectorSize, headerSize, 2048, 0, firstSector, 0, _source.Path);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private Stream OpenSourceStream()
        {
            if (_source.IsFile)
            {
                return OpenTrackFile(_source.Path);
            }

            return _source.Open();
        }

        private static Stream OpenTrackFile(string path)
        {
            // Reads are batched by DiscTrack, so the FileStream buffer only adds a copy.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        }

        private long GetFileSize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            if (!_fileSizes.TryGetValue(path, out var size))
            {
                try
                {
                    var info = new FileInfo(path);
                    size = info.Exists ? info.Length : 0;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    size = 0;
                }

                _fileSizes[path] = size;
            }

            return size;
        }

        private sealed class CueTrackState
        {
            public int Id;
            public int SectorSize;
            public int SectorCount;
            public int FirstSector;
            public int PregapSectors;
            public bool IsData;
            public long FileTrackOffset;
            public int FileFirstSector;
            public string Mode = string.Empty;
            public string FileName = string.Empty;

            public CueTrackState Clone() => (CueTrackState)MemberwiseClone();
        }

        // cdreader_open_cue_track (cdreader.c:250-550), over the parsed cue sheet.
        private DiscTrack OpenCueTrack(DiscTrackSelector selector)
        {
            var kind = selector.Kind;
            var current = new CueTrackState();
            var previous = new CueTrackState();
            var largest = new CueTrackState();
            var done = false;

            foreach (var file in _cue.Files)
            {
                // FILE: close out the last track of the previous file (cdreader.c:400-446).
                if (current.SectorSize != 0)
                {
                    previous = current.Clone();
                    if (previous.SectorCount == 0)
                    {
                        previous.SectorCount = CountFileSectors(previous) - previous.FirstSector;
                    }

                    if (kind == DiscTrackSelectorKind.Largest && previous.IsData && previous.SectorCount > largest.SectorCount)
                    {
                        largest = previous.Clone();
                    }
                }

                current = new CueTrackState
                {
                    FileFirstSector = previous.FileFirstSector + previous.FirstSector + previous.SectorCount,
                    FileName = file.FileName ?? string.Empty
                };

                foreach (var track in file.Tracks)
                {
                    // TRACK (cdreader.c:372-399). The struct is not reset, so the file track offset carries.
                    if (current.SectorSize != 0)
                    {
                        previous = current.Clone();
                    }

                    current.Id = track.Number;
                    current.PregapSectors = -1;
                    current.FirstSector = -1;
                    current.Mode = FirstToken(track.Mode);
                    current.IsData = current.Mode.StartsWith("MODE", StringComparison.OrdinalIgnoreCase);
                    current.SectorSize = current.IsData ? ParseLeadingInt(current.Mode, 6) : 2352;

                    // INDEX (cdreader.c:301-371): the first INDEX line of a track fixes its start.
                    var firstIndex = FirstIndexFrames(track);
                    if (firstIndex.HasValue)
                    {
                        current.FirstSector = firstIndex.Value;
                        if (string.Equals(current.FileName, previous.FileName, StringComparison.Ordinal))
                        {
                            previous.SectorCount = current.FirstSector - previous.FirstSector;
                            current.FileTrackOffset += (long)previous.SectorCount * previous.SectorSize;
                        }

                        if (kind == DiscTrackSelectorKind.Largest && previous.SectorCount > largest.SectorCount && previous.IsData)
                        {
                            largest = previous.Clone();
                        }
                    }

                    if (track.Index01Frames.HasValue && current.FirstSector >= 0)
                    {
                        current.PregapSectors = track.Index01Frames.Value - current.FirstSector;

                        if ((kind == DiscTrackSelectorKind.Number && current.Id == selector.Number) ||
                            (kind == DiscTrackSelectorKind.FirstData && current.IsData) ||
                            (kind == DiscTrackSelectorKind.FirstOfSecondSession && ParseSession(track.Session) == 2))
                        {
                            done = true;
                            break;
                        }
                    }
                }

                if (done)
                {
                    break;
                }
            }

            int wanted;
            switch (kind)
            {
                case DiscTrackSelectorKind.Largest:
                    // cdreader.c:477-494
                    if (current.SectorSize != 0 && current.IsData)
                    {
                        current.SectorCount = CountFileSectors(current) - current.FirstSector;
                        if (largest.SectorCount > current.SectorCount)
                        {
                            current = largest;
                        }
                    }
                    else
                    {
                        current = largest;
                    }

                    wanted = current.Id;
                    break;

                case DiscTrackSelectorKind.Last:
                    wanted = current.Id;
                    break;

                case DiscTrackSelectorKind.Number:
                    wanted = selector.Number;
                    break;

                default:
                    if (!done)
                    {
                        return null;
                    }

                    wanted = current.Id;
                    break;
            }

            if (current.Id != wanted || current.Id == 0 || string.IsNullOrEmpty(current.FileName))
            {
                return null;
            }

            var binPath = CueTrackReader.ResolveTrackPath(_source.Path, current.FileName);
            if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
            {
                return null;
            }

            return OpenBin(
                binPath,
                current.Id,
                current.Mode,
                current.FileTrackOffset,
                current.FileFirstSector + current.FirstSector,
                Math.Max(0, current.PregapSectors),
                overrideFirstSector: null);
        }

        private int CountFileSectors(CueTrackState track)
        {
            if (track.SectorSize <= 0)
            {
                return 0;
            }

            var binPath = CueTrackReader.ResolveTrackPath(_source.Path, track.FileName);
            return (int)((uint)GetFileSize(binPath) / (uint)track.SectorSize);
        }

        // cdreader_open_gdi_track (cdreader.c:552-775). Line format: [track] [lba] [type] [sector size] [file] [offset].
        private DiscTrack OpenGdiTrack(DiscTrackSelector selector)
        {
            var lines = _gdiLines ?? (_gdiLines = File.ReadAllText(_gdiPath, Encoding.Default).Split('\n'));

            int wanted;
            switch (selector.Kind)
            {
                case DiscTrackSelectorKind.Number:
                    wanted = selector.Number;
                    break;
                case DiscTrackSelectorKind.Last:
                    // The first line holds the number of tracks.
                    wanted = ParseLeadingInt(lines[0].TrimStart(), 0);
                    break;
                case DiscTrackSelectorKind.FirstData:
                    wanted = -1;
                    break;
                default:
                    // rcheevos skips every line for these selectors and opens nothing.
                    return null;
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var fields = SplitGdiLine(lines[i]);
                if (fields.Count < 5)
                {
                    continue;
                }

                var trackNumber = ParseLeadingInt(fields[0], 0);
                var lba = ParseLeadingInt(fields[1], 0);
                var trackType = ParseLeadingInt(fields[2], 0);
                var sectorSize = fields[3];

                var matches = wanted == -1 ? trackType == 4 : trackNumber == wanted;
                if (!matches)
                {
                    continue;
                }

                var binPath = CueTrackReader.ResolveTrackPath(_gdiPath, fields[4]);
                if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
                {
                    return null;
                }

                return OpenBin(binPath, trackNumber, "MODE1/" + sectorSize, 0, lba, 0, overrideFirstSector: lba);
            }

            return null;
        }

        private static List<string> SplitGdiLine(string line)
        {
            var fields = new List<string>();
            var i = 0;
            line = line ?? string.Empty;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                if (i >= line.Length)
                {
                    break;
                }

                if (line[i] == '"')
                {
                    var end = line.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        throw new InvalidDataException("Quoted string without closing quote in .gdi.");
                    }

                    fields.Add(line.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }

                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                fields.Add(line.Substring(start, i - start));
            }

            return fields;
        }

        // cdreader_open_bin (cdreader.c:149-205): detect the layout, else trust the cue/gdi mode.
        private static DiscTrack OpenBin(string binPath, int trackNumber, string mode, long fileTrackOffset, int trackFirstSector, int pregapSectors, int? overrideFirstSector)
        {
            var stream = OpenTrackFile(binPath);
            try
            {
                var rawDataSize = 2048;
                if (TryDetectSectorSize(stream, pregapSectors, fileTrackOffset, out var sectorSize, out var headerSize, out var detectedFirstSector))
                {
                    trackFirstSector = detectedFirstSector;
                }
                else if (!TryGetModeLayout(mode, out sectorSize, out headerSize, out rawDataSize))
                {
                    stream.Dispose();
                    return null;
                }

                // A .gdi states the LBA explicitly (cdreader.c:757).
                if (overrideFirstSector.HasValue)
                {
                    trackFirstSector = overrideFirstSector.Value;
                }

                return new DiscTrack(stream, trackNumber, sectorSize, headerSize, rawDataSize, fileTrackOffset, trackFirstSector, pregapSectors, binPath);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static bool TryGetModeLayout(string mode, out int sectorSize, out int headerSize, out int rawDataSize)
        {
            rawDataSize = 2048;
            mode = mode ?? string.Empty;
            if (mode.StartsWith("MODE2/2352", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 2352;
                headerSize = 24;
            }
            else if (mode.StartsWith("MODE1/2048", StringComparison.OrdinalIgnoreCase) ||
                     mode.StartsWith("MODE2/2048", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 2048;
                headerSize = 0;
            }
            else if (mode.StartsWith("MODE2/2336", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 2336;
                headerSize = 8;
            }
            else if (mode.StartsWith("MODE1/2352", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 2352;
                headerSize = 16;
            }
            else if (mode.StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                // Audio tracks have no header or footer (cdreader.c:196-201).
                sectorSize = 2352;
                headerSize = 0;
                rawDataSize = 2352;
            }
            else
            {
                sectorSize = 0;
                headerSize = 0;
                return false;
            }

            return true;
        }

        // cdreader_determine_sector_size (cdreader.c:21-82): look for the sync pattern at each raw
        // sector size, then for "CD001" in the volume descriptor at sector 16.
        private static bool TryDetectSectorSize(Stream stream, int pregapSectors, long fileTrackOffset, out int sectorSize, out int headerSize, out int trackFirstSector)
        {
            sectorSize = 0;
            headerSize = 0;
            trackFirstSector = 0;

            var header = new byte[32];
            var length = stream.Length;
            long tocSector = 16 + pregapSectors;

            if (ReadAt(stream, length, (tocSector * 2352) + fileTrackOffset, header) < header.Length)
            {
                return false;
            }

            if (HashUtils.MatchesAt(header, 0, SyncPattern))
            {
                sectorSize = 2352;
            }
            else
            {
                ReadAt(stream, length, (tocSector * 2336) + fileTrackOffset, header);
                if (HashUtils.MatchesAt(header, 0, SyncPattern))
                {
                    sectorSize = 2336;
                }
                else
                {
                    ReadAt(stream, length, (tocSector * 2048) + fileTrackOffset, header);
                    if (header[1] == 'C' && header[2] == 'D' && header[3] == '0' && header[4] == '0' && header[5] == '1')
                    {
                        sectorSize = 2048;
                        return true;
                    }

                    return false;
                }
            }

            headerSize = header[25] == 'C' && header[26] == 'D' && header[27] == '0' && header[28] == '0' && header[29] == '1' ? 24 : 16;
            trackFirstSector = MsfToSector(header) - (int)tocSector;
            return true;
        }

        // cdreader_get_sector (cdreader.c:9-19): BCD MSF to LBA, where 00:02:00 is LBA 0.
        private static int MsfToSector(byte[] header)
        {
            var minutes = ((header[12] >> 4) * 10) + (header[12] & 0x0F);
            var seconds = ((header[13] >> 4) * 10) + (header[13] & 0x0F);
            var frames = ((header[14] >> 4) * 10) + (header[14] & 0x0F);
            return ((((minutes * 60) + seconds) * 75) + frames) - 150;
        }

        private static int ReadAt(Stream stream, long length, long position, byte[] buffer)
        {
            if (position < 0 || position >= length)
            {
                return 0;
            }

            stream.Seek(position, SeekOrigin.Begin);
            return HashUtils.ReadFull(stream, buffer, 0, buffer.Length);
        }

        private static int? FirstIndexFrames(CueTrackEntry track)
        {
            if (track.Index00Frames.HasValue && (!track.Index01Frames.HasValue || track.Index00Frames.Value <= track.Index01Frames.Value))
            {
                return track.Index00Frames;
            }

            return track.Index01Frames;
        }

        private static int ParseSession(string session)
        {
            return int.TryParse(session, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 1;
        }

        private static string FirstToken(string value)
        {
            value = (value ?? string.Empty).Trim();
            var end = 0;
            while (end < value.Length && !char.IsWhiteSpace(value[end]))
            {
                end++;
            }

            return value.Substring(0, end);
        }

        // atoi semantics: leading digits after optional whitespace, 0 when there are none.
        private static int ParseLeadingInt(string value, int start)
        {
            if (value == null || start >= value.Length)
            {
                return 0;
            }

            var i = start;
            while (i < value.Length && char.IsWhiteSpace(value[i]))
            {
                i++;
            }

            long result = 0;
            while (i < value.Length && value[i] >= '0' && value[i] <= '9' && result < int.MaxValue)
            {
                result = (result * 10) + (value[i] - '0');
                i++;
            }

            return (int)Math.Min(result, int.MaxValue);
        }
    }
}
