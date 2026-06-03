using System;
using System.Net;

namespace PlayniteAchievements.Providers.EA
{
    internal sealed class EaApiHttpException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public EaApiHttpException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal sealed class EaTransientException : Exception
    {
        public EaTransientException(string message)
            : base(message)
        {
        }

        public EaTransientException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class EaAuthRequiredException : Exception
    {
        public EaAuthRequiredException(string message) : base(message) { }
    }
}
