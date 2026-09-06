using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Where an overlay re-encode can stream-copy the base clip's compressed video instead of
    /// re-encoding it. The base clip is H.264 with a keyframe about every second, and only the
    /// frames an overlay changes need re-encoding, so the rest could be copied — but a copied run
    /// has to be whole GOPs, beginning on a keyframe and ending just before one, while the clip
    /// window begins wherever the unlock put it, up to a second after the base clip's opening
    /// keyframe. The output is therefore an alternating sequence of runs on the base clip's
    /// timeline: each changed interval widens to the keyframes around it and is re-encoded, the
    /// head from the window start to the first keyframe is re-encoded, and whatever lies between
    /// is copied. Copied runs too short to pay for the encoder setup a split costs are absorbed
    /// into their re-encoded neighbours. Pure arithmetic over the sample list, kept free of Media
    /// Foundation so it unit-tests directly.
    /// </summary>
    internal sealed class OverlaySplicePlan
    {
        /// <summary>One compressed video sample of the base clip, in 100-ns ticks.</summary>
        public struct SampleInfo
        {
            public long Time;
            public long Duration;
            public bool IsKeyframe;
        }

        /// <summary>Base-clip ticks over which an overlay changes frames, both ends inclusive.</summary>
        public struct Interval
        {
            public Interval(long start, long end)
            {
                Start = start;
                End = end;
            }

            public long Start { get; }

            public long End { get; }
        }

        public enum RunKind
        {
            /// <summary>Compressed samples pass through untouched; begins on a keyframe.</summary>
            Copy,

            /// <summary>Frames decode, take their overlays, and re-encode into their own file.</summary>
            Reencode,
        }

        /// <summary>One stretch of the output, [<see cref="Start"/>, <see cref="End"/>) in base-clip ticks.</summary>
        public sealed class Run
        {
            public Run(RunKind kind, long start, long end)
            {
                Kind = kind;
                Start = start;
                End = end;
            }

            public RunKind Kind { get; }

            public long Start { get; }

            /// <summary>Exclusive.</summary>
            public long End { get; internal set; }

            public int Frames { get; internal set; }

            public long Ticks => End - Start;
        }

        private OverlaySplicePlan(List<Run> runs, long start, long end)
        {
            Runs = runs;
            Start = start;
            End = end;
        }

        /// <summary>The runs in output order, alternating in kind.</summary>
        public IReadOnlyList<Run> Runs { get; }

        /// <summary>Base-clip time the output starts at: the keyframe lead being trimmed off.</summary>
        public long Start { get; }

        /// <summary>Exclusive base-clip end of the output.</summary>
        public long End { get; }

        public int CopyFrames
        {
            get
            {
                var frames = 0;
                foreach (var run in Runs)
                {
                    if (run.Kind == RunKind.Copy)
                    {
                        frames += run.Frames;
                    }
                }

                return frames;
            }
        }

        public int ReencodeFrames
        {
            get
            {
                var frames = 0;
                foreach (var run in Runs)
                {
                    if (run.Kind == RunKind.Reencode)
                    {
                        frames += run.Frames;
                    }
                }

                return frames;
            }
        }

        public int ReencodeRuns
        {
            get
            {
                var count = 0;
                foreach (var run in Runs)
                {
                    if (run.Kind == RunKind.Reencode)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public long CopyTicks
        {
            get
            {
                var ticks = 0L;
                foreach (var run in Runs)
                {
                    if (run.Kind == RunKind.Copy)
                    {
                        ticks += run.Ticks;
                    }
                }

                return ticks;
            }
        }

        /// <summary>
        /// Plans the runs, or returns null when nothing is worth copying: no keyframe after the
        /// lead, every copied run shorter than <paramref name="minCopyTicks"/> once absorbed, or
        /// samples out of order (a stream with reordered frames cannot be cut by sample time).
        /// Times are base-clip ticks; <paramref name="endInclusive"/> is the last time kept,
        /// matching the whole-clip pass's cut. Changed intervals may overlap and arrive in any
        /// order; ones outside the kept window are ignored.
        /// </summary>
        public static OverlaySplicePlan TryPlan(
            IReadOnlyList<SampleInfo> samples, long trimLead, IReadOnlyList<Interval> changed,
            long endInclusive, long minCopyTicks)
        {
            if (samples == null || samples.Count == 0 || trimLead < 0 || endInclusive < trimLead)
            {
                return null;
            }

            var keyframes = new List<long>();
            var previous = long.MinValue;
            foreach (var sample in samples)
            {
                if (sample.Time < previous)
                {
                    return null;
                }

                previous = sample.Time;
                if (sample.IsKeyframe)
                {
                    keyframes.Add(sample.Time);
                }
            }

            var end = endInclusive + 1;
            var firstKeyframe = FirstKeyframeAtOrAfter(keyframes, trimLead);
            if (firstKeyframe < 0 || firstKeyframe >= end)
            {
                return null;
            }

            // Every stretch that must be re-encoded, widened to GOP bounds: from the last keyframe
            // at or before the change (or the window start, when the change begins in the head GOP)
            // to the first keyframe after it (or the end).
            var reencode = new List<Run>();
            if (firstKeyframe > trimLead)
            {
                reencode.Add(new Run(RunKind.Reencode, trimLead, firstKeyframe));
            }

            if (changed != null)
            {
                foreach (var interval in changed)
                {
                    if (interval.End < interval.Start || interval.End < trimLead || interval.Start > endInclusive)
                    {
                        continue;
                    }

                    var from = LastKeyframeAtOrBefore(keyframes, Math.Max(interval.Start, trimLead));
                    if (from < trimLead)
                    {
                        from = trimLead;
                    }

                    var to = FirstKeyframeAfter(keyframes, interval.End);
                    if (to < 0 || to > end)
                    {
                        to = end;
                    }

                    reencode.Add(new Run(RunKind.Reencode, from, to));
                }
            }

            reencode.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<Run>();
            foreach (var run in reencode)
            {
                if (merged.Count > 0 && run.Start <= merged[merged.Count - 1].End)
                {
                    merged[merged.Count - 1].End = Math.Max(merged[merged.Count - 1].End, run.End);
                }
                else
                {
                    merged.Add(run);
                }
            }

            // Fill the gaps with copied runs, then fold short copies into their neighbours.
            var runs = new List<Run>();
            var cursor = trimLead;
            foreach (var run in merged)
            {
                if (run.Start > cursor)
                {
                    runs.Add(new Run(RunKind.Copy, cursor, run.Start));
                }

                runs.Add(run);
                cursor = run.End;
            }

            if (cursor < end)
            {
                runs.Add(new Run(RunKind.Copy, cursor, end));
            }

            runs = AbsorbShortCopies(runs, minCopyTicks);
            var plan = new OverlaySplicePlan(runs, trimLead, end);
            if (plan.CountFrames(samples) == 0)
            {
                return null;
            }

            return plan;
        }

        /// <summary>
        /// Merges copied runs shorter than the minimum into the re-encoded runs around them: a split
        /// costs an encoder setup and a finalize, which a short copy does not repay. The result still
        /// alternates in kind.
        /// </summary>
        private static List<Run> AbsorbShortCopies(List<Run> runs, long minCopyTicks)
        {
            var result = new List<Run>();
            foreach (var run in runs)
            {
                var last = result.Count > 0 ? result[result.Count - 1] : null;
                if (run.Kind == RunKind.Copy && run.Ticks < minCopyTicks)
                {
                    if (last != null)
                    {
                        // Extend the preceding re-encode over it; a following re-encode then
                        // merges into that same run below.
                        last.End = run.End;
                    }
                    else
                    {
                        result.Add(new Run(RunKind.Reencode, run.Start, run.End));
                    }

                    continue;
                }

                if (last != null && last.Kind == run.Kind)
                {
                    last.End = run.End;
                    continue;
                }

                result.Add(run);
            }

            return result;
        }

        /// <summary>Counts each run's frames; returns how many were copied.</summary>
        private int CountFrames(IReadOnlyList<SampleInfo> samples)
        {
            var index = 0;
            foreach (var sample in samples)
            {
                if (sample.Time < Start || sample.Time >= End)
                {
                    continue;
                }

                while (index < Runs.Count && sample.Time >= Runs[index].End)
                {
                    index++;
                }

                if (index < Runs.Count)
                {
                    Runs[index].Frames++;
                }
            }

            return CopyFrames;
        }

        private static long FirstKeyframeAtOrAfter(List<long> keyframes, long time)
        {
            foreach (var keyframe in keyframes)
            {
                if (keyframe >= time)
                {
                    return keyframe;
                }
            }

            return -1;
        }

        private static long FirstKeyframeAfter(List<long> keyframes, long time)
        {
            foreach (var keyframe in keyframes)
            {
                if (keyframe > time)
                {
                    return keyframe;
                }
            }

            return -1;
        }

        private static long LastKeyframeAtOrBefore(List<long> keyframes, long time)
        {
            var found = -1L;
            foreach (var keyframe in keyframes)
            {
                if (keyframe > time)
                {
                    break;
                }

                found = keyframe;
            }

            return found;
        }
    }
}
