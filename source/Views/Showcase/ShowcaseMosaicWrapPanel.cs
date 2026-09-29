using System.ComponentModel;
using WpfToolkit.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The mosaics' virtualizing wrap panel, with every line of tiles centered in the widget.
    /// The default Uniform spacing sizes its gaps against a full row, so a mosaic with fewer
    /// tiles than fit on one row sat against the left edge. Here the tiles pack at the spacing
    /// option's gap (the tile margin) and each line splits its unused width evenly on both sides.
    /// </summary>
    /// <remarks>
    /// Only public properties are set: the plugin compiles against VirtualizingWrapPanel 1.5.4,
    /// but Playnite loads its own 2.x build, whose protected members differ (overriding the
    /// arrange against 1.5.4 internals threw MissingMethodException at runtime). The 2.x
    /// IsGridLayoutEnabled property, which makes each line center on its own tile count rather
    /// than a full row's, is absent from 1.5.4, so it is set by name when present.
    /// </remarks>
    public sealed class ShowcaseMosaicWrapPanel : VirtualizingWrapPanel
    {
        public ShowcaseMosaicWrapPanel()
        {
            SpacingMode = SpacingMode.StartAndEndOnly;
            DependencyPropertyDescriptor
                .FromName("IsGridLayoutEnabled", typeof(VirtualizingWrapPanel), typeof(VirtualizingWrapPanel))
                ?.SetValue(this, false);
        }
    }
}
