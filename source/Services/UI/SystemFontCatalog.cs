using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// One entry of a font family picker. A null <see cref="FamilyName"/> means "inherit"
    /// (the theme default, or the shared surface family for a per-line row);
    /// <see cref="PreviewFamily"/> is never null so item rendering always has a valid font.
    /// </summary>
    public sealed class FontFamilyOption
    {
        public FontFamilyOption(string displayName, string familyName, FontFamily previewFamily)
        {
            DisplayName = displayName;
            FamilyName = familyName;
            PreviewFamily = previewFamily;
        }

        public string DisplayName { get; }

        public string FamilyName { get; }

        public FontFamily PreviewFamily { get; }
    }

    /// <summary>
    /// The selectable font list, cached process-wide.
    /// </summary>
    /// <remarks>
    /// <see cref="Fonts.SystemFontFamilies"/> lists only base families, so weight variants such as
    /// "Segoe UI Light" and "Segoe UI Semilight" are absent from it — WPF keeps those faces inside
    /// <see cref="FontFamily.FamilyTypefaces"/> instead. GDI+ splits the same fonts into
    /// four-face families and so reports the variant names directly, and WPF resolves those names
    /// through <see cref="FontFamily(string)"/>: "Segoe UI Semilight" renders the Semilight face,
    /// including for variable fonts whose weights all live in one file. Unioning both enumerations
    /// therefore yields the variants without any change to how a font is stored (a family-name
    /// string) or applied.
    ///
    /// The face names in <see cref="FamilyTypeface.AdjustedFaceNames"/> are not a usable source for
    /// the same list: the Segoe UI Semilight face reports its name as "350".
    /// </remarks>
    internal static class SystemFontCatalog
    {
        /// <summary>
        /// GDI's <c>LOGFONT.lfFaceName</c> holds 32 characters including the terminator, so GDI+
        /// reports longer family names cut to exactly 31 ("Segoe UI Variable Display Semib").
        /// Those truncated names still resolve to a font, so they are excluded by length rather
        /// than by the resolution check below.
        /// </summary>
        private const int GdiTruncatedNameLength = 31;

        private static readonly object _gate = new object();
        private static IReadOnlyList<FontFamilyOption> _families;

        /// <summary>
        /// Every installed font family and weight variant that WPF can actually render, sorted for
        /// display. Does not include an "inherit" sentinel; callers prepend their own.
        /// </summary>
        public static IReadOnlyList<FontFamilyOption> Families => EnsureFamilies();

        /// <summary>
        /// Builds the catalog ahead of first use. Enumerating and validating the installed fonts
        /// costs enough to be worth keeping off the UI thread; safe to call repeatedly.
        /// </summary>
        public static void Prewarm()
        {
            try
            {
                EnsureFamilies();
            }
            catch
            {
                // Falls back to the lazy build on first access.
            }
        }

        private static IReadOnlyList<FontFamilyOption> EnsureFamilies()
        {
            if (_families != null)
            {
                return _families;
            }

            lock (_gate)
            {
                return _families ?? (_families = BuildFamilies());
            }
        }

        private static IReadOnlyList<FontFamilyOption> BuildFamilies()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var family in Fonts.SystemFontFamilies)
            {
                AddCandidate(names, family?.Source);
            }

            AddGdiFamilies(names);

            return names
                .Where(CanRender)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Select(CreateOption)
                .ToList();
        }

        /// <summary>
        /// Adds the GDI+ family names, which is where the weight variants come from. Best-effort:
        /// if GDI+ font enumeration fails the catalog still contains every WPF base family.
        /// </summary>
        private static void AddGdiFamilies(HashSet<string> names)
        {
            try
            {
                using (var installed = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var family in installed.Families)
                    {
                        AddCandidate(names, family?.Name);
                    }
                }
            }
            catch
            {
                // Leaves the WPF-enumerated families in place.
            }
        }

        private static void AddCandidate(HashSet<string> names, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length == GdiTruncatedNameLength)
            {
                return;
            }

            // A comma separates entries of a FontFamily fallback list and '#' introduces a packed
            // font name, so either character would make the stored value mean something else.
            if (name.IndexOf(',') >= 0 || name.IndexOf('#') >= 0)
            {
                return;
            }

            names.Add(name.Trim());
        }

        /// <summary>
        /// Whether WPF resolves the name to a real face. An unknown name silently falls back to the
        /// default font at render time, so this is what keeps names GDI+ reports but WPF cannot use
        /// (for example "Eras Bold ITC") out of the picker.
        /// </summary>
        public static bool CanRender(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
            {
                return false;
            }

            try
            {
                var typeface = new Typeface(
                    new FontFamily(familyName),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);
                return typeface.TryGetGlyphTypeface(out _);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// An entry for a family name that is not in the catalog, so that a font saved before it was
        /// uninstalled (or chosen on a machine that has it) stays selected and visible instead of
        /// being silently rewritten to the default.
        /// </summary>
        public static FontFamilyOption CreateUnlistedOption(string familyName)
        {
            return CreateOption(familyName);
        }

        private static FontFamilyOption CreateOption(string familyName)
        {
            FontFamily previewFamily;
            try
            {
                previewFamily = new FontFamily(familyName);
            }
            catch (ArgumentException)
            {
                previewFamily = SystemFonts.MessageFontFamily;
            }

            return new FontFamilyOption(familyName, familyName, previewFamily);
        }
    }
}
