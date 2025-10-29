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

// Create external item from raw JSON (auto-transform)
externalItems.MapPost("/raw", async (
    [FromBody] JsonDocument jsonDoc,
    GraphIngestionService ingestionService,
    SchemaValidationService validationService,
    ILogger<Program> logger) =>
{
    try
    {
        var root = jsonDoc.RootElement;
        
        // Extract ID from the JSON (required field)
        if (!root.TryGetProperty("id", out var idElement))
        {
            return Results.BadRequest(new ExternalItemResponse
            {
                Success = false,
                ErrorMessage = "'id' field is required in the JSON payload"
            });
        }
        
        var itemId = idElement.GetString();
        logger.LogInformation("Creating external item from raw JSON with ID: {ItemId}", itemId);
        
        // Transform raw JSON to ExternalItemRequest
        var request = new ExternalItemRequest
        {
            Id = itemId!,
            Properties = new Dictionary<string, object>(),
            Content = string.Empty
        };
        
        // Flatten and add all properties (except id)
        var contentBuilder = new System.Text.StringBuilder();
        FlattenJsonToProperties(root, string.Empty, request.Properties, contentBuilder, new HashSet<string> { "id", "content", "acl", "acls", "properties" });
        
        // Log the properties that were created
        logger.LogInformation("Flattened JSON to {PropertyCount} properties: {PropertyNames}", 
            request.Properties.Count, 
            string.Join(", ", request.Properties.Keys));
        
        // Set content (combine searchable fields)
        request.Content = contentBuilder.ToString().Trim();
        logger.LogInformation("Generated content with length: {ContentLength}", request.Content.Length);
        
        // Ensure required fields exist - add defaults if missing
        if (!request.Properties.ContainsKey("url"))
        {
            // Generate a default URL if not provided
            request.Properties["url"] = $"https://example.com/items/{itemId}";
            logger.LogWarning("Missing 'url' field, added default: {Url}", request.Properties["url"]);
        }
        
        // Ensure content is not empty (Graph may reject empty content)
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            request.Content = request.Properties.ContainsKey("title") 
                ? request.Properties["title"]?.ToString() ?? itemId 
                : itemId;
            logger.LogWarning("Empty content, using fallback: {Content}", request.Content);
        }

        // Align property set with registered schema (remove unknowns, coerce types)
        try
        {
            var schemaConfig = validationService.GetSchemaConfiguration();
            if (schemaConfig != null)
            {
                // Field remapping heuristics (map common alternate names to schema fields if missing)
                // publishedDate -> lastUpdated, score -> price, isActive -> inStock
                if (request.Properties.ContainsKey("publishedDate") && !request.Properties.ContainsKey("lastUpdated") && schemaConfig.Fields.ContainsKey("lastUpdated"))
                {
                    request.Properties["lastUpdated"] = request.Properties["publishedDate"];
                    request.Properties.Remove("publishedDate");
                }
                if (request.Properties.ContainsKey("score") && !request.Properties.ContainsKey("price") && schemaConfig.Fields.ContainsKey("price"))
                {
                    request.Properties["price"] = request.Properties["score"];
                    request.Properties.Remove("score");
                }
                if (request.Properties.ContainsKey("isActive") && !request.Properties.ContainsKey("inStock") && schemaConfig.Fields.ContainsKey("inStock"))
                {
                    request.Properties["inStock"] = request.Properties["isActive"];
                    request.Properties.Remove("isActive");
                }

                var originalKeys = request.Properties.Keys.ToList();
                foreach (var key in originalKeys)
                {
                    // Allow OData type annotation passthrough
                    if (key.EndsWith("@odata.type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!schemaConfig.Fields.ContainsKey(key))
                    {
                        logger.LogWarning("Property '{Prop}' not found in schema. Removing before send.", key);
                        request.Properties.Remove(key);
                        continue;
                    }

                    var fieldType = schemaConfig.Fields[key].Type; // String, Int64, Double, Boolean, DateTime, StringCollection
                    var value = request.Properties[key];

                    switch (fieldType)
                    {
                        case "StringCollection":
                            // If we have a single string, wrap it; if we have a mixed object array, convert to strings
                            if (value is string single)
                            {
                                request.Properties[key] = new List<string> { single };
                            }
                            else if (value is IEnumerable<string> strEnum)
                            {
                                // ensure concrete list for serializer
                                request.Properties[key] = strEnum.ToList();
                            }
                            else if (value is IEnumerable<object> objEnum)
                            {
                                request.Properties[key] = objEnum.Select(o => o?.ToString() ?? string.Empty).ToList();
                            }
                            // Add OData type annotation for collection of strings
                            var annotationKey = key + "@odata.type";
                            if (!request.Properties.ContainsKey(annotationKey))
                            {
                                request.Properties[annotationKey] = "Collection(String)";
                            }
                            break;
                        case "String":
                            // If we have an array, flatten to space-delimited string to avoid Graph deserialization error
                            if (value is IEnumerable<string> arrStrings)
                            {
                                var flattened = string.Join(" ", arrStrings.Where(s => !string.IsNullOrWhiteSpace(s)));
                                request.Properties[key] = flattened;
                                logger.LogWarning("Coerced array value for '{Prop}' to string: {Value}", key, flattened);
                            }
                            break;
                        case "DateTime":
                            if (value is string dtStr && DateTimeOffset.TryParse(dtStr, out var dto))
                            {
                                request.Properties[key] = dto; // Kiota should serialize DateTimeOffset correctly
                            }
                            break;
                        case "Double":
                            if (value is string dblStr && double.TryParse(dblStr, out var dbl))
                            {
                                request.Properties[key] = dbl;
                            }
                            break;
                        case "Int64":
                            if (value is string lngStr && long.TryParse(lngStr, out var lng))
                            {
                                request.Properties[key] = lng;
                            }
                            break;
                        case "Boolean":
                            if (value is string boolStr && bool.TryParse(boolStr, out var b))
                            {
                                request.Properties[key] = b;
                            }
                            break;
                    }
                }

                // Optional: add iconUrl if schema expects it and it's missing
                if (schemaConfig.Fields.ContainsKey("iconUrl") && !request.Properties.ContainsKey("iconUrl"))
                {
                    request.Properties["iconUrl"] = "https://example.com/default-icon.png";
                    logger.LogInformation("Added default iconUrl property.");
                }
            }
        }
        catch (Exception alignEx)
        {
            logger.LogWarning(alignEx, "Schema alignment phase encountered an error but will proceed.");
        }
        
        // Check for explicit ACLs in the JSON
        if (root.TryGetProperty("acls", out var aclsElement) && aclsElement.ValueKind == JsonValueKind.Array)
        {
            request.Acls = new List<ExternalItemAcl>();
            foreach (var aclItem in aclsElement.EnumerateArray())
            {
                request.Acls.Add(new ExternalItemAcl
                {
                    Type = aclItem.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "everyone" : "everyone",
                    Value = aclItem.TryGetProperty("value", out var valueEl) ? valueEl.GetString() ?? "everyone" : "everyone",
                    AccessType = aclItem.TryGetProperty("accessType", out var accessEl) ? accessEl.GetString() ?? "grant" : "grant"
                });
            }
        }
        else
        {
            // Use default ACLs from schema configuration if available
            var schemaConfig = validationService.GetSchemaConfiguration();
            if (schemaConfig?.DefaultAcls != null && schemaConfig.DefaultAcls.Count > 0)
            {
                request.Acls = schemaConfig.DefaultAcls;
                logger.LogInformation("Applied default ACLs from schema configuration: {AclCount} ACLs", schemaConfig.DefaultAcls.Count);
            }
            else
            {
                // Fallback to "everyone" in tenant
                request.Acls = new List<ExternalItemAcl>
                {
                    new ExternalItemAcl
                    {
                        Type = "everyone",
                        Value = "everyone",
                        AccessType = "grant"
                    }
                };
                logger.LogInformation("No default ACLs configured, using 'everyone' in tenant");
            }
        }
        
        // Validate the transformed request
        var validationResult = validationService.ValidateExternalItem(request);
        if (!validationResult.IsValid)
        {
            return Results.BadRequest(new ExternalItemResponse
            {
                Success = false,
                ErrorMessage = "Validation failed after transformation",
                ValidationErrors = validationResult.Errors
            });
        }
        
        // Create the item
        var result = await ingestionService.CreateExternalItemAsync(request);
        
        if (result.Success)
        {
            logger.LogInformation("Successfully created external item from raw JSON: {ItemId}", itemId);
            return Results.Ok(result);
        }
        else
        {
            logger.LogError("Failed to create external item {ItemId}: {Error}", itemId, result.ErrorMessage);
            return Results.Problem(
                statusCode: 500,
                detail: result.ErrorMessage,
                title: "Failed to create external item");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Exception occurred while creating external item from raw JSON");
        return Results.Problem(
            statusCode: 500,
            detail: ex.Message,
            title: "Internal server error");
    }
})
.WithName("CreateExternalItemFromRawJson")
.WithSummary("Create external item from raw JSON")
.WithDescription("Accepts raw JSON payload and automatically transforms it to the external item format (same as schema creation)")
.Produces<ExternalItemResponse>(200)
.Produces<ExternalItemResponse>(400)
.Produces<ExternalItemResponse>(500);

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

// Helper methods for JSON flattening
static void FlattenJsonToProperties(JsonElement element, string prefix, Dictionary<string, object> properties, System.Text.StringBuilder contentBuilder, HashSet<string> reservedNames)
{
    foreach (var property in element.EnumerateObject())
    {
        // Use the same concatenation logic as schema creation - no camelCase transformation
        var propertyName = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}{property.Name}";
        if (reservedNames.Contains(property.Name.ToLowerInvariant())) continue;
        propertyName = NormalizePropertyName(propertyName);
        
        switch (property.Value.ValueKind)
        {
            case JsonValueKind.Object:
                FlattenJsonToProperties(property.Value, propertyName, properties, contentBuilder, reservedNames);
                break;
            case JsonValueKind.Array:
                var arrayValues = new List<object>();
                var isStringArray = true;
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var strValue = item.GetString();
                        if (strValue != null) { arrayValues.Add(strValue); contentBuilder.Append(strValue).Append(" "); }
                    }
                    else if (item.ValueKind == JsonValueKind.Number) { isStringArray = false; arrayValues.Add(item.GetDouble()); }
                    else if (item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.False) { isStringArray = false; arrayValues.Add(item.GetBoolean()); }
                    else { isStringArray = false; }
                }
                if (arrayValues.Count > 0) properties[propertyName] = isStringArray ? arrayValues.Cast<string>().ToArray() : arrayValues.ToArray();
                break;
            case JsonValueKind.String:
                var stringValue = property.Value.GetString();
                if (!string.IsNullOrEmpty(stringValue)) { properties[propertyName] = stringValue; contentBuilder.Append(stringValue).Append(" "); }
                break;
            case JsonValueKind.Number:
                properties[propertyName] = property.Value.GetDouble();
                contentBuilder.Append(property.Value.GetDouble()).Append(" ");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                properties[propertyName] = property.Value.GetBoolean();
                break;
        }
    }
}

static string NormalizePropertyName(string name)
{
    if (string.IsNullOrEmpty(name)) return name;
    var normalized = new System.Text.StringBuilder();
    foreach (var c in name)
    {
        if (char.IsLetterOrDigit(c)) normalized.Append(c);
        else if (c == '_' || c == '-') normalized.Append('_');
    }
    var result = normalized.ToString();
    if (result.Length > 0 && !char.IsLetter(result[0])) result = "field_" + result;
    return result;
}

app.Run();
