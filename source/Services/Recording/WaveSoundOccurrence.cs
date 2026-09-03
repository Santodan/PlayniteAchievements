using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// One sound UniPlaySong launched for one toast wave. A wave may own several achievement
    /// requests, but it still produces exactly one live sound and therefore exactly one occurrence.
    /// Identity is deliberately independent of UTC ticks: two launches may share the same clock
    /// value and repeated use of the same WAV is ordinary.
    /// </summary>
    internal sealed class WaveSoundOccurrence
    {
        private long _endUtcTicks;

        internal WaveSoundOccurrence(
            Guid sessionId,
            Guid occurrenceId,
            long sequence,
            DateTime launchUtc,
            DateTime endUtc,
            double referenceLeadSeconds,
            IEnumerable<Guid> ownerCorrelationIds,
            string soundFilePath,
            double? soundFileGain,
            int? soundAlignmentDelayMs,
            double timelinePaddingSeconds)
        {
            SessionId = sessionId;
            OccurrenceId = occurrenceId;
            Sequence = sequence;
            LaunchUtc = AsUtc(launchUtc);
            var normalizedEnd = AsUtc(endUtc);
            _endUtcTicks = (normalizedEnd < LaunchUtc ? LaunchUtc : normalizedEnd).Ticks;
            ReferenceLeadSeconds = Math.Max(0, referenceLeadSeconds);
            TimelinePaddingSeconds = Math.Max(0, timelinePaddingSeconds);
            OwnerCorrelationIds = (ownerCorrelationIds ?? Enumerable.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            SoundFilePath = soundFilePath;
            SoundFileGain = soundFileGain;
            SoundAlignmentDelayMs = soundAlignmentDelayMs;
        }

        public Guid SessionId { get; }

        public Guid OccurrenceId { get; }

        public long Sequence { get; }

        public DateTime LaunchUtc { get; }

        /// <summary>
        /// Exclusive end of the live playback span that matters to capture. Starting a later UPS
        /// sound shortens this value because UPS disposes its previous external player first.
        /// </summary>
        public DateTime EndUtc => new DateTime(
            Interlocked.Read(ref _endUtcTicks), DateTimeKind.Utc);

        public double ReferenceLeadSeconds { get; }

        public double TimelinePaddingSeconds { get; }

        public DateTime RemovalStartUtc =>
            LaunchUtc.AddSeconds(-(ReferenceLeadSeconds + TimelinePaddingSeconds));

        public DateTime RemovalEndUtc => EndUtc.AddSeconds(TimelinePaddingSeconds);

        public IReadOnlyList<Guid> OwnerCorrelationIds { get; }

        public string SoundFilePath { get; }

        public double? SoundFileGain { get; }

        public int? SoundAlignmentDelayMs { get; }

        public bool Owns(Guid correlationId)
        {
            return correlationId != Guid.Empty && OwnerCorrelationIds.Contains(correlationId);
        }

        public bool Overlaps(DateTime startUtc, DateTime endUtc)
        {
            startUtc = AsUtc(startUtc);
            endUtc = AsUtc(endUtc);
            return RemovalStartUtc < endUtc && RemovalEndUtc > startUtc;
        }

        internal void StopAt(DateTime utc)
        {
            utc = AsUtc(utc);
            var target = (utc < LaunchUtc ? LaunchUtc : utc).Ticks;
            while (true)
            {
                var current = Interlocked.Read(ref _endUtcTicks);
                if (target >= current ||
                    Interlocked.CompareExchange(ref _endUtcTicks, target, current) == current)
                {
                    return;
                }
            }
        }

        internal void SetEndAt(DateTime utc)
        {
            utc = AsUtc(utc);
            var target = (utc < LaunchUtc ? LaunchUtc : utc).Ticks;
            Interlocked.Exchange(ref _endUtcTicks, target);
        }

        private static DateTime AsUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }
    }

    /// <summary>A maximal set of occurrence cleanup windows that overlap in time.</summary>
    internal sealed class WaveSoundCleanupCluster
    {
        public WaveSoundCleanupCluster(IReadOnlyList<WaveSoundOccurrence> occurrences)
        {
            if (occurrences == null || occurrences.Count == 0)
            {
                throw new ArgumentException("A cleanup cluster needs at least one occurrence.", nameof(occurrences));
            }

            Occurrences = occurrences.OrderBy(o => o.Sequence).ToArray();
            CacheKey = string.Join("-", Occurrences.Select(o => o.OccurrenceId.ToString("N")));
        }

        public IReadOnlyList<WaveSoundOccurrence> Occurrences { get; }

        public DateTime StartUtc => Occurrences.Min(o => o.RemovalStartUtc);

        public DateTime EndUtc => Occurrences.Max(o => o.RemovalEndUtc);

        public string CacheKey { get; }

        public bool Overlaps(DateTime startUtc, DateTime endUtc)
        {
            return StartUtc < endUtc && EndUtc > startUtc;
        }
    }

    /// <summary>
    /// Session-scoped occurrence history and overlap planner. It replaces the timestamp-only
    /// dictionaries that conflated simultaneous/repeated sounds and silently discarded the 65th
    /// launch. The owner decides retention from the rolling-buffer horizon.
    /// </summary>
    internal sealed class WaveSoundOccurrenceRegistry
    {
        private readonly object _gate = new object();
        private readonly List<WaveSoundOccurrence> _items = new List<WaveSoundOccurrence>();
        private long _nextSequence;

        public WaveSoundOccurrence Register(
            Guid sessionId,
            Guid occurrenceId,
            DateTime launchUtc,
            double expectedPlaybackSeconds,
            double referenceLeadSeconds,
            IEnumerable<Guid> ownerCorrelationIds,
            string soundFilePath,
            double? soundFileGain,
            int? soundAlignmentDelayMs,
            double timelinePaddingSeconds = 0)
        {
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException("A sound occurrence must belong to a capture session.", nameof(sessionId));
            }

            if (occurrenceId == Guid.Empty)
            {
                occurrenceId = Guid.NewGuid();
            }

            launchUtc = launchUtc.Kind == DateTimeKind.Utc ? launchUtc : launchUtc.ToUniversalTime();
            var endUtc = launchUtc.AddSeconds(Math.Max(0.05, expectedPlaybackSeconds));

            lock (_gate)
            {
                var duplicate = _items.FirstOrDefault(i =>
                    i.SessionId == sessionId && i.OccurrenceId == occurrenceId);
                if (duplicate != null)
                {
                    return duplicate;
                }

                // UniPlaySong owns one external jingle player. A new launch disposes the previous
                // player, so the earlier occurrence cannot contribute samples after this instant.
                var previous = _items
                    .Where(i => i.SessionId == sessionId && i.LaunchUtc <= launchUtc)
                    .OrderByDescending(i => i.Sequence)
                    .FirstOrDefault();
                previous?.StopAt(launchUtc);

                // UI delivery is normally ordered, but session shutdown and continuations can
                // interleave. If an older stamped launch arrives after a newer one, that newer
                // launch still marks the moment UPS disposed this older player.
                var next = _items
                    .Where(i => i.SessionId == sessionId && i.LaunchUtc > launchUtc)
                    .OrderBy(i => i.LaunchUtc)
                    .ThenBy(i => i.Sequence)
                    .FirstOrDefault();
                if (next != null && next.LaunchUtc < endUtc)
                {
                    endUtc = next.LaunchUtc;
                }

                var occurrence = new WaveSoundOccurrence(
                    sessionId,
                    occurrenceId,
                    ++_nextSequence,
                    launchUtc,
                    endUtc,
                    referenceLeadSeconds,
                    ownerCorrelationIds,
                    soundFilePath,
                    soundFileGain,
                    soundAlignmentDelayMs,
                    timelinePaddingSeconds);
                _items.Add(occurrence);
                return occurrence;
            }
        }

        public WaveSoundOccurrence FindOwned(Guid sessionId, Guid correlationId)
        {
            lock (_gate)
            {
                return _items
                    .Where(i => i.SessionId == sessionId && i.Owns(correlationId))
                    .OrderByDescending(i => i.Sequence)
                    .FirstOrDefault();
            }
        }

        public WaveSoundOccurrence Find(Guid sessionId, Guid occurrenceId)
        {
            if (occurrenceId == Guid.Empty)
            {
                return null;
            }

            lock (_gate)
            {
                return _items.FirstOrDefault(i =>
                    i.SessionId == sessionId && i.OccurrenceId == occurrenceId);
            }
        }

        /// <summary>
        /// Replaces the provisional toast-based playback estimate with the resolved file's real
        /// duration. A later registered UPS launch remains the hard ceiling because it disposes
        /// the previous external player.
        /// </summary>
        public void SetNaturalPlaybackSeconds(
            Guid sessionId,
            Guid occurrenceId,
            double playbackSeconds)
        {
            if (occurrenceId == Guid.Empty || double.IsNaN(playbackSeconds) ||
                double.IsInfinity(playbackSeconds) || playbackSeconds <= 0)
            {
                return;
            }

            lock (_gate)
            {
                var occurrence = _items.FirstOrDefault(i =>
                    i.SessionId == sessionId && i.OccurrenceId == occurrenceId);
                if (occurrence == null)
                {
                    return;
                }

                var endUtc = occurrence.LaunchUtc.AddSeconds(playbackSeconds);
                var next = _items
                    .Where(i =>
                        i.SessionId == sessionId &&
                        (i.LaunchUtc > occurrence.LaunchUtc ||
                         (i.LaunchUtc == occurrence.LaunchUtc &&
                          i.Sequence > occurrence.Sequence)))
                    .OrderBy(i => i.LaunchUtc)
                    .ThenBy(i => i.Sequence)
                    .FirstOrDefault();
                if (next != null && next.LaunchUtc < endUtc)
                {
                    endUtc = next.LaunchUtc;
                }

                occurrence.SetEndAt(endUtc);
            }
        }

        public IReadOnlyList<WaveSoundOccurrence> GetOverlapping(
            Guid sessionId,
            DateTime startUtc,
            DateTime endUtc)
        {
            lock (_gate)
            {
                return _items
                    .Where(i => i.SessionId == sessionId && i.Overlaps(startUtc, endUtc))
                    .OrderBy(i => i.Sequence)
                    .ToArray();
            }
        }

        public IReadOnlyList<WaveSoundCleanupCluster> GetOverlappingClusters(
            Guid sessionId,
            DateTime startUtc,
            DateTime endUtc)
        {
            WaveSoundOccurrence[] sessionItems;
            lock (_gate)
            {
                sessionItems = _items
                    .Where(i => i.SessionId == sessionId)
                    .OrderBy(i => i.RemovalStartUtc)
                    .ThenBy(i => i.Sequence)
                    .ToArray();
            }

            var clusters = new List<WaveSoundCleanupCluster>();
            var current = new List<WaveSoundOccurrence>();
            var currentEnd = DateTime.MinValue;
            foreach (var item in sessionItems)
            {
                if (current.Count == 0 || item.RemovalStartUtc <= currentEnd)
                {
                    current.Add(item);
                    if (item.RemovalEndUtc > currentEnd)
                    {
                        currentEnd = item.RemovalEndUtc;
                    }

                    continue;
                }

                var completed = new WaveSoundCleanupCluster(current.ToArray());
                if (completed.Overlaps(startUtc, endUtc))
                {
                    clusters.Add(completed);
                }

                current.Clear();
                current.Add(item);
                currentEnd = item.RemovalEndUtc;
            }

            if (current.Count > 0)
            {
                var completed = new WaveSoundCleanupCluster(current.ToArray());
                if (completed.Overlaps(startUtc, endUtc))
                {
                    clusters.Add(completed);
                }
            }

            return clusters;
        }

        public void PruneSessionBefore(Guid sessionId, DateTime utc)
        {
            lock (_gate)
            {
                _items.RemoveAll(i => i.SessionId == sessionId && i.RemovalEndUtc < utc);
            }
        }

        public void RemoveSession(Guid sessionId)
        {
            lock (_gate)
            {
                _items.RemoveAll(i => i.SessionId == sessionId);
            }
        }
    }
}
