namespace PlayniteAchievements.Services.Refresh
{
    /// <summary>
    /// The result of one rebuild run: the final status text the user sees, and whether the run
    /// fell short of what it was asked to do. Faulted covers a provider that threw, a provider that
    /// needs re-authentication, and a run that failed outright — all reasons the refresh did not
    /// deliver everything. The status text already spells out the detail; this carries the decision
    /// structurally, so a caller deciding whether to warn never has to sniff a localized string for
    /// the word "failed".
    /// </summary>
    public sealed class RebuildOutcome
    {
        public RebuildOutcome(string status, bool faulted)
        {
            Status = status;
            Faulted = faulted;
        }

        public string Status { get; }

        public bool Faulted { get; }

        public static RebuildOutcome Failed(string status)
        {
            return new RebuildOutcome(status, true);
        }
    }
}
