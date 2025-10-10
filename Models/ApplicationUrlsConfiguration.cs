namespace CopilotConnectorGui.Models
{
    public class ApplicationUrlsConfiguration
    {
        public string BaseUrl { get; set; } = "http://localhost:5000";
        public string BaseUrlSecure { get; set; } = "https://localhost:5001";
        public string DevelopmentUrl { get; set; } = "http://localhost:7265";
        public string DevelopmentUrlSecure { get; set; } = "https://localhost:7266";
        public string ConsentCompletePath { get; set; } = "/consent-complete";
        public string SignInCallbackPath { get; set; } = "/signin-oidc";

        /// <summary>
        /// Gets the full consent complete URL using the base URL
        /// </summary>
        public string ConsentCompleteUrl => $"{BaseUrl}{ConsentCompletePath}";

        /// <summary>
        /// Gets the full sign-in callback URL using the secure base URL
        /// </summary>
        public string SignInCallbackUrl => $"{BaseUrlSecure}{SignInCallbackPath}";

        /// <summary>
        /// Gets all redirect URIs for app registration
        /// </summary>
        public List<string> GetAllRedirectUris()
        {
            return new List<string>
            {
                $"{BaseUrl}/",
                $"{BaseUrlSecure}/",
                $"{DevelopmentUrl}/",
                $"{DevelopmentUrlSecure}/",
                ConsentCompleteUrl
            };
        }
    }
}
