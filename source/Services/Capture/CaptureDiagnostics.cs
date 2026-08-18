using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Capture
{
    /// <summary>
    /// Which layers of the unlock-clip capture pipeline are switched off for a diagnostic run.
    /// <para>
    /// A desktop-responsiveness cost was measured while <see cref="WgcVideoRecorder"/> runs that did
    /// not change between 30 and 60 fps, did not depend on which window had focus, and survived
    /// turning HDR off, suppressing the cursor, dropping MinUpdateInterval, leaving the GPU priority
    /// at the driver default, and turning both audio recorders off. A cost with that shape cannot come
    /// from how much work is done per frame, only from a mode being switched on — so what is needed is
    /// a way to run the recorder with progressively less of itself active.
    /// </para>
    /// <para>
    /// <see cref="DisableUpdateRateLimit"/> and <see cref="DisableGpuPriorityOverride"/> are single
    /// optimizations, neither required for correct capture, and OBS sets neither. The seven after them
    /// are a ladder: each removes the layer inside the one above it, so setting them from the top down
    /// runs capture with strictly less of the pipeline live. Every stage still starts, still logs and
    /// still stops cleanly; the recorder simply produces less, so no clip is written from stage 1 on.
    /// </para>
    /// <para>
    /// Ladder, outermost layer first, set cumulatively:
    /// 1 <see cref="DisableEncoding"/> — capture and compose, never encode. No Media Foundation sink
    /// writer, no encoder session, no segment file, no disk write, no per-segment writer build.
    /// 2 <see cref="DisableFrameComposition"/> — pull frames and drop them. No GPU draw is submitted.
    /// 3 <see cref="DisableFrameConsumption"/> — never pull a frame. The WGC session still runs.
    /// 4 <see cref="DisableCaptureSession"/> — no frame pool, no capture session, no StartCapture, no
    /// composer. The D3D11 device, the Media Foundation lease and the pump thread remain.
    /// 5 <see cref="DisableMediaFoundationLease"/> — no MFStartup held for the session.
    /// 6 <see cref="DisablePacerHighResolution"/> — the pump waits on Thread.Sleep instead of a
    /// high-resolution waitable timer.
    /// 7 <see cref="DisablePumpThread"/> — no pump thread. The D3D11 device alone remains.
    /// </para>
    /// <para>
    /// Three switches sit outside the ladder. <see cref="DisableBorderSuppression"/> leaves Windows'
    /// capture border on and skips the one-time borderless-access request, the remaining WGC session
    /// mode the recorder changes. <see cref="CaptureNonGameWindow"/> keeps the whole pipeline running
    /// but points it at a window that is not the game, which separates "holding a capture session
    /// costs something" from "capturing the game's presentation costs something" — the latter being
    /// what demoting a fullscreen game's flip chain to composed presentation would look like.
    /// <see cref="FramePoolBufferCount"/> is the frame pool's buffer count; the pool is built at the
    /// window's size against a producer running far faster than the pump consumes, so it is saturated
    /// essentially always, and whether that costs the producer anything is unverified.
    /// </para>
    /// <para>
    /// A stage is normally selected by <see cref="FileName"/> in the plugin's user data folder rather
    /// than by rebuilding: one entry per line, case-insensitive, blank lines and lines starting with
    /// '#' ignored. A bare name turns a switch on (<c>DisableEncoding</c>); the <c>Name=Value</c> form
    /// carries a value (<c>FramePoolBufferCount=4</c>). It is read fresh every time a capture starts,
    /// so a stage costs a game relaunch instead of a rebuild or a Playnite restart. The compiled-in
    /// defaults below stay available for hard-setting a switch in a build, and the file is applied on
    /// top of them.
    /// </para>
    /// </summary>
    internal sealed class CaptureDiagnostics
    {
        /// <summary>The switch file, read from the plugin's user data folder.</summary>
        public const string FileName = "capture-diagnostics.txt";

        // Compiled-in defaults. All false is the shipping pipeline; a developer can hard-set one here
        // without needing the file.
        private const bool DisableUpdateRateLimitDefault = false;
        private const bool DisableGpuPriorityOverrideDefault = false;
        private const bool DisableEncodingDefault = false;
        private const bool DisableFrameCompositionDefault = false;
        private const bool DisableFrameConsumptionDefault = false;
        private const bool DisableCaptureSessionDefault = false;
        private const bool DisableMediaFoundationLeaseDefault = false;
        private const bool DisablePacerHighResolutionDefault = false;
        private const bool DisablePumpThreadDefault = false;
        private const bool DisableBorderSuppressionDefault = false;
        private const bool CaptureNonGameWindowDefault = false;

        /// <summary>
        /// Frame pool buffers. Two is what the recorder has always used and what OBS uses; the range
        /// exists only so the probe can raise it, since a pool below two cannot double-buffer at all
        /// and a large one only holds more stale frames.
        /// </summary>
        private const int FramePoolBufferCountDefault = 2;
        private const int MinFramePoolBuffers = 2;
        private const int MaxFramePoolBuffers = 6;

        /// <summary>The defaults alone: what every capture uses when no switch file exists.</summary>
        public static readonly CaptureDiagnostics CompiledIn = FromDefaults();

        private int _defaultCount;
        private int _fileCount;

        private CaptureDiagnostics()
        {
        }

        public bool DisableUpdateRateLimit { get; private set; }
        public bool DisableGpuPriorityOverride { get; private set; }
        public bool DisableEncoding { get; private set; }
        public bool DisableFrameComposition { get; private set; }
        public bool DisableFrameConsumption { get; private set; }
        public bool DisableCaptureSession { get; private set; }
        public bool DisableMediaFoundationLease { get; private set; }
        public bool DisablePacerHighResolution { get; private set; }
        public bool DisablePumpThread { get; private set; }
        public bool DisableBorderSuppression { get; private set; }
        public bool CaptureNonGameWindow { get; private set; }
        public int FramePoolBufferCount { get; private set; }

        /// <summary>
        /// The switch set for one capture session: the compiled-in defaults, OR'd with the names
        /// listed in <see cref="FileName"/> under <paramref name="directory"/>.
        /// <para>
        /// A missing file — the normal case — an unreadable one, and contents naming nothing
        /// recognizable all resolve to the defaults, so a user who has never created the file cannot
        /// be affected by it and no capture can be lost to it.
        /// </para>
        /// </summary>
        public static CaptureDiagnostics Resolve(string directory, ILogger logger)
        {
            var resolved = FromDefaults();
            var entries = ReadEntries(directory, logger);
            if (entries == null || entries.Count == 0)
            {
                return resolved;
            }

            var unrecognized = new List<string>();
            foreach (var entry in entries)
            {
                // An '=' splits a valued entry from a bare switch name. Only the first one splits, so
                // a value may itself contain one.
                var separator = entry.IndexOf('=');
                var name = separator < 0 ? entry : entry.Substring(0, separator).Trim();
                var value = separator < 0 ? null : entry.Substring(separator + 1).Trim();
                if (resolved.TryApply(name, value))
                {
                    resolved._fileCount++;
                }
                else
                {
                    unrecognized.Add(entry);
                }
            }

            if (unrecognized.Count > 0)
            {
                // A misspelled switch name would otherwise read as a stage that changed nothing,
                // which costs a whole game launch to discover.
                logger?.Warn(
                    $"[Recording] {FileName} names {unrecognized.Count} switch(es) that do not exist " +
                    $"and were ignored: {string.Join(", ", unrecognized)}.");
            }

            return resolved;
        }

        /// <summary>
        /// The switches that are set and where they came from, for the capture start line. Empty for
        /// the shipping pipeline, so a log carrying a stage is unmistakable.
        /// </summary>
        public string Describe()
        {
            var stages =
                Named(DisableUpdateRateLimit, "DisableUpdateRateLimit") +
                Named(DisableGpuPriorityOverride, "DisableGpuPriorityOverride") +
                Named(DisableEncoding, "DisableEncoding") +
                Named(DisableFrameComposition, "DisableFrameComposition") +
                Named(DisableFrameConsumption, "DisableFrameConsumption") +
                Named(DisableCaptureSession, "DisableCaptureSession") +
                Named(DisableMediaFoundationLease, "DisableMediaFoundationLease") +
                Named(DisablePacerHighResolution, "DisablePacerHighResolution") +
                Named(DisablePumpThread, "DisablePumpThread") +
                Named(DisableBorderSuppression, "DisableBorderSuppression");
            // The probes that change what capture does rather than removing a layer, so the line does
            // not describe them as disabled.
            var set =
                Named(CaptureNonGameWindow, "CaptureNonGameWindow") +
                (FramePoolBufferCount == FramePoolBufferCountDefault
                    ? string.Empty
                    : " FramePoolBufferCount=" + FramePoolBufferCount.ToString(CultureInfo.InvariantCulture));
            if (stages.Length == 0 && set.Length == 0)
            {
                return string.Empty;
            }

            string source;
            if (_fileCount > 0 && _defaultCount > 0)
            {
                source = FileName + " and compiled-in defaults";
            }
            else if (_fileCount > 0)
            {
                source = FileName;
            }
            else
            {
                source = "compiled-in defaults";
            }

            var described = stages.Length == 0
                ? string.Empty
                : $" Diagnostic switches disabling:{stages}.";
            if (set.Length > 0)
            {
                described += $" Diagnostic switches set:{set}.";
            }

            return $"{described} Source: {source}.";
        }

        private static CaptureDiagnostics FromDefaults()
        {
            var diagnostics = new CaptureDiagnostics
            {
                DisableUpdateRateLimit = DisableUpdateRateLimitDefault,
                DisableGpuPriorityOverride = DisableGpuPriorityOverrideDefault,
                DisableEncoding = DisableEncodingDefault,
                DisableFrameComposition = DisableFrameCompositionDefault,
                DisableFrameConsumption = DisableFrameConsumptionDefault,
                DisableCaptureSession = DisableCaptureSessionDefault,
                DisableMediaFoundationLease = DisableMediaFoundationLeaseDefault,
                DisablePacerHighResolution = DisablePacerHighResolutionDefault,
                DisablePumpThread = DisablePumpThreadDefault,
                DisableBorderSuppression = DisableBorderSuppressionDefault,
                CaptureNonGameWindow = CaptureNonGameWindowDefault,
                FramePoolBufferCount = FramePoolBufferCountDefault,
            };
            diagnostics._defaultCount =
                Count(DisableUpdateRateLimitDefault) +
                Count(DisableGpuPriorityOverrideDefault) +
                Count(DisableEncodingDefault) +
                Count(DisableFrameCompositionDefault) +
                Count(DisableFrameConsumptionDefault) +
                Count(DisableCaptureSessionDefault) +
                Count(DisableMediaFoundationLeaseDefault) +
                Count(DisablePacerHighResolutionDefault) +
                Count(DisablePumpThreadDefault) +
                Count(DisableBorderSuppressionDefault) +
                Count(CaptureNonGameWindowDefault);
            return diagnostics;
        }

        /// <summary>
        /// Reads the entries out of the file, or null when there is nothing to read. Never throws: an
        /// unreadable, locked or malformed file leaves the capture exactly as it ships.
        /// </summary>
        private static List<string> ReadEntries(string directory, ILogger logger)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return null;
                }

                var path = Path.Combine(directory, FileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                var names = new List<string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    names.Add(trimmed);
                }

                return names;
            }
            catch (Exception ex)
            {
                logger?.Debug(
                    ex, $"[Recording] Could not read {FileName}; capture uses its compiled-in defaults.");
                return null;
            }
        }

        private bool TryApply(string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "disableupdateratelimit":
                    DisableUpdateRateLimit = Flag(value);
                    return true;
                case "disablegpupriorityoverride":
                    DisableGpuPriorityOverride = Flag(value);
                    return true;
                case "disableencoding":
                    DisableEncoding = Flag(value);
                    return true;
                case "disableframecomposition":
                    DisableFrameComposition = Flag(value);
                    return true;
                case "disableframeconsumption":
                    DisableFrameConsumption = Flag(value);
                    return true;
                case "disablecapturesession":
                    DisableCaptureSession = Flag(value);
                    return true;
                case "disablemediafoundationlease":
                    DisableMediaFoundationLease = Flag(value);
                    return true;
                case "disablepacerhighresolution":
                    DisablePacerHighResolution = Flag(value);
                    return true;
                case "disablepumpthread":
                    DisablePumpThread = Flag(value);
                    return true;
                case "disablebordersuppression":
                    DisableBorderSuppression = Flag(value);
                    return true;
                case "capturenongamewindow":
                    CaptureNonGameWindow = Flag(value);
                    return true;
                case "framepoolbuffercount":
                    FramePoolBufferCount = BufferCount(value);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A switch line's value. Writing the line at all is what turns the switch on, so no value and
        /// an unreadable value both mean on; only an explicit <c>false</c> turns it back off, which is
        /// what lets a line be disarmed without deleting it.
        /// </summary>
        private static bool Flag(string value)
        {
            return value == null || !bool.TryParse(value, out var parsed) || parsed;
        }

        /// <summary>
        /// A frame pool buffer count, or the shipping default for anything unparseable or outside the
        /// supported range — the same fail-open rule the rest of the file follows.
        /// </summary>
        private static int BufferCount(string value)
        {
            return value != null &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= MinFramePoolBuffers &&
                parsed <= MaxFramePoolBuffers
                ? parsed
                : FramePoolBufferCountDefault;
        }

        private static int Count(bool value)
        {
            return value ? 1 : 0;
        }

        private static string Named(bool disabled, string name)
        {
            return disabled ? " " + name : string.Empty;
        }
    }
}
