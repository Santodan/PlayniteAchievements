namespace PlayniteAchievements.Views.Helpers
{
    internal enum DataGridDropZoneKind
    {
        InsertBefore,
        InsertAfter,
        NestOnTarget
    }

    /// <summary>
    /// Maps a pointer position over a row to what a drop there means. Pure math, kept out of the
    /// behavior so the band boundaries are unit-testable without WPF.
    /// </summary>
    internal static class DataGridDropZone
    {
        /// <summary>
        /// Fraction of the row height given to each insert band. A quarter leaves the top and
        /// bottom bands comfortably larger than the insert line they aim at, while the row body -
        /// the on-row gesture's target - keeps the middle half.
        /// </summary>
        public const double ReorderBandFraction = 0.25;

        /// <summary>
        /// The zone under the pointer. With nesting unavailable the row splits at the midpoint,
        /// which is the pre-nest behavior every reorder-only grid keeps.
        /// </summary>
        public static DataGridDropZoneKind Resolve(double pointerY, double rowHeight, bool nestAllowed)
        {
            if (!nestAllowed || rowHeight <= 0)
            {
                return pointerY > rowHeight / 2.0
                    ? DataGridDropZoneKind.InsertAfter
                    : DataGridDropZoneKind.InsertBefore;
            }

            var band = rowHeight * ReorderBandFraction;
            if (pointerY < band)
            {
                return DataGridDropZoneKind.InsertBefore;
            }

            if (pointerY > rowHeight - band)
            {
                return DataGridDropZoneKind.InsertAfter;
            }

            return DataGridDropZoneKind.NestOnTarget;
        }
    }
}
