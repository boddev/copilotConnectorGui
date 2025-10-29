using Microsoft.Graph;
using Microsoft.Identity.Web;
using System.Security.Claims;
using Azure.Identity;

namespace CopilotConnectorGui.Services
{
    public class GraphService
    {
        private readonly ITokenAcquisition _tokenAcquisition;
        private readonly ILogger<GraphService> _logger;

        public GraphService(ITokenAcquisition tokenAcquisition, ILogger<GraphService> logger)
        {
            _tokenAcquisition = tokenAcquisition;
            _logger = logger;
        }

        public async Task<string> GetAccessTokenForUserAsync(ClaimsPrincipal user)
        {
            try
            {
                // Use specific delegated scopes including External Connection permissions
                var scopes = new[] { 
                    "https://graph.microsoft.com/Application.ReadWrite.All",
                    "https://graph.microsoft.com/Directory.ReadWrite.All",
                    "https://graph.microsoft.com/ExternalConnection.ReadWrite.All",
                    "https://graph.microsoft.com/ExternalItem.ReadWrite.All"
                };
                
                var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(
                    scopes, 
                    user: user);

                return accessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting access token for user");
                throw;
            }
        }

        public GraphServiceClient GetGraphServiceClientWithAzureCli()
        {
            try
            {
                // Use Azure CLI credential - requires user to be logged in via "az login"
                var credential = new AzureCliCredential();
                var graphServiceClient = new GraphServiceClient(credential);
                return graphServiceClient;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Graph client with Azure CLI. Make sure you're logged in with 'az login'");
                throw new InvalidOperationException("Azure CLI authentication failed. Please run 'az login' first.", ex);
            }
        }

        public async Task<bool> IsAzureCliLoggedInAsync()
        {
            try
            {
                var credential = new AzureCliCredential();
                var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
                var token = await credential.GetTokenAsync(tokenRequestContext);
                return !string.IsNullOrEmpty(token.Token);
            }
            catch
            {
                return false;
            }
        }

        public GraphServiceClient GetGraphServiceClientForApp(string tenantId, string clientId, string clientSecret)
        {
            try
            {
                var options = new ClientSecretCredentialOptions
                {
                    AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                };

                var clientSecretCredential = new ClientSecretCredential(
                    tenantId,
                    clientId,
                    clientSecret,
                    options);

                var graphServiceClient = new GraphServiceClient(clientSecretCredential);
                return graphServiceClient;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Graph client for app");
                throw;
            }
        }

        public async Task<HttpClient> GetAuthenticatedHttpClientAsync(ClaimsPrincipal user)
        {
            var accessToken = await GetAccessTokenForUserAsync(user);
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            return httpClient;
        }

        public async Task<bool> CheckUserPermissionsAsync(ClaimsPrincipal user)
        {
            try
            {
                using var httpClient = await GetAuthenticatedHttpClientAsync(user);
                
                // Try to access the applications endpoint to check permissions
                var response = await httpClient.GetAsync("applications?$top=1");
                
                return response.IsSuccessStatusCode;
            }
            catch (Microsoft.Identity.Web.MicrosoftIdentityWebChallengeUserException ex)
            {
                _logger.LogWarning("Incremental consent required for user: {Error}", ex.Message);
                // This is expected when new scopes are added - return false to trigger proper error handling
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user permissions");
                return false;
            }
        }

        public async Task<List<Microsoft.Graph.Models.Group>> GetEntraIdGroupsAsync(ClaimsPrincipal user)
        {
            try
            {
                var accessToken = await GetAccessTokenForUserAsync(user);
                var credential = new Azure.Core.TokenCredential[] { new CustomTokenCredential(accessToken) }[0];
                var graphServiceClient = new GraphServiceClient(credential);

                var groups = new List<Microsoft.Graph.Models.Group>();
                
                // Get groups with required headers for $orderby
                var groupsPage = await graphServiceClient.Groups
                    .GetAsync(requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Select = new[] { "id", "displayName", "description", "mail" };
                        requestConfiguration.QueryParameters.Top = 999;
                        requestConfiguration.QueryParameters.Count = true;
                        requestConfiguration.QueryParameters.Orderby = new[] { "displayName" };
                        requestConfiguration.Headers.Add("ConsistencyLevel", "eventual");
                    });

                if (groupsPage?.Value != null)
                {
                    groups.AddRange(groupsPage.Value);

                    // Handle pagination
                    while (groupsPage?.OdataNextLink != null)
                    {
                        groupsPage = await graphServiceClient.Groups
                            .WithUrl(groupsPage.OdataNextLink)
                            .GetAsync();
                        
                        if (groupsPage?.Value != null)
                        {
                            groups.AddRange(groupsPage.Value);
                        }
                    }
                }

                _logger.LogInformation("Successfully retrieved {GroupCount} Entra ID groups", groups.Count);
                return groups;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Entra ID groups");
                throw;
            }
        }

        // Custom token credential for user-based authentication
        private class CustomTokenCredential : Azure.Core.TokenCredential
        {
            private readonly string _accessToken;

            public CustomTokenCredential(string accessToken)
            {
                _accessToken = accessToken;
            }

            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new Azure.Core.AccessToken(_accessToken, DateTimeOffset.UtcNow.AddHours(1));
            }

            public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new ValueTask<Azure.Core.AccessToken>(new Azure.Core.AccessToken(_accessToken, DateTimeOffset.UtcNow.AddHours(1)));
            }
        }
    }
}
