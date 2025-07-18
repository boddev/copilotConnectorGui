using Microsoft.AspNetCore.Mvc;
using CopilotConnectorGui.Services;

namespace CopilotConnectorGui.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BootstrapController : ControllerBase
    {
        private readonly AzureBootstrapService _bootstrapService;
        private readonly ILogger<BootstrapController> _logger;

        public BootstrapController(AzureBootstrapService bootstrapService, ILogger<BootstrapController> logger)
        {
            _bootstrapService = bootstrapService;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> StartBootstrap()
        {
            try
            {
                var result = await _bootstrapService.BootstrapApplicationAsync();
                
                if (result.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        message = result.Message,
                        clientId = result.ClientId,
                        tenantId = result.TenantId,
                        requiresRestart = true
                    });
                }
                else
                {
                    return Ok(new
                    {
                        success = false,
                        error = result.ErrorMessage,
                        requiresManualAction = result.RequiresManualAction,
                        manualActionUrl = result.ManualActionUrl,
                        azureCliCommand = result.AzureCliCommand
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bootstrap API call failed");
                return StatusCode(500, new
                {
                    success = false,
                    error = $"Internal server error: {ex.Message}"
                });
            }
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetBootstrapStatus()
        {
            try
            {
                // Check if Azure CLI is available and user is logged in
                var cliInstalled = await IsAzureCliInstalledAsync();
                var loginStatus = await CheckAzureCliLoginAsync();

                return Ok(new
                {
                    azureCliInstalled = cliInstalled,
                    azureCliLoggedIn = loginStatus.IsLoggedIn,
                    userPrincipalName = loginStatus.UserPrincipalName,
                    tenantId = loginStatus.TenantId,
                    canBootstrap = cliInstalled && loginStatus.IsLoggedIn
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking bootstrap status");
                return StatusCode(500, new
                {
                    error = $"Error checking status: {ex.Message}"
                });
            }
        }

        private async Task<bool> IsAzureCliInstalledAsync()
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<(bool IsLoggedIn, string? UserPrincipalName, string? TenantId)> CheckAzureCliLoginAsync()
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "account show --output json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new System.Diagnostics.Process { StartInfo = startInfo };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    return (false, null, null);
                }

                var accountInfo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(output);
                var tenantId = accountInfo.GetProperty("tenantId").GetString();
                var userPrincipalName = accountInfo.GetProperty("user").GetProperty("name").GetString();

                return (true, userPrincipalName, tenantId);
            }
            catch
            {
                return (false, null, null);
            }
        }
    }
}
