using IngestionService.Services;
using IngestionService.Models;
using Microsoft.OpenApi.Models;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON options
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

// Add API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "Copilot Connector Ingestion API", 
        Version = "v1",
        Description = "Minimal API for ingesting external items into Microsoft Graph External Connections"
    });
    
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key needed to access the endpoints",
        In = ParameterLocation.Header,
        Name = "X-API-Key",
        Type = SecuritySchemeType.ApiKey
    });
    
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            new string[] { }
        }
    });
});

// Add health checks
builder.Services.AddHealthChecks();

// Add application services
builder.Services.AddSingleton<GraphIngestionService>();
builder.Services.AddSingleton<SchemaValidationService>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
// Enable Swagger for container deployments to allow testing and API exploration
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Copilot Connector Ingestion API v1");
    c.RoutePrefix = "swagger"; // Serve Swagger UI at /swagger
});

// Only use HTTPS redirection in development when not in a container
if (app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("IN_CONTAINER"))
{
    app.UseHttpsRedirection();
}

app.UseCors();

// Initialize services
var graphService = app.Services.GetRequiredService<GraphIngestionService>();
var validationService = app.Services.GetRequiredService<SchemaValidationService>();
await graphService.InitializeAsync();
await validationService.InitializeAsync();

// ===== MINIMAL API ENDPOINTS =====

var externalItems = app.MapGroup("/api/external-items")
    .WithTags("External Items")
    .WithOpenApi();

// Create external item
externalItems.MapPost("/", async (
    ExternalItemRequest request,
    GraphIngestionService ingestionService,
    SchemaValidationService validationService,
    ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Creating external item with ID: {ItemId}", request.Id);
        
        // Validate the request
        var validationResult = validationService.ValidateExternalItem(request);
        if (!validationResult.IsValid)
        {
            return Results.BadRequest(new ExternalItemResponse
            {
                Success = false,
                ErrorMessage = "Validation failed",
                ValidationErrors = validationResult.Errors
            });
        }
        
        // Create the item
        var result = await ingestionService.CreateExternalItemAsync(request);
        
        if (result.Success)
        {
            logger.LogInformation("Successfully created external item: {ItemId}", request.Id);
            return Results.Ok(result);
        }
        else
        {
            logger.LogError("Failed to create external item {ItemId}: {Error}", request.Id, result.ErrorMessage);
            return Results.Problem(
                statusCode: 500,
                detail: result.ErrorMessage,
                title: "Failed to create external item");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Exception occurred while creating external item: {ItemId}", request.Id);
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Internal server error");
    }
})
.WithName("CreateExternalItem")
.WithSummary("Create a new external item")
.WithDescription("Creates a new external item in the Microsoft Graph External Connection")
.Produces<ExternalItemResponse>(200)
.Produces<ExternalItemResponse>(400)
.Produces<ExternalItemResponse>(500);

// Update external item
externalItems.MapPut("/{id}", async (
    string id,
    ExternalItemRequest request,
    GraphIngestionService ingestionService,
    SchemaValidationService validationService,
    ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Updating external item with ID: {ItemId}", id);
        
        // Ensure IDs match
        if (request.Id != id)
        {
            request.Id = id;
        }
        
        // Validate the request
        var validationResult = validationService.ValidateExternalItem(request);
        if (!validationResult.IsValid)
        {
            return Results.BadRequest(new ExternalItemResponse
            {
                Success = false,
                ErrorMessage = "Validation failed",
                ValidationErrors = validationResult.Errors
            });
        }
        
        // Update the item
        var result = await ingestionService.UpdateExternalItemAsync(request);
        
        if (result.Success)
        {
            logger.LogInformation("Successfully updated external item: {ItemId}", id);
            return Results.Ok(result);
        }
        else
        {
            logger.LogError("Failed to update external item {ItemId}: {Error}", id, result.ErrorMessage);
            
            // Check if it's a not found error
            if (result.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Results.NotFound(result);
            }
            
            return Results.Problem(
                statusCode: 500,
                detail: result.ErrorMessage,
                title: "Failed to update external item");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Exception occurred while updating external item: {ItemId}", id);
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Internal server error");
    }
})
.WithName("UpdateExternalItem")
.WithSummary("Update an existing external item")  
.WithDescription("Updates an existing external item in the Microsoft Graph External Connection")
.Produces<ExternalItemResponse>(200)
.Produces<ExternalItemResponse>(400)
.Produces<ExternalItemResponse>(404)
.Produces<ExternalItemResponse>(500);

// Delete external item
externalItems.MapDelete("/{id}", async (
    string id,
    GraphIngestionService ingestionService,
    ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Deleting external item with ID: {ItemId}", id);
        
        var result = await ingestionService.DeleteExternalItemAsync(id);
        
        if (result.Success)
        {
            logger.LogInformation("Successfully deleted external item: {ItemId}", id);
            return Results.Ok(result);
        }
        else
        {
            logger.LogError("Failed to delete external item {ItemId}: {Error}", id, result.ErrorMessage);
            
            // Check if it's a not found error
            if (result.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Results.NotFound(result);
            }
            
            return Results.Problem(
                statusCode: 500,
                detail: result.ErrorMessage,
                title: "Failed to delete external item");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Exception occurred while deleting external item: {ItemId}", id);
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Internal server error");
    }
})
.WithName("DeleteExternalItem")
.WithSummary("Delete an external item")
.WithDescription("Deletes an external item from the Microsoft Graph External Connection")
.Produces<ExternalItemResponse>(200)
.Produces<ExternalItemResponse>(404)
.Produces<ExternalItemResponse>(500);

