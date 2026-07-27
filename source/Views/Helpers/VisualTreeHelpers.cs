using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shared visual tree helper methods for traversing and inspecting the WPF visual tree.
    /// </summary>
    public static class VisualTreeHelpers
    {
        /// <summary>
        /// Finds a visual child of the specified type.
        /// </summary>
        public static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }

                var nested = FindVisualChild<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// <summary>
        /// Enumerates all visual descendants of the specified type, depth-first.
        /// </summary>
        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                yield break;
            }

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in FindVisualChildren<T>(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// Finds a visual parent of the specified type by walking up the visual tree.
        /// </summary>
        public static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typed)
                {
                    return typed;
                }

                // VisualTreeHelper only works with Visual or Visual3D elements.
                // For non-visual elements like Run, use logical tree or content parent.
                if (child is Visual || child is Visual3D)
                {
                    child = VisualTreeHelper.GetParent(child);
                }
                else if (child is FrameworkContentElement frameworkContentElement)
                {
                    child = frameworkContentElement.Parent;
                }
                else
                {
                    // Fallback: try logical parent for FrameworkElement
                    child = (child as FrameworkElement)?.Parent;
                }
            }

            return null;
        }

        /// <summary>
        /// Determines whether the specified source element is a column resize thumb.
        /// </summary>
        public static bool IsColumnResizeThumbHit(DependencyObject source)
        {
            return TryFindColumnResizeThumb(source, out _);
        }

        /// <summary>
        /// Finds the column resize thumb for the specified source element.
        /// </summary>
        public static bool TryFindColumnResizeThumb(DependencyObject source, out Thumb resizeThumb)
        {
            while (source != null)
            {
                if (source is Thumb thumb &&
                    (string.Equals(thumb.Name, "PART_LeftHeaderGripper", System.StringComparison.Ordinal) ||
                     string.Equals(thumb.Name, "PART_RightHeaderGripper", System.StringComparison.Ordinal)))
                {
                    resizeThumb = thumb;
                    return true;
                }

                source = GetParentForHitTesting(source);
            }

            resizeThumb = null;
            return false;
        }

        /// <summary>
        /// Gets the parent element for hit testing, handling Visual, Visual3D, and content elements.
        /// </summary>
        public static DependencyObject GetParentForHitTesting(DependencyObject source)
        {
            if (source == null)
            {
                return null;
            }

            if (source is Visual || source is Visual3D)
            {
                return VisualTreeHelper.GetParent(source);
            }

            if (source is FrameworkContentElement frameworkContentElement)
            {
                return frameworkContentElement.Parent;
            }

            if (source is ContentElement contentElement)
            {
                return ContentOperations.GetParent(contentElement);
            }

            return null;
        }
    }
}
