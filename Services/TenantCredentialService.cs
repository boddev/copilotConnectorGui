namespace CopilotConnectorGui.Services
{
    public class TenantCredentialService
    {
        private string? _overrideTenantId;
        private string? _overrideClientId;
        private string? _overrideClientSecret;
        private readonly IConfiguration _configuration;

        public TenantCredentialService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void SetCredentials(string tenantId, string clientId, string clientSecret)
        {
            _overrideTenantId = tenantId;
            _overrideClientId = clientId;
            _overrideClientSecret = clientSecret;
        }

        public void ClearCredentials()
        {
            _overrideTenantId = null;
            _overrideClientId = null;
            _overrideClientSecret = null;
        }

        public string GetTenantId()
        {
            return _overrideTenantId ?? _configuration["AzureAd:TenantId"] ?? string.Empty;
        }

        public string GetClientId()
        {
            return _overrideClientId ?? _configuration["AzureAd:ClientId"] ?? string.Empty;
        }

        public string GetClientSecret()
        {
            return _overrideClientSecret ?? _configuration["AzureAd:ClientSecret"] ?? string.Empty;
        }

        public bool HasOverride()
        {
            return !string.IsNullOrEmpty(_overrideTenantId) && 
                   !string.IsNullOrEmpty(_overrideClientId) && 
                   !string.IsNullOrEmpty(_overrideClientSecret);
        }

        public (string tenantId, string clientId, string clientSecret) GetCurrentCredentials()
        {
            return (GetTenantId(), GetClientId(), GetClientSecret());
        }
    }
}
