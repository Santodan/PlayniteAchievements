using System.Windows.Controls;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// The option groups of a settings surface, in display order. The grid display settings
    /// editor and the showcase widget settings share these, so a grid widget's own rows and its
    /// grid display rows sit under one set of headers.
    /// </summary>
    public interface IOptionGroups
    {
        OptionGroup Content { get; }

        OptionGroup Appearance { get; }

        OptionGroup Layout { get; }

        OptionGroup Sorting { get; }

        OptionGroup ControlBar { get; }
    }

    /// <summary>
    /// The option groups on their own, for settings surfaces without a grid display editor.
    /// </summary>
    public sealed class OptionGroupsPanel : StackPanel, IOptionGroups
    {
        public OptionGroupsPanel()
        {
            Content = Add("LOCPlayAch_Common_Content");
            Appearance = Add("LOCPlayAch_Common_Appearance");
            Layout = Add("LOCPlayAch_Common_Layout");
            Sorting = Add("LOCPlayAch_Common_Sorting");
            ControlBar = Add("LOCPlayAch_Common_ControlBar");
        }

        public OptionGroup Content { get; }

        public OptionGroup Appearance { get; }

        public OptionGroup Layout { get; }

        public OptionGroup Sorting { get; }

        public OptionGroup ControlBar { get; }

        private OptionGroup Add(string headerKey)
        {
            var group = new OptionGroup { Header = ResourceProvider.GetString(headerKey) };
            Children.Add(group);
            return group;
        }
    }
}
