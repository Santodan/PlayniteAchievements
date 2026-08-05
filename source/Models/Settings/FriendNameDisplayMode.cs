namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Controls how friend names combine the provider profile (persona) name and the
    /// provider-assigned nickname. A manual plugin rename always wins over this mode.
    /// </summary>
    public enum FriendNameDisplayMode
    {
        // Profile name only.
        Persona,

        // Provider nickname when present, profile name otherwise.
        Nickname,

        // "Profile name (nickname)"; profile name alone when no nickname exists.
        PersonaAndNickname
    }
}
