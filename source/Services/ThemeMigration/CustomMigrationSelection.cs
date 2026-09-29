using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeMigration
{
    /// <summary>
    /// Defines which legacy theme elements should be modernized during a custom migration.
    /// Unselected elements remain in their legacy form.
    /// </summary>
    public sealed class CustomMigrationSelection
    {
        public CustomMigrationSelection(IEnumerable<string> modernControlNames = null, bool modernizeBindings = false)
        {
            ModernControlNames = new HashSet<string>(
                modernControlNames ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            ModernizeBindings = modernizeBindings;
        }

        /// <summary>
        /// Gets the legacy control names that should be replaced with modern controls.
        /// </summary>
        public HashSet<string> ModernControlNames { get; }

        /// <summary>
        /// Gets or sets whether LegacyData bindings should be replaced with Theme bindings.
        /// </summary>
        public bool ModernizeBindings { get; set; }

        public bool ShouldModernizeControl(string legacyControlName)
        {
            return !string.IsNullOrWhiteSpace(legacyControlName) &&
                   ModernControlNames.Contains(legacyControlName);
        }
    }
}

