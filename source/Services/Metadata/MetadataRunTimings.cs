using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Where one overlay run spent its time, by step and by source, for the run's
    /// <c>[RefreshPerf]</c> line. Games are overlaid in parallel, so each step's time is summed
    /// across games and can exceed the run's own length.
    /// </summary>
    internal sealed class MetadataRunTimings
    {
        private readonly ConcurrentDictionary<string, long[]> _steps = new ConcurrentDictionary<string, long[]>();
        private int _games;

        public int Games => Volatile.Read(ref _games);

        public void CountGame() => Interlocked.Increment(ref _games);

        /// <summary>Adds the time since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/>) to the step.</summary>
        public void Add(string step, long startTimestamp)
        {
            var entry = _steps.GetOrAdd(step, _ => new long[2]);
            Interlocked.Add(ref entry[0], Stopwatch.GetTimestamp() - startTimestamp);
            Interlocked.Increment(ref entry[1]);
        }

        /// <summary>"games=12 load=3400ms/12 fetch:Exophase=2100ms/12 ...", steps in name order.</summary>
        public string Format()
        {
            var steps = _steps
                .OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
                .Select(pair => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}={1}ms/{2}",
                    pair.Key,
                    Volatile.Read(ref pair.Value[0]) * 1000 / Stopwatch.Frequency,
                    Volatile.Read(ref pair.Value[1])));
            return string.Join(" ", new[] { "games=" + Games.ToString(CultureInfo.InvariantCulture) }.Concat(steps));
        }
    }
}
