using System;

namespace PlayniteAchievements.Services
{
    public sealed class CachePersistenceException : Exception
    {
        public string CacheKey { get; }
        public string ProviderKey { get; }
        public string ErrorCode { get; }

        public CachePersistenceException(
            string cacheKey,
            string providerKey,
            string errorCode,
            string message,
            Exception innerException = null)
            : base(message, innerException)
        {
            CacheKey = cacheKey;
            ProviderKey = providerKey;
            ErrorCode = errorCode;
        }
    }
}
