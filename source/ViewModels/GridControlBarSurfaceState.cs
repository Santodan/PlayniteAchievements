using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The saved control bar state of a grid outside the showcase, in the same
    /// <see cref="ControlBarFilterState"/> shape the widgets save. Only the Progress and Activity
    /// choices are saved so far; everything else on the bar starts fresh each session. Saving more
    /// means filling more fields here and reading them back where the grid restores.
    /// </summary>
    public static class GridControlBarSurfaceState
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <param name="progressKeys">The Progress picks as ProgressFilterChoices keys; null while the bar shows its default.</param>
        /// <param name="activityOptions">The Activity choices in bar order, which the picks are saved as positions of.</param>
        /// <param name="selectedActivity">The Activity picks.</param>
        public static ControlBarFilterState Capture(
            IEnumerable<string> progressKeys,
            IList<string> activityOptions,
            ICollection<string> selectedActivity)
        {
            var activity = new List<int>();
            for (var i = 0; i < (activityOptions?.Count ?? 0); i++)
            {
                if (selectedActivity?.Contains(activityOptions[i]) == true)
                {
                    activity.Add(i);
                }
            }

            return new ControlBarFilterState
            {
                ProgressKeys = progressKeys?
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Activity = activity.Count > 0 ? activity : null
            };
        }

        /// <summary>The Activity picks <paramref name="state"/> holds, as the options at its positions.</summary>
        public static List<string> RestoreActivity(ControlBarFilterState state, IList<string> activityOptions)
        {
            return (state?.Activity ?? Enumerable.Empty<int>())
                .Where(position => position >= 0 && position < (activityOptions?.Count ?? 0))
                .Select(position => activityOptions[position])
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .ToList();
        }

        /// <summary>The state as saved, or null when it holds nothing to restore.</summary>
        public static string Serialize(ControlBarFilterState state)
        {
            if (state == null || (state.ProgressKeys == null && (state.Activity == null || state.Activity.Count == 0)))
            {
                return null;
            }

            return JsonConvert.SerializeObject(state, JsonSettings);
        }

        /// <summary>The saved state, or null when there is none or it cannot be read.</summary>
        public static ControlBarFilterState Deserialize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<ControlBarFilterState>(raw, JsonSettings);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
