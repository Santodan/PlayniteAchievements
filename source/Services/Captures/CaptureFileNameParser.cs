using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// Parses a capture filename (<c>NNN_AchievementName[_variant].png|mp4</c>, optionally with a
    /// " (2)" collision marker) back into a <see cref="CaptureItem"/>. Kept free of any capture-writer
    /// or Win32 dependency so it can be unit-tested in isolation.
    /// </summary>
    internal static class CaptureFileNameParser
    {
        private static readonly Regex DedupMarker = new Regex(@"\s\((\d+)\)$", RegexOptions.Compiled);
        private static readonly Regex LeadingNumber = new Regex(@"^(\d+)_", RegexOptions.Compiled);

        public static SuffixResolver CreateResolver(
            string cleanSuffix,
            string notificationSuffix,
            string framedSuffix) =>
            SuffixResolver.Create(cleanSuffix, notificationSuffix, framedSuffix);

        public static bool TryParse(string filePath, SuffixResolver resolver, out CaptureItem item)
        {
            item = null;
            if (string.IsNullOrEmpty(filePath) || resolver == null)
            {
                return false;
            }

            var ext = Path.GetExtension(filePath);
            var isVideo = string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase);
            var isPng = string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
            if (!isVideo && !isPng)
            {
                return false;
            }

            var name = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            // A filename collision appends " (2)", " (3)" before the extension; keep the counter
            // (0 = original file) and drop the marker so the variant suffix ends the string and
            // the achievement stem groups correctly.
            var dedupCounter = 0;
            var dedupMatch = DedupMarker.Match(name);
            if (dedupMatch.Success)
            {
                int.TryParse(
                    dedupMatch.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out dedupCounter);
                name = name.Substring(0, dedupMatch.Index);
            }

            var number = 0;
            var remainder = name;
            var match = LeadingNumber.Match(name);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                number = parsed;
                remainder = name.Substring(match.Length);
            }

            CaptureVariant variant;
            string stem;
            if (isVideo)
            {
                // Video clips are written without a variant suffix.
                variant = CaptureVariant.Video;
                stem = remainder;
            }
            else if (!resolver.TryClassifyPng(remainder, out variant, out stem))
            {
                // No configured suffix matched (e.g. a blanked-out suffix): fall back to Clean.
                variant = CaptureVariant.Clean;
                stem = remainder;
            }

            if (string.IsNullOrEmpty(stem))
            {
                return false;
            }

            item = new CaptureItem(filePath, variant, number, stem, dedupCounter);
            return true;
        }

        /// <summary>
        /// Maps the user-configured screenshot suffixes back to variants so a filename's trailing
        /// "_suffix" can be classified. Suffixes are sanitized the same way the writer sanitizes them.
        /// Longest suffix wins so a shorter suffix cannot shadow a longer one sharing its ending.
        /// </summary>
        public sealed class SuffixResolver
        {
            private readonly List<KeyValuePair<string, CaptureVariant>> _pngSuffixes;
            private readonly CaptureVariant? _blankSuffixVariant;

            private SuffixResolver(
                List<KeyValuePair<string, CaptureVariant>> pngSuffixes,
                CaptureVariant? blankSuffixVariant)
            {
                _pngSuffixes = pngSuffixes;
                _blankSuffixVariant = blankSuffixVariant;
            }

            public static SuffixResolver Create(
                string cleanSuffix,
                string notificationSuffix,
                string framedSuffix)
            {
                var configured = new[]
                {
                    new KeyValuePair<CaptureVariant, string>(CaptureVariant.Clean, cleanSuffix),
                    new KeyValuePair<CaptureVariant, string>(CaptureVariant.Notification, notificationSuffix),
                    new KeyValuePair<CaptureVariant, string>(CaptureVariant.Framed, framedSuffix),
                };

                var pngSuffixes = new List<KeyValuePair<string, CaptureVariant>>();
                CaptureVariant? blank = null;
                foreach (var entry in configured)
                {
                    var sanitized = string.IsNullOrWhiteSpace(entry.Value)
                        ? string.Empty
                        : AchievementIconCachePathBuilder.SanitizeSegment(entry.Value);
                    if (string.IsNullOrEmpty(sanitized))
                    {
                        // First variant with a blank suffix owns the suffix-less filename form.
                        if (!blank.HasValue)
                        {
                            blank = entry.Key;
                        }

                        continue;
                    }

                    pngSuffixes.Add(new KeyValuePair<string, CaptureVariant>(sanitized, entry.Key));
                }

                pngSuffixes.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                return new SuffixResolver(pngSuffixes, blank);
            }

            public bool TryClassifyPng(string remainder, out CaptureVariant variant, out string stem)
            {
                foreach (var pair in _pngSuffixes)
                {
                    var token = "_" + pair.Key;
                    if (remainder.Length > token.Length &&
                        remainder.EndsWith(token, StringComparison.OrdinalIgnoreCase))
                    {
                        variant = pair.Value;
                        stem = remainder.Substring(0, remainder.Length - token.Length);
                        return true;
                    }
                }

                if (_blankSuffixVariant.HasValue)
                {
                    variant = _blankSuffixVariant.Value;
                    stem = remainder;
                    return true;
                }

                variant = CaptureVariant.Clean;
                stem = null;
                return false;
            }
        }
    }
}
