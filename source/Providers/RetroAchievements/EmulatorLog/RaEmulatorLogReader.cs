using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayniteAchievements.Providers.RetroAchievements.EmulatorLog
{
    /// <summary>
    /// Tails a RetroAchievements-capable emulator log and turns newly appended "Awarding achievement"
    /// lines into unlock observations. Only bytes past the session's consumed offset are parsed, so a
    /// steady stream of verbose log output does not force a full-file re-read on every signal.
    /// </summary>
    internal static class RaEmulatorLogReader
    {
        // Upper bound on how many trailing bytes a single read parses. Guards against a very large
        // appended region (for example the first read of a log that accumulates across many sessions).
        private const long MaxReadBytes = 8L * 1024 * 1024;

        public static bool TryRead(
            RaEmulatorLogSession session,
            out IReadOnlyList<AchievementProgressObservation> observations)
        {
            observations = Array.Empty<AchievementProgressObservation>();
            if (session == null || string.IsNullOrWhiteSpace(session.LogPath))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(session.LogPath);
                if (!info.Exists)
                {
                    // The emulator has not written the log yet; nothing to report, keep waiting for signals.
                    return true;
                }

                var length = info.Length;
                if (length < session.ConsumedOffset)
                {
                    // The emulator rewrote/truncated the log on a new run.
                    session.ConsumedOffset = 0;
                }

                if (length <= session.ConsumedOffset)
                {
                    return true;
                }

                var start = session.ConsumedOffset;
                if (length - start > MaxReadBytes)
                {
                    start = length - MaxReadBytes;
                }

                var text = ReadCompletedLines(session, start, length, out var newOffset);
                session.ConsumedOffset = newOffset;
                if (string.IsNullOrEmpty(text))
                {
                    return true;
                }

                observations = Parse(session, text);
                return true;
            }
            catch (IOException)
            {
                // The file was locked mid-write; a later read retries.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ReadCompletedLines(
            RaEmulatorLogSession session,
            long start,
            long length,
            out long newOffset)
        {
            newOffset = session.ConsumedOffset;
            var count = (int)Math.Min(length - start, MaxReadBytes);
            var buffer = new byte[count];

            using (var stream = new FileStream(
                session.LogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(start, SeekOrigin.Begin);
                var read = 0;
                while (read < count)
                {
                    var chunk = stream.Read(buffer, read, count - read);
                    if (chunk == 0)
                    {
                        break;
                    }

                    read += chunk;
                }

                if (read == 0)
                {
                    return null;
                }

                var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                if (lastNewline < 0)
                {
                    // No complete line available yet; leave the offset so the partial line is re-read.
                    newOffset = start;
                    return null;
                }

                newOffset = start + lastNewline + 1;
                return Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            }
        }

        private static IReadOnlyList<AchievementProgressObservation> Parse(
            RaEmulatorLogSession session,
            string text)
        {
            var profile = session.Profile;
            List<AchievementProgressObservation> observations = null;
            HashSet<string> emitted = null;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                UpdateMode(session, profile, line);

                var awardMatch = profile.Award.Match(line);
                if (!awardMatch.Success)
                {
                    continue;
                }

                var achievementId = awardMatch.Groups[1].Value;
                if (!session.SchemaAchievementIds.Contains(achievementId))
                {
                    // Belongs to a different game (RetroAchievements ids are globally unique).
                    continue;
                }

                emitted = emitted ?? new HashSet<string>(StringComparer.Ordinal);
                if (!emitted.Add(achievementId))
                {
                    continue;
                }

                observations = observations ?? new List<AchievementProgressObservation>();
                observations.Add(new AchievementProgressObservation
                {
                    ApiName = achievementId,
                    Unlocked = true,
                    UnlockTimeUtc = DateTime.UtcNow,
                    UnlockMode = session.Hardcore ? "Hardcore" : "Softcore"
                });
            }

            return (IReadOnlyList<AchievementProgressObservation>)observations ??
                   Array.Empty<AchievementProgressObservation>();
        }

        private static void UpdateMode(
            RaEmulatorLogSession session,
            RaEmulatorLogParseProfile profile,
            string line)
        {
            foreach (var enabled in profile.HardcoreEnabled)
            {
                if (enabled.IsMatch(line))
                {
                    session.Hardcore = true;
                    return;
                }
            }

            if (profile.HardcoreDisabled.IsMatch(line))
            {
                session.Hardcore = false;
            }
        }
    }
}
