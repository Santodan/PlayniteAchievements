using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// A merged dictionary that parses each <c>Source</c> once per thread and shares the result.
    /// </summary>
    /// <remarks>
    /// WPF re-parses a <c>Source=</c> dictionary for every owner, so a control that merges the
    /// plugin's resource dictionaries in its own <c>Resources</c> pays that parse per instance:
    /// every realized mosaic tile, every widget host, every grid. This keeps each control's
    /// resource scope exactly as declared (lookup order and shadowing are unchanged) while the
    /// underlying dictionary is one instance shared across owners, which WPF supports.
    /// The cache is per thread because dictionary contents include Freezables owned by the
    /// thread that created them, and the plugin loads templates on non-UI threads too.
    /// </remarks>
    public sealed class SharedResourceDictionary : ResourceDictionary
    {
        [ThreadStatic]
        private static Dictionary<Uri, ResourceDictionary> _cache;

        private Uri _source;

        public new Uri Source
        {
            get => _source;
            set
            {
                _source = value;
                if (value == null)
                {
                    return;
                }

                if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
                {
                    base.Source = value;
                    return;
                }

                var cache = _cache ?? (_cache = new Dictionary<Uri, ResourceDictionary>());
                if (!cache.TryGetValue(value, out var shared))
                {
                    shared = new ResourceDictionary { Source = value };
                    cache[value] = shared;
                }

                MergedDictionaries.Add(shared);
            }
        }
    }
}
