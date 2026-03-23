using PlayniteAchievements.Providers.Settings;
using System;

namespace PlayniteAchievements.Providers.Epic
{
    /// <summary>
    /// Epic Games provider settings. Authentication is handled via session manager.
    /// </summary>
    public class EpicSettings : ProviderSettingsBase
    {
        private string _accountId;
        private string _accessToken;
        private string _refreshToken;
        private string _tokenType;
        private DateTime _tokenExpiryUtc;
        private DateTime _refreshTokenExpiryUtc;

        /// <inheritdoc />
        public override string ProviderKey => "Epic";

        /// <summary>
        /// Epic account ID.
        /// </summary>
        public string AccountId
        {
            get => _accountId;
            set => SetValue(ref _accountId, value);
        }

        /// <summary>
        /// OAuth access token.
        /// </summary>
        public string AccessToken
        {
            get => _accessToken;
            set => SetValue(ref _accessToken, value);
        }

        /// <summary>
        /// OAuth refresh token.
        /// </summary>
        public string RefreshToken
        {
            get => _refreshToken;
            set => SetValue(ref _refreshToken, value);
        }

        /// <summary>
        /// Token type (e.g., "bearer").
        /// </summary>
        public string TokenType
        {
            get => _tokenType;
            set => SetValue(ref _tokenType, value);
        }

        /// <summary>
        /// Access token expiry time in UTC.
        /// </summary>
        public DateTime TokenExpiryUtc
        {
            get => _tokenExpiryUtc;
            set => SetValue(ref _tokenExpiryUtc, value);
        }

        /// <summary>
        /// Refresh token expiry time in UTC.
        /// </summary>
        public DateTime RefreshTokenExpiryUtc
        {
            get => _refreshTokenExpiryUtc;
            set => SetValue(ref _refreshTokenExpiryUtc, value);
        }
    }
}
