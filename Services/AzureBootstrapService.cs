using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using CopilotConnectorGui.Models;

namespace CopilotConnectorGui.Services
{
    public class AzureBootstrapService
    {
        private readonly ILogger<AzureBootstrapService> _logger;
        private readonly ApplicationUrlsConfiguration _urlConfig;

        public AzureBootstrapService(
            ILogger<AzureBootstrapService> logger,
            IOptions<ApplicationUrlsConfiguration> urlConfig)
        {
            _logger = logger;
            _urlConfig = urlConfig.Value;
        }

        public async Task<BootstrapResult> BootstrapApplicationAsync()
        {
            try
            {
                // Step 1: Check if Azure CLI is installed
                var cliInstalled = await IsAzureCliInstalledAsync();
                if (!cliInstalled)
                {
                    return new BootstrapResult
                    {
                        Success = false,
                        ErrorMessage = "Azure CLI is not installed. Please install Azure CLI first.",
                        RequiresManualAction = true,
                        ManualActionUrl = "https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
                    };
                }

                // Step 2: Check if user is logged in
                var loginStatus = await CheckAzureCliLoginAsync();
                if (!loginStatus.IsLoggedIn)
                {
                    return new BootstrapResult
                    {
                        Success = false,
                        ErrorMessage = "Please log in to Azure CLI first.",
                        RequiresManualAction = true,
                        AzureCliCommand = "az login"
                    };
                }

                // Step 3: Create the bootstrap app registration
                var appReg = await CreateBootstrapAppRegistrationAsync();
                if (!appReg.Success)
                {
                    return appReg;
                }

                // Step 4: Update the application configuration
                await UpdateApplicationConfigurationAsync(appReg.ClientId!, appReg.ClientSecret!, loginStatus.TenantId!);

                return new BootstrapResult
                {
                    Success = true,
                    ClientId = appReg.ClientId,
                    ClientSecret = appReg.ClientSecret,
                    TenantId = loginStatus.TenantId,
                    Message = "Bootstrap completed successfully! The application is now configured for SSO."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bootstrap process failed");
                return new BootstrapResult
                {
                    Success = false,
                    ErrorMessage = $"Bootstrap failed: {ex.Message}"
                };
            }
        }

        private async Task<bool> IsAzureCliInstalledAsync()
        {
            try
            {
                var result = await RunCommandAsync("az", "--version");
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<AzureLoginStatus> CheckAzureCliLoginAsync()
        {
            try
            {
                var result = await RunCommandAsync("az", "account show --output json");
                if (result.ExitCode != 0)
                {
                    return new AzureLoginStatus { IsLoggedIn = false };
                }

                var accountInfo = JsonSerializer.Deserialize<JsonElement>(result.Output);
                var tenantId = accountInfo.GetProperty("tenantId").GetString();
                var userPrincipalName = accountInfo.GetProperty("user").GetProperty("name").GetString();

                return new AzureLoginStatus
                {
                    IsLoggedIn = true,
                    TenantId = tenantId,
                    UserPrincipalName = userPrincipalName
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Azure CLI login status");
                return new AzureLoginStatus { IsLoggedIn = false };
            }
        }

        private async Task<BootstrapResult> CreateBootstrapAppRegistrationAsync()
        {
            try
            {
                // Create the app registration
                var appName = $"Copilot-Connector-Bootstrap-{DateTime.Now:yyyyMMdd-HHmmss}";
                var createResult = await RunCommandAsync("az", $"ad app create --display-name \"{appName}\" --output json");
                
                if (createResult.ExitCode != 0)
                {
                    return new BootstrapResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create app registration: {createResult.Error}"
                    };
                }

                var appInfo = JsonSerializer.Deserialize<JsonElement>(createResult.Output);
                var appId = appInfo.GetProperty("appId").GetString();
                var objectId = appInfo.GetProperty("id").GetString();

                // Add required API permissions for Microsoft Graph
                await RunCommandAsync("az", $"ad app permission add --id {appId} --api 00000003-0000-0000-c000-000000000000 --api-permissions 1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9=Role"); // Application.ReadWrite.All
                await RunCommandAsync("az", $"ad app permission add --id {appId} --api 00000003-0000-0000-c000-000000000000 --api-permissions 9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30=Role"); // Application.Read.All
                await RunCommandAsync("az", $"ad app permission add --id {appId} --api 00000003-0000-0000-c000-000000000000 --api-permissions f431331c-49a6-499f-be1c-62af74111dda=Role"); // ExternalConnection.ReadWrite.All

                // Grant admin consent
                await RunCommandAsync("az", $"ad app permission admin-consent --id {appId}");

                // Create client secret
                var secretResult = await RunCommandAsync("az", $"ad app credential reset --id {appId} --output json");
                if (secretResult.ExitCode != 0)
                {
                    return new BootstrapResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create client secret: {secretResult.Error}"
                    };
                }

                var secretInfo = JsonSerializer.Deserialize<JsonElement>(secretResult.Output);
                var clientSecret = secretInfo.GetProperty("password").GetString();

                // Add redirect URI for the application
                var redirectUri = _urlConfig.SignInCallbackUrl;
                await RunCommandAsync("az", $"ad app update --id {appId} --web-redirect-uris {redirectUri}");

                return new BootstrapResult
                {
                    Success = true,
                    ClientId = appId,
                    ClientSecret = clientSecret,
                    ObjectId = objectId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating bootstrap app registration");
                return new BootstrapResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to create app registration: {ex.Message}"
                };
            }
        }

        private async Task UpdateApplicationConfigurationAsync(string clientId, string clientSecret, string tenantId)
        {
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            var configJson = await File.ReadAllTextAsync(configPath);
            
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            
            var options = new JsonWriterOptions { Indented = true };
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, options);
            
            writer.WriteStartObject();
            
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "AzureAd")
                {
                    writer.WritePropertyName("AzureAd");
                    writer.WriteStartObject();
                    
                    foreach (var azureAdProperty in property.Value.EnumerateObject())
                    {
                        if (azureAdProperty.Name == "ClientId")
                        {
                            writer.WriteString("ClientId", clientId);
                        }
                        else if (azureAdProperty.Name == "ClientSecret")
                        {
                            writer.WriteString("ClientSecret", clientSecret);
                        }
                        else if (azureAdProperty.Name == "TenantId")
                        {
                            writer.WriteString("TenantId", tenantId);
                        }
                        else
                        {
                            azureAdProperty.WriteTo(writer);
                        }
                    }
                    
                    writer.WriteEndObject();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            
            writer.WriteEndObject();
            
            var updatedJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            await File.WriteAllTextAsync(configPath, updatedJson);
        }

        private async Task<CommandResult> RunCommandAsync(string command, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync();

            return new CommandResult
            {
                ExitCode = process.ExitCode,
                Output = output,
                Error = error
            };
        }
    }

    public class BootstrapResult
    {
        public bool Success { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? TenantId { get; set; }
        public string? ObjectId { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Message { get; set; }
        public bool RequiresManualAction { get; set; }
        public string? ManualActionUrl { get; set; }
        public string? AzureCliCommand { get; set; }
    }

    public class AzureLoginStatus
    {
        public bool IsLoggedIn { get; set; }
        public string? TenantId { get; set; }
        public string? UserPrincipalName { get; set; }
    }

    public class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }
}
