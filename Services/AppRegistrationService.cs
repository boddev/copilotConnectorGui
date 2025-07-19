using Microsoft.Graph;
using Microsoft.Graph.Models;
using CopilotConnectorGui.Models;
using System.Security.Claims;
using Newtonsoft.Json;
using Microsoft.Graph.Applications.Item.AddPassword;

namespace CopilotConnectorGui.Services
{
    public class AppRegistrationService
    {
        private readonly GraphService _graphService;
        private readonly ILogger<AppRegistrationService> _logger;

        public AppRegistrationService(GraphService graphService, ILogger<AppRegistrationService> logger)
        {
            _graphService = graphService;
            _logger = logger;
        }

        public async Task<AppRegistrationResult> CreateAppRegistrationAsync(ClaimsPrincipal user, string tenantId)
        {
            try
            {
                // Check user permissions first
                var hasPermissions = await _graphService.CheckUserPermissionsAsync(user);
                if (!hasPermissions)
                {
                    return new AppRegistrationResult
                    {
                        Success = false,
                        ErrorMessage = "Insufficient permissions. This could be due to:\n" +
                                     "1. Your user account lacks the required Azure AD role (Application Developer, Cloud Application Administrator, or Global Administrator)\n" +
                                     "2. This application needs admin consent for Microsoft Graph permissions\n" +
                                     "3. The current app registration needs proper delegated permissions configured\n\n" +
                                     "Required delegated permissions for this app:\n" +
                                     "• Application.ReadWrite.All\n" +
                                     "• Directory.ReadWrite.All\n\n" +
                                     "TO FIX THIS:\n" +
                                     "1. Go to Azure Portal: https://portal.azure.com\n" +
                                     "2. Navigate to: Azure Active Directory > App registrations\n" +
                                     "3. Find app: 3e847995-c69c-4dc4-9246-21ad3fa3d76c\n" +
                                     "4. Click 'API permissions' > 'Add a permission'\n" +
                                     "5. Select 'Microsoft Graph' > 'Delegated permissions'\n" +
                                     "6. Add: Application.ReadWrite.All and Directory.ReadWrite.All\n" +
                                     "7. Click 'Grant admin consent' button\n" +
                                     "8. Refresh this page and try again\n\n" +
                                     "Direct link: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/3e847995-c69c-4dc4-9246-21ad3fa3d76c"
                    };
                }

                // Use HttpClient approach since Graph SDK is having issues
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);

                // Create the application
                var appRegistration = new
                {
                    displayName = $"Copilot Connector - {DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    signInAudience = "AzureADMyOrg",
                    requiredResourceAccess = new[]
                    {
                        new
                        {
                            resourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph
                            resourceAccess = new[]
                            {
                                new
                                {
                                    id = "8116ae0f-55c2-452d-9944-d18420f5b2c8", // ExternalConnection.ReadWrite.OwnedBy
                                    type = "Role"
                                },
                                new
                                {
                                    id = "38c3d6ee-69ee-422f-b954-e17819665354", // ExternalItem.ReadWrite.OwnedBy
                                    type = "Role"
                                },
                                new
                                {
                                    id = "f431331c-49a6-499f-be1c-62af19c34a9d", // ExternalConnection.ReadWrite.All (Delegated)
                                    type = "Scope"
                                },
                                new
                                {
                                    id = "34c37bc0-2b40-4d5e-85e1-2365cd256d79", // ExternalItem.ReadWrite.All (Delegated)  
                                    type = "Scope"
                                },
                                new
                                {
                                    id = "19dbc75e-c2e2-444c-a770-ec69d8559fc7", // Directory.ReadWrite.All (Delegated)
                                    type = "Scope"
                                },
                                new
                                {
                                    id = "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9", // Application.ReadWrite.All (Delegated)
                                    type = "Scope"
                                }
                            }
                        }
                    }
                };

                return await CreateAppRegistrationInternalAsync(httpClient, appRegistration, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating app registration");
                
                var errorMessage = ex.Message.Contains("Insufficient privileges") || 
                                   ex.Message.Contains("Authorization_RequestDenied")
                    ? "Insufficient privileges to create app registrations. " +
                      "You need 'Application Developer' role or 'Cloud Application Administrator' role in Azure AD. " +
                      "Contact your Azure AD administrator to grant these permissions, or try using the Azure CLI Bootstrap option."
                    : ex.Message;
                
                return new AppRegistrationResult
                {
                    Success = false,
                    ErrorMessage = errorMessage
                };
            }
        }

