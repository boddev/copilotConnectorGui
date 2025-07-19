using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using CopilotConnectorGui.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
        
        // Enable auto-authentication for already logged-in users
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // Check if this is an auto-login attempt
            if (context.Request.Path.StartsWithSegments("/auto-login") || 
                context.Properties.Items.ContainsKey("prompt"))
            {
                context.ProtocolMessage.Prompt = "none"; // Silent authentication
            }
            return Task.CompletedTask;
        };
        
        // Handle silent authentication failures gracefully
        options.Events.OnAuthenticationFailed = context =>
        {
            if (context.Exception?.Message.Contains("login_required") == true ||
                context.Exception?.Message.Contains("interaction_required") == true ||
                context.Exception?.Message.Contains("consent_required") == true)
            {
                // Redirect to normal login if silent auth fails
                context.Response.Redirect("/?autoLoginFailed=true");
                context.HandleResponse();
            }
            return Task.CompletedTask;
        };

        // Handle successful authentication
        options.Events.OnTicketReceived = context =>
        {
            // Redirect to home page after successful authentication
            if (context.Properties?.RedirectUri == "/")
            {
                context.Response.Redirect("/");
            }
            return Task.CompletedTask;
        };
    })
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddDistributedTokenCaches(); // Use distributed cache for better persistence

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// Configure cookie authentication for persistence
builder.Services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(7); // Keep user logged in for 7 days
    options.SlidingExpiration = true; // Refresh the expiration time on each request
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
});

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor()
    .AddMicrosoftIdentityConsentHandler();

builder.Services.AddScoped<GraphService>();
builder.Services.AddScoped<AppRegistrationService>();
builder.Services.AddScoped<SchemaService>();
builder.Services.AddScoped<AzureBootstrapService>();
builder.Services.AddSingleton<WebTerminalService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

// WebSocket endpoint for terminal
app.Map("/ws/terminal", async (HttpContext context, WebTerminalService terminalService, ILogger<Program> logger) =>
{
    logger.LogInformation("WebSocket request received at /ws/terminal");
    
    if (context.WebSockets.IsWebSocketRequest)
    {
        var sessionId = context.Request.Query["sessionId"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        logger.LogInformation("Accepting WebSocket connection for session {SessionId}", sessionId);
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await terminalService.HandleWebSocketAsync(webSocket, sessionId);
    }
    else
    {
        logger.LogWarning("Non-WebSocket request received at /ws/terminal");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.Run();
