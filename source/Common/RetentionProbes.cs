using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// A surface that can report what it is holding, for the retention report.
    /// </summary>
    internal interface IRetentionProbe
    {
        /// <summary>Short prefix for this surface's fields in the [MemPerf] line.</summary>
        string RetentionProbeName { get; }

        /// <summary>Compact key=value occupancy detail, or empty when there is nothing to say.</summary>
        string DescribeRetention();
    }

    /// <summary>
    /// Lets a long-lived surface contribute its own occupancy figures to the retention report
    /// without the plugin having to reach into it. Registrations are weak, which is the whole
    /// point: a probe list that held its targets strongly would root the objects the report
    /// exists to catch, and the report would then prove only that it was itself the leak.
    /// Gated by <see cref="MemoryDiagnostics.Enabled"/>.
    /// </summary>
    internal static class RetentionProbes
    {
        private static readonly object Sync = new object();
        private static readonly List<WeakReference<IRetentionProbe>> Probes =
            new List<WeakReference<IRetentionProbe>>();

        public static void Register(IRetentionProbe probe)
        {
            if (!MemoryDiagnostics.Enabled || probe == null)
            {
                return;
            }

            lock (Sync)
            {
                Probes.RemoveAll(reference => !reference.TryGetTarget(out _));
                Probes.Add(new WeakReference<IRetentionProbe>(probe));
            }
        }

        public static void Unregister(IRetentionProbe probe)
        {
            if (probe == null)
            {
                return;
            }

            lock (Sync)
            {
                Probes.RemoveAll(reference =>
                    !reference.TryGetTarget(out var target) || ReferenceEquals(target, probe));
            }
        }

        /// <summary>
        /// Every live probe's detail, space separated. Includes a count of live probes per name,
        /// because more than one live instance of a surface that should be a singleton is itself
        /// the finding.
        /// </summary>
        public static string Describe()
        {
            if (!MemoryDiagnostics.Enabled)
            {
                return string.Empty;
            }

            var live = new List<IRetentionProbe>();
            lock (Sync)
            {
                Probes.RemoveAll(reference => !reference.TryGetTarget(out _));
                foreach (var reference in Probes)
                {
                    if (reference.TryGetTarget(out var target) && target != null)
                    {
                        live.Add(target);
                    }
                }
            }

            if (live.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var group in live.GroupBy(probe => SafeName(probe), StringComparer.Ordinal)
                                      .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                parts.Add($"{group.Key}Live={group.Count()}");
                foreach (var probe in group)
                {
                    var detail = SafeDescribe(probe);
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        parts.Add(detail.Trim());
                    }
                }
            }

            return string.Join(" ", parts);
        }

        private static string SafeName(IRetentionProbe probe)
        {
            try
            {
                var name = probe.RetentionProbeName;
                return string.IsNullOrWhiteSpace(name) ? "probe" : name.Trim();
            }
            catch
            {
                return "probe";
            }
        }

        // A probe is diagnostics: one that throws must not take the report down with it.
        private static string SafeDescribe(IRetentionProbe probe)
        {
            try
            {
                return probe.DescribeRetention() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
