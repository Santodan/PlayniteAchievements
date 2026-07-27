using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Single source of truth for the culture used to format numbers, percents, and other
    /// culture-sensitive display values. Resolves from the plugin's GlobalLanguage setting;
    /// unknown or empty values fall back to the OS regional format (CurrentCulture).
    /// Also provides the matching XmlLanguage applied to plugin visual roots so XAML
    /// StringFormat bindings use the same culture as C# formatting.
    /// </summary>
    public static class FormattingCulture
    {
        private static readonly object Sync = new object();
        private static readonly List<WeakReference<FrameworkElement>> Roots =
            new List<WeakReference<FrameworkElement>>();

        private static Func<string> _globalLanguageGetter;
        private static CultureInfo _current;
        private static XmlLanguage _xamlLanguage;

        private static readonly Dictionary<string, string> LanguageToCultureTag =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "english", "en-US" },
                { "german", "de-DE" },
                { "french", "fr-FR" },
                { "spanish", "es-ES" },
                { "italian", "it-IT" },
                { "portuguese", "pt-PT" },
                { "brazilian", "pt-BR" },
                { "russian", "ru-RU" },
                { "polish", "pl-PL" },
                { "dutch", "nl-NL" },
                { "swedish", "sv-SE" },
                { "finnish", "fi-FI" },
                { "danish", "da-DK" },
                { "norwegian", "nb-NO" },
                { "hungarian", "hu-HU" },
                { "czech", "cs-CZ" },
                { "romanian", "ro-RO" },
                { "turkish", "tr-TR" },
                { "greek", "el-GR" },
                { "bulgarian", "bg-BG" },
                { "ukrainian", "uk-UA" },
                { "thai", "th-TH" },
                { "vietnamese", "vi-VN" },
                { "japanese", "ja-JP" },
                { "koreana", "ko-KR" },
                { "schinese", "zh-CN" },
                { "tchinese", "zh-TW" },
                { "arabic", "ar-SA" }
            };

        /// <summary>
        /// Culture used for all plugin number/percent formatting.
        /// </summary>
        public static CultureInfo Current => _current ?? CultureInfo.CurrentCulture;

        /// <summary>
        /// XmlLanguage matching <see cref="Current"/>, applied to plugin visual roots so
        /// XAML StringFormat bindings format with the same culture.
        /// </summary>
        public static XmlLanguage XamlLanguage =>
            _xamlLanguage ?? (_xamlLanguage = ToXmlLanguage(Current));

        /// <summary>
        /// Wires the GlobalLanguage source and resolves the initial culture.
        /// </summary>
        public static void Initialize(Func<string> globalLanguageGetter)
        {
            _globalLanguageGetter = globalLanguageGetter;
            Refresh();
        }

        /// <summary>
        /// Sets the formatting language on a plugin visual root and tracks it so a later
        /// <see cref="Refresh"/> can re-apply the language to live roots.
        /// </summary>
        public static void Apply(FrameworkElement root)
        {
            if (root == null)
            {
                return;
            }

            root.Language = XamlLanguage;

            lock (Sync)
            {
                Roots.Add(new WeakReference<FrameworkElement>(root));
            }
        }

        /// <summary>
        /// Re-resolves the culture from the GlobalLanguage source and re-applies the matching
        /// XmlLanguage to live tracked roots. Bindings created after this call use the new
        /// culture; existing bindings update when their view rebuilds.
        /// </summary>
        public static void Refresh()
        {
            _current = Resolve(_globalLanguageGetter?.Invoke());
            _xamlLanguage = ToXmlLanguage(Current);

            lock (Sync)
            {
                for (var i = Roots.Count - 1; i >= 0; i--)
                {
                    if (Roots[i].TryGetTarget(out var root))
                    {
                        root.Language = _xamlLanguage;
                    }
                    else
                    {
                        Roots.RemoveAt(i);
                    }
                }
            }
        }

        private static CultureInfo Resolve(string globalLanguage)
        {
            if (!string.IsNullOrWhiteSpace(globalLanguage) &&
                LanguageToCultureTag.TryGetValue(globalLanguage.Trim(), out var tag))
            {
                try
                {
                    return CultureInfo.GetCultureInfo(tag);
                }
                catch (CultureNotFoundException)
                {
                }
            }

            return CultureInfo.CurrentCulture;
        }

        private static XmlLanguage ToXmlLanguage(CultureInfo culture)
        {
            try
            {
                return XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            }
            catch (ArgumentException)
            {
                return XmlLanguage.GetLanguage("en-US");
            }
        }
    }
}
