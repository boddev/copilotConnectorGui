using Microsoft.Graph;
using Microsoft.Graph.Models;
using CopilotConnectorGui.Models;
using System.Security.Claims;
using Newtonsoft.Json;
using Microsoft.Graph.Applications.Item.AddPassword;
using Microsoft.Extensions.Options;

namespace CopilotConnectorGui.Services
{
    public class AppRegistrationService
    {
        private readonly GraphService _graphService;
        private readonly ILogger<AppRegistrationService> _logger;
        private readonly ApplicationUrlsConfiguration _urlConfig;

        public AppRegistrationService(
            GraphService graphService, 
            ILogger<AppRegistrationService> logger,
            IOptions<ApplicationUrlsConfiguration> urlConfig)
        {
            _graphService = graphService;
            _logger = logger;
            _urlConfig = urlConfig.Value;
        }

        public async Task<AppRegistrationResult> CreateAppRegistrationAsync(ClaimsPrincipal user, string tenantId)
        {
            try
            {
                _logger.LogInformation("Creating app registration for tenant: {TenantId}", tenantId);

                // Use HttpClient approach since Graph SDK is having issues
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);

                // Create the application
                var appRegistration = new
                {
                    displayName = $"Copilot Connector - {DateTime.UtcNow:yyyyMMdd-HHmmss}",
                    signInAudience = "AzureADMyOrg",
                    web = new
                    {
                        redirectUris = _urlConfig.GetAllRedirectUris().ToArray()
                    },
                    requiredResourceAccess = new[]
                    {
                        new
                        {
                            resourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph
                            resourceAccess = new[]
                            {
                                new
                                {
                                    id = "34c37bc0-2b40-4d5e-85e1-2365cd256d79", // ExternalConnection.ReadWrite.OwnedBy (Application)
                                    type = "Role"
                                },
                                new
                                {
                                    id = "8116ae0f-55c2-452d-9944-d18420f5b2c8", // ExternalItem.ReadWrite.OwnedBy (Application)
                                    type = "Role"
                                }
                            }
                        }
                    }
                };

                var result = await CreateAppRegistrationInternalAsync(httpClient, appRegistration, tenantId);
                
                // If app creation succeeded, try to grant admin consent using the authenticated user's context
                if (result.Success)
                {
                    bool consentGranted = false;
                    
                    try
                    {
                        _logger.LogInformation("Attempting to grant admin consent for app: {AppId}", result.ApplicationId);
                        await GrantAdminConsentWithUserContextAsync(user, result.ApplicationId!, tenantId);
                        _logger.LogInformation("Admin consent granted successfully using user context");
                        consentGranted = true;
                    }
                    catch (Exception consentEx)
                    {
                        _logger.LogWarning(consentEx, "Could not automatically grant admin consent using user context. Manual consent will be required.");
                    }
                    
                    // If consent wasn't granted, provide the URL for manual consent
                    if (!consentGranted)
                    {
                        result.AdminConsentRequired = true;
                        result.AdminConsentUrl = GenerateAdminConsentUrl(result.ApplicationId!, tenantId);
                        _logger.LogWarning("Admin consent URL: {ConsentUrl}", result.AdminConsentUrl);
                    }
                }
                
                return result;
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
                    Web = new Microsoft.Graph.Models.WebApplication
                    {
                        RedirectUris = _urlConfig.GetAllRedirectUris()
                    },
                    RequiredResourceAccess = new List<RequiredResourceAccess>
                    {
                        new RequiredResourceAccess
                        {
                            ResourceAppId = "00000003-0000-0000-c000-000000000000", // Microsoft Graph
                            ResourceAccess = new List<ResourceAccess>
                            {
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("34c37bc0-2b40-4d5e-85e1-2365cd256d79"), // ExternalConnection.ReadWrite.OwnedBy (Application)
                                    Type = "Role"
                                },
                                new ResourceAccess
                                {
                                    Id = Guid.Parse("8116ae0f-55c2-452d-9944-d18420f5b2c8"), // ExternalItem.ReadWrite.OwnedBy (Application)
                                    Type = "Role"
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

                // Try to grant admin consent automatically
                bool consentGranted = false;
                string? consentUrl = null;
                
                try
                {
                    _logger.LogInformation("Attempting to grant admin consent for app: {AppId}", createdApp.AppId);
                    await GrantAdminConsentAsync(graphClient, createdApp.AppId, tenantId);
                    _logger.LogInformation("Admin consent granted successfully");
                    consentGranted = true;
                }
                catch (Exception consentEx)
                {
                    _logger.LogWarning(consentEx, "Could not automatically grant admin consent. Manual consent will be required.");
                    
                    // Generate admin consent URL for manual consent
                    consentUrl = GenerateAdminConsentUrl(createdApp.AppId, tenantId);
                    _logger.LogWarning("Admin consent URL: {ConsentUrl}", consentUrl);
                }

                return new AppRegistrationResult
                {
                    Success = true,
                    ApplicationId = createdApp.AppId,
                    ClientSecret = secretResult.SecretText,
                    TenantId = tenantId,
                    AdminConsentRequired = !consentGranted,
                    AdminConsentUrl = consentUrl
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
                // Default to consent completion page from configuration
                redirectUri = _urlConfig.ConsentCompleteUrl;
            }

            var consentUrl = $"https://login.microsoftonline.com/{tenantId}/adminconsent?" +
                           $"client_id={clientId}" +
                           $"&state=12345" +
                           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

            _logger.LogInformation("Generated admin consent URL: {ConsentUrl}", consentUrl);
            return consentUrl;
        }

        private async Task GrantAdminConsentWithUserContextAsync(ClaimsPrincipal user, string applicationId, string tenantId)
        {
            try
            {
                // Use the authenticated user's context (they're already logged in with sufficient permissions)
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);

                // Get the service principal for the application
                var servicePrincipalsResponse = await httpClient.GetAsync($"servicePrincipals?$filter=appId eq '{applicationId}'");
                var servicePrincipalsContent = await servicePrincipalsResponse.Content.ReadAsStringAsync();
                
                if (!servicePrincipalsResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to get service principals: {servicePrincipalsContent}");
                }

                dynamic servicePrincipalsResult = JsonConvert.DeserializeObject(servicePrincipalsContent)!;
                dynamic? servicePrincipal = null;
                
                if (servicePrincipalsResult.value.Count > 0)
                {
                    servicePrincipal = servicePrincipalsResult.value[0];
                }
                else
                {
                    // Create service principal if it doesn't exist
                    var newServicePrincipal = new { appId = applicationId };
                    var spJson = JsonConvert.SerializeObject(newServicePrincipal);
                    var spContent = new StringContent(spJson, System.Text.Encoding.UTF8, "application/json");
                    
                    var spResponse = await httpClient.PostAsync("servicePrincipals", spContent);
                    var spResponseContent = await spResponse.Content.ReadAsStringAsync();
                    
                    if (!spResponse.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Failed to create service principal: {spResponseContent}");
                    }
                    
                    servicePrincipal = JsonConvert.DeserializeObject(spResponseContent)!;
                }

                string servicePrincipalId = servicePrincipal.id;

                // Get Microsoft Graph service principal
                var graphSpResponse = await httpClient.GetAsync("servicePrincipals?$filter=appId eq '00000003-0000-0000-c000-000000000000'");
                var graphSpContent = await graphSpResponse.Content.ReadAsStringAsync();
                
                if (!graphSpResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to get Microsoft Graph service principal: {graphSpContent}");
                }

                dynamic graphSpResult = JsonConvert.DeserializeObject(graphSpContent)!;
                string graphServicePrincipalId = graphSpResult.value[0].id;

                // Grant admin consent for the required permissions
                var permissionIds = new[]
                {
                    "34c37bc0-2b40-4d5e-85e1-2365cd256d79", // ExternalConnection.ReadWrite.OwnedBy
                    "8116ae0f-55c2-452d-9944-d18420f5b2c8"  // ExternalItem.ReadWrite.OwnedBy
                };

                int successCount = 0;
                int failureCount = 0;
                List<string> errors = new List<string>();

                foreach (var permissionId in permissionIds)
                {
                    try
                    {
                        var appRoleAssignment = new
                        {
                            appRoleId = permissionId,
                            principalId = servicePrincipalId,
                            resourceId = graphServicePrincipalId
                        };

                        var assignmentJson = JsonConvert.SerializeObject(appRoleAssignment);
                        var assignmentContent = new StringContent(assignmentJson, System.Text.Encoding.UTF8, "application/json");

                        var assignmentResponse = await httpClient.PostAsync($"servicePrincipals/{servicePrincipalId}/appRoleAssignments", assignmentContent);
                        
                        if (assignmentResponse.IsSuccessStatusCode)
                        {
                            _logger.LogInformation("Granted permission {PermissionId} to app {AppId}", permissionId, applicationId);
                            successCount++;
                        }
                        else
                        {
                            var assignmentError = await assignmentResponse.Content.ReadAsStringAsync();
                            if (assignmentError.Contains("Permission being assigned already exists"))
                            {
                                _logger.LogInformation("Permission {PermissionId} already granted to app {AppId}", permissionId, applicationId);
                                successCount++;
                            }
                            else
                            {
                                _logger.LogWarning("Failed to grant permission {PermissionId}: {Error}", permissionId, assignmentError);
                                failureCount++;
                                errors.Add($"Permission {permissionId}: {assignmentError}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex.Message.Contains("Permission being assigned already exists"))
                    {
                        _logger.LogInformation("Permission {PermissionId} already granted to app {AppId}", permissionId, applicationId);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Exception granting permission {PermissionId}", permissionId);
                        failureCount++;
                        errors.Add($"Permission {permissionId}: {ex.Message}");
                    }
                }

                // If no permissions were successfully granted, throw an exception
                if (successCount == 0 && failureCount > 0)
                {
                    var errorMessage = $"Failed to grant admin consent. {failureCount} permission(s) failed. " +
                                     $"Errors: {string.Join("; ", errors)}";
                    _logger.LogError("Admin consent failed: {ErrorMessage}", errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }
                
                _logger.LogInformation("Admin consent completed: {SuccessCount} succeeded, {FailureCount} failed", 
                    successCount, failureCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting admin consent for application {AppId} using user context", applicationId);
                throw;
            }
        }

        private async Task GrantAdminConsentAsync(GraphServiceClient graphClient, string applicationId, string tenantId)
        {
            try
            {
                // Get the service principal for the application
                var servicePrincipals = await graphClient.ServicePrincipals
                    .GetAsync(requestConfiguration => requestConfiguration.QueryParameters.Filter = $"appId eq '{applicationId}'");

                var servicePrincipal = servicePrincipals?.Value?.FirstOrDefault();
                if (servicePrincipal == null)
                {
                    // Create service principal if it doesn't exist
                    var newServicePrincipal = new Microsoft.Graph.Models.ServicePrincipal
                    {
                        AppId = applicationId
                    };
                    servicePrincipal = await graphClient.ServicePrincipals.PostAsync(newServicePrincipal);
                }

                if (servicePrincipal?.Id == null)
                {
                    throw new InvalidOperationException("Could not create or find service principal");
                }

                // Grant admin consent for the Microsoft Graph API permissions
                var graphServicePrincipals = await graphClient.ServicePrincipals
                    .GetAsync(requestConfiguration => requestConfiguration.QueryParameters.Filter = "appId eq '00000003-0000-0000-c000-000000000000'");

                var graphServicePrincipal = graphServicePrincipals?.Value?.FirstOrDefault();
                if (graphServicePrincipal?.Id == null)
                {
                    throw new InvalidOperationException("Could not find Microsoft Graph service principal");
                }

                // Create app role assignments for the required permissions
                var permissionIds = new[]
                {
                    "34c37bc0-2b40-4d5e-85e1-2365cd256d79", // ExternalConnection.ReadWrite.OwnedBy
                    "8116ae0f-55c2-452d-9944-d18420f5b2c8"  // ExternalItem.ReadWrite.OwnedBy
                };

                foreach (var permissionId in permissionIds)
                {
                    try
                    {
                        var appRoleAssignment = new Microsoft.Graph.Models.AppRoleAssignment
                        {
                            AppRoleId = Guid.Parse(permissionId),
                            PrincipalId = Guid.Parse(servicePrincipal.Id),
                            ResourceId = Guid.Parse(graphServicePrincipal.Id)
                        };

                        await graphClient.ServicePrincipals[servicePrincipal.Id].AppRoleAssignments.PostAsync(appRoleAssignment);
                        _logger.LogInformation("Granted permission {PermissionId} to app {AppId}", permissionId, applicationId);
                    }
                    catch (Exception ex) when (ex.Message.Contains("Permission being assigned already exists"))
                    {
                        _logger.LogInformation("Permission {PermissionId} already granted to app {AppId}", permissionId, applicationId);
                        // This is fine - permission already exists
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error granting admin consent for application {AppId}", applicationId);
                throw;
            }
        }
    }
}
