using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Recording
{
    /// <summary>
    /// Validates a user-supplied ffmpeg build: parses -version, probes -encoders for the H.264
    /// encoders the capture command can use, probes -filters for ddagrab, and optionally runs
    /// 1-second gdigrab/ddagrab smoke tests to the null muxer. Results are cached per path for
    /// the session; drives the settings Test button and the recording service's Auto encoder and
    /// capture backend selection.
    /// </summary>
    internal sealed class FfmpegValidationService
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

        private static readonly string[] KnownEncoders =
        {
            "h264_nvenc",
            "h264_qsv",
            "h264_amf",
            "libx264"
        };

        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, FfmpegValidationResult> _cache =
            new ConcurrentDictionary<string, FfmpegValidationResult>(StringComparer.OrdinalIgnoreCase);

        public FfmpegValidationService(ILogger logger)
        {
            _logger = logger;
        }

        public sealed class FfmpegValidationResult
        {
            public bool IsValid { get; set; }

            public string Version { get; set; }

            public IReadOnlyList<string> AvailableEncoders { get; set; } = new List<string>();

            /// <summary>
            /// True when the build's -filters output lists ddagrab (and, if the smoke test ran,
            /// a 1s ddagrab capture succeeded). Drives the Auto capture backend preferring
            /// Desktop Duplication over the cursor-flickering gdigrab.
            /// </summary>
            public bool SupportsDdagrab { get; set; }

            /// <summary>Diagnostic detail for the settings status line when invalid.</summary>
            public string Error { get; set; }

            /// <summary>True when the gdigrab smoke test ran as part of this result.</summary>
            public bool SmokeTested { get; set; }
        }

        /// <summary>
        /// Validates the ffmpeg at the given path. Cached per path per session; when the caller
        /// requests the smoke test and the cached result is probe-only, the validation is rerun
        /// once with the smoke test to upgrade the cache.
        /// </summary>
        public async Task<FfmpegValidationResult> ValidateAsync(string ffmpegPath, bool runSmokeTest = false)
        {
            var path = ffmpegPath?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new FfmpegValidationResult { IsValid = false, Error = "file not found" };
            }

            if (_cache.TryGetValue(path, out var cached) && (!runSmokeTest || cached.SmokeTested || !cached.IsValid))
            {
                return cached;
            }

            var result = await ProbeAsync(path, runSmokeTest).ConfigureAwait(false);
            _cache[path] = result;
            return result;
        }

        private async Task<FfmpegValidationResult> ProbeAsync(string path, bool runSmokeTest)
        {
            var result = new FfmpegValidationResult();

            var versionLines = await RunProbeAsync(path, RecordingCommandBuilder.VersionProbeArguments)
                .ConfigureAwait(false);
            result.Version = ParseVersion(versionLines);
            if (result.Version == null)
            {
                result.Error = "-version probe failed";
                return result;
            }

            var encoderLines = await RunProbeAsync(path, RecordingCommandBuilder.EncodersProbeArguments)
                .ConfigureAwait(false);
            result.AvailableEncoders = ParseEncoders(encoderLines);
            if (result.AvailableEncoders.Count == 0)
            {
                result.Error = "no usable H.264 encoder";
                return result;
            }

            // Best-effort: a failed filters probe just means no ddagrab preference, not an
            // invalid build.
            var filterLines = await RunProbeAsync(path, RecordingCommandBuilder.FiltersProbeArguments)
                .ConfigureAwait(false);
            result.SupportsDdagrab = ParseDdagrabSupport(filterLines);

            if (runSmokeTest)
            {
                result.SmokeTested = true;
                var smokeOk = await RunSmokeTestAsync(path, RecordingCommandBuilder.BuildSmokeTestArguments())
                    .ConfigureAwait(false);
                if (!smokeOk)
                {
                    result.Error = "screen capture test failed";
                    return result;
                }

                // Desktop Duplication can fail at runtime (RDP, hybrid GPUs) even when the
                // filter exists; a failed ddagrab test only drops the Auto preference.
                if (result.SupportsDdagrab &&
                    !await RunSmokeTestAsync(path, RecordingCommandBuilder.BuildDdagrabSmokeTestArguments())
                        .ConfigureAwait(false))
                {
                    result.SupportsDdagrab = false;
                }
            }

            result.IsValid = true;
            return result;
        }

        /// <summary>Runs one short-lived probe and returns its stdout lines (null on failure).</summary>
        private async Task<IReadOnlyList<string>> RunProbeAsync(string path, string arguments)
        {
            using (var host = new FfmpegProcessHost(path, arguments, _logger, captureStdOut: true))
            {
                if (!host.Start())
                {
                    return null;
                }

                var exitCode = await host.WaitForExitAsync(ProbeTimeout).ConfigureAwait(false);
                return exitCode == 0 ? host.StdOutLines : null;
            }
        }

        private async Task<bool> RunSmokeTestAsync(string path, string arguments)
        {
            using (var host = new FfmpegProcessHost(
                       path,
                       arguments,
                       _logger))
            {
                if (!host.Start())
                {
                    return false;
                }

                var exitCode = await host.WaitForExitAsync(ProbeTimeout).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    _logger?.Debug($"ffmpeg smoke test failed (exit={exitCode}): {host.StdErrTail}");
                    return false;
                }

                return true;
            }
        }

        internal static string ParseVersion(IReadOnlyList<string> stdOutLines)
        {
            var first = stdOutLines?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (first == null)
            {
                return null;
            }

            var match = Regex.Match(first, @"ffmpeg version (\S+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static IReadOnlyList<string> ParseEncoders(IReadOnlyList<string> stdOutLines)
        {
            if (stdOutLines == null)
            {
                return new List<string>();
            }

            return KnownEncoders
                .Where(encoder => stdOutLines.Any(line =>
                    line != null &&
                    Regex.IsMatch(line, $@"\b{Regex.Escape(encoder)}\b")))
                .ToList();
        }

        internal static bool ParseDdagrabSupport(IReadOnlyList<string> stdOutLines)
        {
            return stdOutLines != null &&
                   stdOutLines.Any(line => line != null && Regex.IsMatch(line, @"\bddagrab\b"));
        }
    }
}