// Batch create/update external items
externalItems.MapPost("/batch", async (
    BatchExternalItemRequest request,
    GraphIngestionService ingestionService,
    ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("Processing batch request with {Count} items", request.Items.Count);
        
        if (request.Items.Count == 0)
        {
            return Results.BadRequest(new BatchExternalItemResponse
            {
                Success = false,
                ErrorCount = 1,
                Results = new List<ExternalItemResponse>
                {
                    new ExternalItemResponse
                    {
                        Success = false,
                        ErrorMessage = "No items provided in batch request"
                    }
                }
            });
        }
        
        if (request.Items.Count > 100)
        {
            return Results.BadRequest(new BatchExternalItemResponse
            {
                Success = false,
                ErrorCount = 1,
                Results = new List<ExternalItemResponse>
                {
                    new ExternalItemResponse
                    {
                        Success = false,
                        ErrorMessage = "Batch size cannot exceed 100 items"
                    }
                }
            });
        }
        
        var result = await ingestionService.BatchCreateExternalItemsAsync(request.Items);
        
        logger.LogInformation("Batch processing completed: {SuccessCount} successful, {ErrorCount} errors", 
            result.SuccessCount, result.ErrorCount);
        
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Exception occurred during batch processing");
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Batch processing failed");
    }
})
.WithName("BatchCreateExternalItems")
.WithSummary("Create or update multiple external items")
.WithDescription("Processes a batch of external items (up to 100 items)")
.Produces<BatchExternalItemResponse>(200)
.Produces<BatchExternalItemResponse>(400)
.Produces<BatchExternalItemResponse>(500);

// Validate external item
externalItems.MapPost("/validate", (
    ExternalItemRequest request,
    SchemaValidationService validationService) =>
{
    var validationResult = validationService.ValidateExternalItem(request);
    
    return Results.Ok(new ExternalItemResponse
    {
        Success = validationResult.IsValid,
        ErrorMessage = validationResult.IsValid ? null : "Validation failed",
        ValidationErrors = validationResult.Errors,
        ItemId = request.Id
    });
})
.WithName("ValidateExternalItem")
.WithSummary("Validate an external item")
.WithDescription("Validates an external item against the schema without creating it")
.Produces<ExternalItemResponse>(200);

// Get schema configuration
externalItems.MapGet("/schema", (SchemaValidationService validationService) =>
{
    var schema = validationService.GetSchemaConfiguration();
    return Results.Ok(schema);
})
.WithName("GetSchema")
.WithSummary("Get schema configuration")
.WithDescription("Returns the schema configuration for this external connection")
.Produces<SchemaConfiguration>(200);

// ===== HEALTH CHECK ENDPOINTS =====

app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Timestamp = DateTime.UtcNow,
    Version = "1.0.0"
}))
.WithName("BasicHealthCheck")
.WithSummary("Basic health check")
.WithTags("Health")
.WithOpenApi();

app.MapGet("/health/detailed", async (
    GraphIngestionService ingestionService,
    ILogger<Program> logger) =>
{
    try
    {
        var graphConnected = await ingestionService.TestGraphConnectivityAsync();
        
        return Results.Ok(new
        {
            Status = graphConnected ? "Healthy" : "Degraded",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0",
            Services = new
            {
                GraphConnection = graphConnected ? "Connected" : "Disconnected"
            }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Health check failed");
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Health check failed");
    }
})
.WithName("DetailedHealthCheck")
.WithSummary("Detailed health check")
.WithDescription("Includes connectivity tests to Microsoft Graph")
.WithTags("Health")
.WithOpenApi();

// ===== SERVICE INFO ENDPOINTS =====

app.MapGet("/info", (IConfiguration config) =>
{
    var connectionId = config["CONNECTION_ID"];
    var servicePort = config["SERVICE_PORT"];
    
    return Results.Ok(new
    {
        ServiceName = "Copilot Connector Ingestion Service",
        Version = "1.0.0",
        ConnectionId = connectionId,
        Port = servicePort,
        Timestamp = DateTime.UtcNow,
        Endpoints = new
        {
            CreateItem = "/api/external-items",
            UpdateItem = "/api/external-items/{id}",
            DeleteItem = "/api/external-items/{id}",
            BatchItems = "/api/external-items/batch",
            ValidateItem = "/api/external-items/validate",
            GetSchema = "/api/external-items/schema",
            Health = "/health",
            Documentation = "/swagger"
        }
    });
})
.WithName("ServiceInfo")
.WithSummary("Service information")
.WithDescription("Returns service information and available endpoints")
.WithTags("Info")
.WithOpenApi();

app.Run();