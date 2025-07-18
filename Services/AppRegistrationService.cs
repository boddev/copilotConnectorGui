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
                                    type = "Application"
                                },
                                new
                                {
                                    id = "38c3d6ee-69ee-422f-b954-e17819665354", // ExternalItem.ReadWrite.OwnedBy
                                    type = "Application"
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
                return new AppRegistrationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
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
                                    Id = Guid.Parse("8116ae0f-55c2-452d-9944-d18420f5b2c8"), // ExternalConnection.ReadWrite.OwnedBy
                                    Type = "Application"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("38c3d6ee-69ee-422f-b954-e17819665354"), // ExternalItem.ReadWrite.OwnedBy
                                    Type = "Application"
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
                return new AppRegistrationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
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
                    return new AppRegistrationResult 
                    { 
                        Success = false, 
                        ErrorMessage = $"Failed to create app registration: {response.StatusCode}" 
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
    }
}