        public async Task<AppRegistrationResult> CreateAppRegistrationWithAzureCliAsync(string tenantId)
        {
            try
            {
                // Use Azure CLI authentication
                var graphClient = _graphService.GetGraphServiceClientWithAzureCli();

                // Create the application using Graph SDK
                var application = new Application
                {
                    DisplayName = $"Copilot Connector - {DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    SignInAudience = "AzureADMyOrg",
                    RequiredResourceAccess = new List<RequiredResourceAccess>
                    {
                        new RequiredResourceAccess
                        {
                            ResourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph
                            ResourceAccess = new List<ResourceAccess>
                            {
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("8116ae0f-55c2-452d-9944-d18420f5b2c8"), // ExternalConnection.ReadWrite.OwnedBy (Application)
                                    Type = "Role"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("38c3d6ee-69ee-422f-b954-e17819665354"), // ExternalItem.ReadWrite.OwnedBy (Application)
                                    Type = "Role"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("f431331c-49a6-499f-be1c-62af19c34a9d"), // ExternalConnection.ReadWrite.All (Delegated)
                                    Type = "Scope"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("34c37bc0-2b40-4d5e-85e1-2365cd256d79"), // ExternalItem.ReadWrite.All (Delegated)
                                    Type = "Scope"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("19dbc75e-c2e2-444c-a770-ec69d8559fc7"), // Directory.ReadWrite.All (Delegated)
                                    Type = "Scope"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9"), // Application.ReadWrite.All (Delegated)
                                    Type = "Scope"
                                }
                            }
                        }
                    }
                };

                var createdApp = await graphClient.Applications.PostAsync(application);

                if (createdApp?.AppId == null)
                {
                    throw new InvalidOperationException("Failed to create application - no App ID returned");
                }

                // Add client secret
                var addPasswordPostRequestBody = new AddPasswordPostRequestBody
                {
                    PasswordCredential = new PasswordCredential
                    {
                        DisplayName = "Client Secret for Copilot Connector",
                        EndDateTime = DateTimeOffset.UtcNow.AddYears(2)
                    }
                };

                var secretResult = await graphClient.Applications[createdApp.Id].AddPassword.PostAsync(addPasswordPostRequestBody);

                if (secretResult?.SecretText == null)
                {
                    throw new InvalidOperationException("Failed to create client secret");
                }

                return new AppRegistrationResult
                {
                    Success = true,
                    ApplicationId = createdApp.AppId,
                    ClientSecret = secretResult.SecretText,
                    TenantId = tenantId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating app registration with Azure CLI");
                
                var errorMessage = ex.Message.Contains("Insufficient privileges") || 
                                   ex.Message.Contains("Authorization_RequestDenied")
                    ? "Insufficient privileges to create app registrations. " +
                      "You need 'Application Developer' role or 'Cloud Application Administrator' role in Azure AD. " +
                      "Contact your Azure AD administrator to grant these permissions, or try using the Azure CLI method if you have the necessary permissions there."
                    : ex.Message;
                
                return new AppRegistrationResult
                {
                    Success = false,
                    ErrorMessage = errorMessage
                };
            }
        }

        private async Task<AppRegistrationResult> CreateAppRegistrationInternalAsync(HttpClient httpClient, object appRegistration, string tenantId)
        {
            try
            {
                var json = JsonConvert.SerializeObject(appRegistration);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("applications", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to create app registration: {responseContent}");
                    
                    // Provide specific guidance for common errors
                    var errorMessage = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Forbidden => 
                            "Insufficient privileges to create app registrations. " +
                            "You need 'Application Developer' role or 'Cloud Application Administrator' role in Azure AD. " +
                            "Contact your Azure AD administrator to grant these permissions.",
                        System.Net.HttpStatusCode.Unauthorized => 
                            "Authentication failed. Please sign out and sign back in.",
                        _ => $"Failed to create app registration: {response.StatusCode} - {responseContent}"
                    };
                    
                    return new AppRegistrationResult 
                    { 
                        Success = false, 
                        ErrorMessage = errorMessage
                    };
                }

                dynamic appResult = JsonConvert.DeserializeObject(responseContent)!;
                string applicationId = appResult.appId;
                string objectId = appResult.id;

                // Create client secret
                var secretRequest = new
                {
                    passwordCredential = new
                    {
                        displayName = "Client Secret for Copilot Connector",
                        endDateTime = DateTime.UtcNow.AddYears(2).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    }
                };

                var secretJson = JsonConvert.SerializeObject(secretRequest);
                var secretContent = new StringContent(secretJson, System.Text.Encoding.UTF8, "application/json");

                var secretResponse = await httpClient.PostAsync($"applications/{objectId}/addPassword", secretContent);
                var secretResponseContent = await secretResponse.Content.ReadAsStringAsync();

                if (!secretResponse.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to create client secret: {secretResponseContent}");
                    return new AppRegistrationResult 
                    { 
                        Success = false, 
                        ErrorMessage = $"Failed to create client secret: {secretResponse.StatusCode}" 
                    };
                }

                dynamic secretResult = JsonConvert.DeserializeObject(secretResponseContent)!;
                string clientSecret = secretResult.secretText;

                return new AppRegistrationResult
                {
                    Success = true,
                    ApplicationId = applicationId,
                    ClientSecret = clientSecret,
                    TenantId = tenantId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CreateAppRegistrationInternalAsync");
                return new AppRegistrationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public string GenerateAdminConsentUrl(string clientId, string tenantId, string? redirectUri = null)
        {
            if (string.IsNullOrEmpty(redirectUri))
            {
                redirectUri = "https://localhost:7266/";
            }

            var consentUrl = $"https://login.microsoftonline.com/{tenantId}/adminconsent?" +
                           $"client_id={clientId}" +
                           $"&state=12345" +
                           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

            _logger.LogInformation("Generated admin consent URL: {ConsentUrl}", consentUrl);
            return consentUrl;
        }
    }
}
