using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayniteAchievements.Services.Sound
{
    /// <summary>
    /// The line protocol between the plugin and the sound host process, compiled into both. One
    /// message per line, fields separated by tabs, numerics invariant, the path always last (paths
    /// cannot contain tabs or newlines on Windows, so nothing needs escaping). Kept free of
    /// Newtonsoft.Json and tuple syntax: the host exe cannot resolve the Playnite-provided
    /// assemblies Toolbox strips from the package.
    ///
    /// plugin to host:  preload\t{path}...  |  play\t{id}\t{gain}\t{path}  |  stop  |  quit
    /// host to plugin:  ready\t{pid}  |  started\t{id}\t{qpc}  |  error\t{id or -1}\t{message}
    /// </summary>
    internal static class SoundHostProtocol
    {
        public const string ExecutableName = "PlayniteAchievementsHelper.exe";

        /// <summary>The helper's role argument that selects the unlock-sound renderer.</summary>
        public const string RoleArgument = "sound";

        public const string PreloadVerb = "preload";
        public const string PlayVerb = "play";
        public const string StopVerb = "stop";
        public const string QuitVerb = "quit";
        public const string ReadyVerb = "ready";
        public const string StartedVerb = "started";
        public const string ErrorVerb = "error";

        private const char Separator = '\t';

        public static string EncodePreload(IEnumerable<string> paths)
        {
            var parts = new List<string> { PreloadVerb };
            if (paths != null)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        parts.Add(Sanitize(path));
                    }
                }
            }

            return string.Join(Separator.ToString(), parts);
        }

        public static string EncodePlay(int id, string path, double gain)
        {
            return string.Join(
                Separator.ToString(),
                PlayVerb,
                id.ToString(CultureInfo.InvariantCulture),
                gain.ToString("0.####", CultureInfo.InvariantCulture),
                Sanitize(path));
        }

        public static string EncodeReady(int pid)
        {
            return ReadyVerb + Separator + pid.ToString(CultureInfo.InvariantCulture);
        }

        public static string EncodeStarted(int id, long qpc)
        {
            return StartedVerb + Separator
                + id.ToString(CultureInfo.InvariantCulture) + Separator
                + qpc.ToString(CultureInfo.InvariantCulture);
        }

        public static string EncodeError(int id, string message)
        {
            return ErrorVerb + Separator
                + id.ToString(CultureInfo.InvariantCulture) + Separator
                + Sanitize(message ?? string.Empty);
        }

        /// <summary>Parses one line; false for a blank, unknown, or malformed message.</summary>
        public static bool TryParse(string line, out SoundHostMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var fields = line.Split(Separator);
            var verb = fields[0];
            switch (verb)
            {
                case PreloadVerb:
                {
                    var paths = new List<string>(fields.Length - 1);
                    for (var i = 1; i < fields.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(fields[i]))
                        {
                            paths.Add(fields[i]);
                        }
                    }

                    message = new SoundHostMessage(verb) { Paths = paths.ToArray() };
                    return true;
                }

                case PlayVerb:
                {
                    if (fields.Length < 4
                        || !TryParseInt(fields[1], out var id)
                        || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var gain)
                        || string.IsNullOrWhiteSpace(fields[3]))
                    {
                        return false;
                    }

                    message = new SoundHostMessage(verb) { Id = id, Gain = gain, Path = fields[3] };
                    return true;
                }

                case StopVerb:
                case QuitVerb:
                    message = new SoundHostMessage(verb);
                    return true;

                case ReadyVerb:
                {
                    if (fields.Length < 2 || !TryParseInt(fields[1], out var pid))
                    {
                        return false;
                    }

                    message = new SoundHostMessage(verb) { Id = pid };
                    return true;
                }

                case StartedVerb:
                {
                    if (fields.Length < 3
                        || !TryParseInt(fields[1], out var id)
                        || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var qpc))
                    {
                        return false;
                    }

                    message = new SoundHostMessage(verb) { Id = id, Qpc = qpc };
                    return true;
                }

                case ErrorVerb:
                {
                    if (fields.Length < 3 || !TryParseInt(fields[1], out var id))
                    {
                        return false;
                    }

                    message = new SoundHostMessage(verb) { Id = id, Text = fields[2] };
                    return true;
                }

                default:
                    return false;
            }
        }

        private static bool TryParseInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string Sanitize(string text)
        {
            return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    /// <summary>One parsed protocol line. Which fields are meaningful depends on <see cref="Verb"/>.</summary>
    internal sealed class SoundHostMessage
    {
        public SoundHostMessage(string verb)
        {
            Verb = verb;
        }

        public string Verb { get; }

        /// <summary>Play/started/error id; the process id for ready.</summary>
        public int Id { get; set; }

        public double Gain { get; set; }
        public long Qpc { get; set; }
        public string Path { get; set; }
        public string[] Paths { get; set; }
        public string Text { get; set; }
    }
}
