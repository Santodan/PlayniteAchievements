using System;

namespace PlayniteAchievements.Providers.Exophase
{
    public enum ExophaseAuthOutcome
    {
        Authenticated,
        AlreadyAuthenticated,
        NotAuthenticated,
        Cancelled,
        TimedOut,
        Failed,
        ProbeFailed
    }

    public enum ExophaseAuthProgressStep
    {
        CheckingExistingSession,
        OpeningLoginWindow,
        WaitingForUserLogin,
        VerifyingSession,
        Completed,
        Failed
    }

    public sealed class ExophaseAuthResult
    {
        public ExophaseAuthOutcome Outcome { get; set; }

        public string MessageKey { get; set; }

        public string Username { get; set; }

        public bool WindowOpened { get; set; }

        public bool IsSuccess =>
            Outcome == ExophaseAuthOutcome.Authenticated ||
            Outcome == ExophaseAuthOutcome.AlreadyAuthenticated;

        public static ExophaseAuthResult Create(
            ExophaseAuthOutcome outcome,
            string messageKey,
            string username = null,
            bool windowOpened = false)
        {
            return new ExophaseAuthResult
            {
                Outcome = outcome,
                MessageKey = messageKey,
                Username = username,
                WindowOpened = windowOpened
            };
        }
    }

    public interface IExophaseTokenProvider
    {
        bool IsAuthenticated { get; }
    }
}
