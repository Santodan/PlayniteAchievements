namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// Which of a category row's two stat snapshots its live properties hold: the achievements
    /// labelled exactly that node (an expanded row, so visible rows partition the set), or the
    /// node's whole subtree (a collapsed row, absorbing the descendants its collapse hid).
    /// </summary>
    internal enum CategoryStatsScope
    {
        Own = 0,
        Subtree = 1
    }
}
