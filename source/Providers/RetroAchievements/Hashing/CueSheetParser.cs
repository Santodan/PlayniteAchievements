using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements.Hashing
{
    internal sealed class CueSheet
    {
        public string CuePath { get; set; }
        public List<CueFileEntry> Files { get; } = new List<CueFileEntry>();
        public List<CueTrackEntry> Tracks { get; } = new List<CueTrackEntry>();
        public List<string> RemSessions { get; } = new List<string>();
    }

    internal sealed class CueFileEntry
    {
        public string FileName { get; set; }
        public string FileType { get; set; }
        public List<CueTrackEntry> Tracks { get; } = new List<CueTrackEntry>();
    }

    internal sealed class CueTrackEntry
    {
        public int Number { get; set; }
        public string Mode { get; set; }
        public CueFileEntry File { get; set; }
        public int? Index00Frames { get; set; }
        public int? Index01Frames { get; set; }
        public int PregapFrames { get; set; }
        public string Session { get; set; }
    }

    internal static class CueSheetParser
    {
        public static bool TryParseFile(string cuePath, out CueSheet sheet, out string error)
        {
            sheet = null;
            error = null;

            if (string.IsNullOrWhiteSpace(cuePath))
            {
                error = "Cue path is required.";
                return false;
            }

            if (!File.Exists(cuePath))
            {
                error = "Cue file does not exist.";
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(cuePath, Encoding.Default);
                return TryParse(cuePath, lines, out sheet, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryParse(string cuePath, IEnumerable<string> lines, out CueSheet sheet, out string error)
        {
            sheet = new CueSheet { CuePath = cuePath };
            error = null;

            if (lines == null)
            {
                error = "Cue content is required.";
                return false;
            }

            CueFileEntry currentFile = null;
            CueTrackEntry currentTrack = null;
            string currentSession = null;

            var lineNumber = 0;
            foreach (var rawLine in lines)
            {
                lineNumber++;
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var command = ReadFirstToken(line, out var rest).ToUpperInvariant();
                switch (command)
                {
                    case "REM":
                        ParseRem(rest, sheet, ref currentSession);
                        break;

                    case "FILE":
                        if (!TryParseFileCommand(rest, out var fileName, out var fileType))
                        {
                            error = $"Invalid FILE command at line {lineNumber}.";
                            return false;
                        }

                        currentFile = new CueFileEntry
                        {
                            FileName = fileName,
                            FileType = fileType
                        };
                        sheet.Files.Add(currentFile);
                        currentTrack = null;
                        break;

                    case "TRACK":
                        if (currentFile == null)
                        {
                            error = $"TRACK command before FILE at line {lineNumber}.";
                            return false;
                        }

                        if (!TryParseTrackCommand(rest, out var trackNumber, out var mode))
                        {
                            error = $"Invalid TRACK command at line {lineNumber}.";
                            return false;
                        }

                        currentTrack = new CueTrackEntry
                        {
                            Number = trackNumber,
                            Mode = mode,
                            File = currentFile,
                            Session = currentSession
                        };
                        currentFile.Tracks.Add(currentTrack);
                        sheet.Tracks.Add(currentTrack);
                        break;

                    case "INDEX":
                        if (currentTrack == null)
                        {
                            error = $"INDEX command before TRACK at line {lineNumber}.";
                            return false;
                        }

                        if (!TryParseIndexCommand(rest, out var indexNumber, out var frames))
                        {
                            error = $"Invalid INDEX command at line {lineNumber}.";
                            return false;
                        }

                        if (indexNumber == 0)
                        {
                            currentTrack.Index00Frames = frames;
                        }
                        else if (indexNumber == 1)
                        {
                            currentTrack.Index01Frames = frames;
                        }
                        break;

                    case "PREGAP":
                        if (currentTrack == null)
                        {
                            error = $"PREGAP command before TRACK at line {lineNumber}.";
                            return false;
                        }

                        if (!TryParseMsf(rest, out var pregapFrames))
                        {
                            error = $"Invalid PREGAP command at line {lineNumber}.";
                            return false;
                        }

                        currentTrack.PregapFrames = pregapFrames;
                        break;
                }
            }

            if (sheet.Files.Count == 0 || sheet.Tracks.Count == 0)
            {
                error = "Cue file does not contain any FILE/TRACK entries.";
                return false;
            }

            return true;
        }

        public static bool TryParseMsf(string value, out int frames)
        {
            frames = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Trim().Split(':');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                return false;
            }

            if (minutes < 0 || seconds < 0 || seconds >= 60 || frame < 0 || frame >= 75)
            {
                return false;
            }

            frames = ((minutes * 60) + seconds) * 75 + frame;
            return true;
        }

        private static void ParseRem(string rest, CueSheet sheet, ref string currentSession)
        {
            var key = ReadFirstToken(rest ?? string.Empty, out var value);
            if (!string.Equals(key, "SESSION", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var session = (value ?? string.Empty).Trim();
            if (session.Length == 0)
            {
                return;
            }

            currentSession = session;
            sheet.RemSessions.Add(session);
        }

        private static bool TryParseFileCommand(string rest, out string fileName, out string fileType)
        {
            fileName = null;
            fileType = null;
            rest = (rest ?? string.Empty).Trim();
            if (rest.Length == 0)
            {
                return false;
            }

            if (rest[0] == '"')
            {
                var endQuote = rest.IndexOf('"', 1);
                if (endQuote <= 0)
                {
                    return false;
                }

                fileName = rest.Substring(1, endQuote - 1);
                fileType = rest.Substring(endQuote + 1).Trim();
                return fileName.Length > 0 && fileType.Length > 0;
            }

            var lastSpace = LastWhitespaceIndex(rest);
            if (lastSpace <= 0 || lastSpace >= rest.Length - 1)
            {
                return false;
            }

            fileName = rest.Substring(0, lastSpace).Trim();
            fileType = rest.Substring(lastSpace + 1).Trim();
            return fileName.Length > 0 && fileType.Length > 0;
        }

        private static bool TryParseTrackCommand(string rest, out int trackNumber, out string mode)
        {
            trackNumber = 0;
            mode = null;

            var numberToken = ReadFirstToken(rest ?? string.Empty, out var modeRest);
            if (!int.TryParse(numberToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out trackNumber))
            {
                return false;
            }

            mode = (modeRest ?? string.Empty).Trim();
            return trackNumber > 0 && mode.Length > 0;
        }

        private static bool TryParseIndexCommand(string rest, out int indexNumber, out int frames)
        {
            indexNumber = 0;
            frames = 0;

            var indexToken = ReadFirstToken(rest ?? string.Empty, out var timeRest);
            if (!int.TryParse(indexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out indexNumber))
            {
                return false;
            }

            return indexNumber >= 0 && TryParseMsf(timeRest, out frames);
        }

        private static string ReadFirstToken(string value, out string rest)
        {
            value = (value ?? string.Empty).TrimStart();
            rest = string.Empty;
            if (value.Length == 0)
            {
                return string.Empty;
            }

            var idx = 0;
            while (idx < value.Length && !char.IsWhiteSpace(value[idx]))
            {
                idx++;
            }

            var token = value.Substring(0, idx);
            rest = idx < value.Length ? value.Substring(idx).TrimStart() : string.Empty;
            return token;
        }

        private static int LastWhitespaceIndex(string value)
        {
            for (var i = value.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(value[i]))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
