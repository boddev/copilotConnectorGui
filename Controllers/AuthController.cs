using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace CopilotConnectorGui.Controllers
{
    public class AuthController : Controller
    {
        private readonly MicrosoftIdentityOptions _azureAdOptions;

        public AuthController(IOptionsMonitor<MicrosoftIdentityOptions> azureAdOptions)
        {
            _azureAdOptions = azureAdOptions.CurrentValue;
        }

        [HttpGet("/auto-login")]
        public IActionResult AutoLogin()
        {
            // Check if user is already authenticated
            if (User.Identity?.IsAuthenticated == true)
            {
                // Already authenticated, redirect to home
                return Redirect("/");
            }

            // Check if we have a valid client ID configured (not the placeholder)
            if (string.IsNullOrEmpty(_azureAdOptions.ClientId) || 
                _azureAdOptions.ClientId == "11111111-1111-1111-11111111111111111")
            {
                // Invalid configuration - redirect with specific error
                return Redirect("/?autoLoginFailed=true&reason=placeholder");
            }

            // Attempt silent authentication with SSO
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/",
                Items =
                {
                    ["prompt"] = "none", // Silent authentication
                    ["login_hint"] = "" // Allow SSO from any Microsoft session
                }
            };
            
            return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
        }

        [HttpGet("/signin-oidc")]
        public IActionResult SignInCallback()
        {
            // Handle the callback and redirect to home
            return Redirect("/");
        }

        [HttpGet("/api/config-check")]
        public IActionResult ConfigCheck()
        {
            var isPlaceholder = string.IsNullOrEmpty(_azureAdOptions.ClientId) || 
                               _azureAdOptions.ClientId == "11111111-1111-1111-11111111111111111";
            
            return Json(new { isPlaceholder });
        }
    }
}
