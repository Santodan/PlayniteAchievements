using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.EA.Models
{
    /// <summary>
    /// Response from EA's implicit grant token endpoint.
    /// URL: https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&amp;response_type=token&amp;redirect_uri=nucleus:rest&amp;prompt=none
    /// </summary>
    internal sealed class EaTokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("expires_in")]
        public string ExpiresIn { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("error_description")]
        public string ErrorDescription { get; set; }

        [JsonIgnore]
        public bool IsLoggedIn =>
            string.IsNullOrWhiteSpace(Error) &&
            !string.IsNullOrWhiteSpace(AccessToken);
    }
}
