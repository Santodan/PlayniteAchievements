namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Outcome of an authentication probe or interactive authentication attempt.
    /// </summary>
    public enum AuthOutcome
    {
        /// <summary>
        /// Authentication was already valid, no action needed.
        /// </summary>
        AlreadyAuthenticated,

        /// <summary>
        /// Authentication completed successfully via login flow.
        /// </summary>
        Authenticated,

        /// <summary>
        /// User is not authenticated.
        /// </summary>
        NotAuthenticated,

        /// <summary>
        /// Authentication timed out waiting for user action.
        /// </summary>
        TimedOut,

        /// <summary>
        /// Authentication was cancelled by user.
        /// </summary>
        Cancelled,

        /// <summary>
        /// Authentication failed due to an error.
        /// </summary>
        Failed,

        /// <summary>
        /// Probe to check authentication status failed.
        /// </summary>
        ProbeFailed
    }

    /// <summary>
    /// Progress steps during authentication flow.
    /// </summary>
    public enum AuthProgressStep
    {
        CheckingExistingSession,
        OpeningLoginWindow,
        WaitingForUserLogin,
        VerifyingSession,
        Completed,
        Failed
    }
}
