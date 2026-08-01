using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

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

        /// <summary>Hardware encoders that have a GPU-resident (no-hwdownload) capture path.</summary>
        private static readonly RecordingEncoder[] GpuCaptureEncoders =
        {
            RecordingEncoder.Nvenc,
            RecordingEncoder.Amf,
            RecordingEncoder.Qsv
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

            /// <summary>
            /// Per hardware encoder, true when the build can feed ddagrab's D3D11 frames straight
            /// into that encoder (the encoder is present and ddagrab is usable) and, if the smoke
            /// test ran, a 1s ddagrab-&gt;encoder run succeeded. Drives the recording service's
            /// choice to keep frames on the GPU instead of the hwdownload round trip.
            /// </summary>
            public bool SupportsNvencGpuCapture { get; set; }

            public bool SupportsAmfGpuCapture { get; set; }

            public bool SupportsQsvGpuCapture { get; set; }

            /// <summary>
            /// Per hardware encoder, true when a source-agnostic test encode actually succeeded (or,
            /// before the smoke test runs, when the encoder is merely present). A false flag after a
            /// smoke test means the encoder is compiled into the build but its GPU/driver rejects the
            /// encode — the case the -encoders text list can't see.
            /// </summary>
            public bool SupportsNvencEncode { get; set; }

            public bool SupportsAmfEncode { get; set; }

            public bool SupportsQsvEncode { get; set; }

            /// <summary>The codec of the first encoder whose test encode failed, null when none did.</summary>
            public string FailedEncoderCodec { get; set; }

            /// <summary>The ffmpeg stderr tail from that first failing test encode (the driver error).</summary>
            public string EncoderProbeError { get; set; }

            /// <summary>GPU-resident capture support for a specific resolved encoder.</summary>
            public bool SupportsGpuCapture(RecordingEncoder encoder)
            {
                switch (encoder)
                {
                    case RecordingEncoder.Nvenc:
                        return SupportsNvencGpuCapture;
                    case RecordingEncoder.Amf:
                        return SupportsAmfGpuCapture;
                    case RecordingEncoder.Qsv:
                        return SupportsQsvGpuCapture;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Whether the given encoder can actually encode: the smoke-tested per-encoder flag for
            /// hardware encoders, presence for libx264 (a CPU encoder with no driver dependency), and
            /// true for Auto (it resolves to whatever is usable).
            /// </summary>
            public bool CanEncode(RecordingEncoder encoder)
            {
                switch (encoder)
                {
                    case RecordingEncoder.Nvenc:
                        return SupportsNvencEncode;
                    case RecordingEncoder.Amf:
                        return SupportsAmfEncode;
                    case RecordingEncoder.Qsv:
                        return SupportsQsvEncode;
                    case RecordingEncoder.X264:
                        return AvailableEncoders.Contains("libx264");
                    default:
                        return true;
                }
            }

            /// <summary>The subset of <see cref="AvailableEncoders"/> that actually encode.</summary>
            public IReadOnlyList<string> UsableEncoders =>
                AvailableEncoders.Where(IsCodecUsable).ToList();

            private bool IsCodecUsable(string codec)
            {
                switch (codec)
                {
                    case "h264_nvenc":
                        return SupportsNvencEncode;
                    case "h264_amf":
                        return SupportsAmfEncode;
                    case "h264_qsv":
                        return SupportsQsvEncode;
                    default:
                        return true;
                }
            }

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
            DeriveGpuCaptureSupport(result);
            DeriveEncodeSupport(result);

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

                // ddagrab may have just been disabled; re-derive per-encoder support, then confirm
                // each supported ddagrab->encoder chain actually runs on this build and hardware
                // (filter/encoder presence alone can pass on setups where the direct feed fails).
                DeriveGpuCaptureSupport(result);
                foreach (var encoder in GpuCaptureEncoders)
                {
                    if (result.SupportsGpuCapture(encoder) &&
                        !await RunSmokeTestAsync(path, RecordingCommandBuilder.BuildGpuSmokeTestArguments(encoder))
                            .ConfigureAwait(false))
                    {
                        SetGpuCaptureSupport(result, encoder, false);
                    }
                }

                // Confirm each present hardware encoder can actually encode via a source-agnostic
                // test encode. This is what catches a driver that rejects the encoder (e.g. an NVENC
                // API-version mismatch) — the -encoders text list alone reports it as present.
                foreach (var encoder in GpuCaptureEncoders)
                {
                    if (!result.CanEncode(encoder))
                    {
                        continue;
                    }

                    var probeArgs = RecordingCommandBuilder.BuildEncoderProbeArguments(encoder);
                    if (probeArgs == null)
                    {
                        continue;
                    }

                    var probe = await RunEncodeProbeAsync(path, probeArgs).ConfigureAwait(false);
                    if (!probe.Ok)
                    {
                        SetEncodeSupport(result, encoder, false);
                        if (result.FailedEncoderCodec == null)
                        {
                            result.FailedEncoderCodec = RecordingCommandBuilder.EncoderCodec(encoder);
                            result.EncoderProbeError = probe.StdErrTail;
                        }
                    }
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

        /// <summary>
        /// Runs one encoder test encode and returns whether it succeeded along with the ffmpeg stderr
        /// tail on failure (the driver error), so the caller can surface it instead of only logging.
        /// </summary>
        private async Task<(bool Ok, string StdErrTail)> RunEncodeProbeAsync(string path, string arguments)
        {
            using (var host = new FfmpegProcessHost(path, arguments, _logger))
            {
                if (!host.Start())
                {
                    return (false, null);
                }

                var exitCode = await host.WaitForExitAsync(ProbeTimeout).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    var tail = host.StdErrTail;
                    _logger?.Debug($"ffmpeg encoder probe failed (exit={exitCode}): {tail}");
                    return (false, tail);
                }

                return (true, null);
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

        /// <summary>
        /// A hardware encoder gets a GPU-resident capture path when it is present and ddagrab is
        /// usable — the direct feed adds no CUDA filters, so nothing else in -filters is required.
        /// </summary>
        private static void DeriveGpuCaptureSupport(FfmpegValidationResult result)
        {
            result.SupportsNvencGpuCapture =
                result.SupportsDdagrab && result.AvailableEncoders.Contains("h264_nvenc");
            result.SupportsAmfGpuCapture =
                result.SupportsDdagrab && result.AvailableEncoders.Contains("h264_amf");
            result.SupportsQsvGpuCapture =
                result.SupportsDdagrab && result.AvailableEncoders.Contains("h264_qsv");
        }

        private static void SetGpuCaptureSupport(
            FfmpegValidationResult result, RecordingEncoder encoder, bool value)
        {
            switch (encoder)
            {
                case RecordingEncoder.Nvenc:
                    result.SupportsNvencGpuCapture = value;
                    break;
                case RecordingEncoder.Amf:
                    result.SupportsAmfGpuCapture = value;
                    break;
                case RecordingEncoder.Qsv:
                    result.SupportsQsvGpuCapture = value;
                    break;
            }
        }

        /// <summary>
        /// Seeds per-encoder encode support from presence; the smoke-test pass then clears a flag
        /// when its test encode fails. Before the smoke test runs, presence is the best signal
        /// available (matching the pre-existing -encoders-only behavior).
        /// </summary>
        private static void DeriveEncodeSupport(FfmpegValidationResult result)
        {
            result.SupportsNvencEncode = result.AvailableEncoders.Contains("h264_nvenc");
            result.SupportsAmfEncode = result.AvailableEncoders.Contains("h264_amf");
            result.SupportsQsvEncode = result.AvailableEncoders.Contains("h264_qsv");
        }

        private static void SetEncodeSupport(
            FfmpegValidationResult result, RecordingEncoder encoder, bool value)
        {
            switch (encoder)
            {
                case RecordingEncoder.Nvenc:
                    result.SupportsNvencEncode = value;
                    break;
                case RecordingEncoder.Amf:
                    result.SupportsAmfEncode = value;
                    break;
                case RecordingEncoder.Qsv:
                    result.SupportsQsvEncode = value;
                    break;
            }
        }
    }
}
