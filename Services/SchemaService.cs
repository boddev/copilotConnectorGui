using Microsoft.Graph;
using CopilotConnectorGui.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Azure.Identity;

namespace CopilotConnectorGui.Services
{
    public class SchemaService
    {
        private readonly GraphService _graphService;
        private readonly ILogger<SchemaService> _logger;
        private readonly ContainerManagementService _containerService;
        private readonly JsonFieldParserService _jsonParser;
        private readonly SemanticLabelMappingService _semanticMapper;

        public SchemaService(
            GraphService graphService, 
            ILogger<SchemaService> logger,
            ContainerManagementService containerService,
            JsonFieldParserService jsonParser,
            SemanticLabelMappingService semanticMapper)
        {
            _graphService = graphService;
            _logger = logger;
            _containerService = containerService;
            _jsonParser = jsonParser;
            _semanticMapper = semanticMapper;
        }

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionWithUserContextAsync(
            ClaimsPrincipal user,
            string jsonSample,
            string connectionName,
            string connectionDescription,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting External Connection creation with user context");
                progress?.Report("Initializing External Connection creation...");
                
                // Use user's delegated permissions instead of application permissions
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);
                
                _logger.LogInformation("Parsing JSON sample to create schema");
                progress?.Report("Parsing JSON schema and normalizing property names...");
                // Parse JSON sample to create schema
                var schema = CreateSchemaFromJson(jsonSample);
                
                // Create connection ID that fits 3-32 character limit
                var connectionId = $"copilot{Guid.NewGuid():N}"[..20]; // First 20 chars: "copilot" + 13 chars from GUID
                _logger.LogInformation("Generated connection ID: {ConnectionId}", connectionId);
                
                // Create the external connection using HTTP client with user context
                var connection = new
                {
                    id = connectionId,
                    name = connectionName,
                    description = connectionDescription
                };

                var connectionJson = JsonConvert.SerializeObject(connection);
                var connectionContent = new StringContent(connectionJson, System.Text.Encoding.UTF8, "application/json");
                
                _logger.LogInformation("Creating External Connection via Microsoft Graph API");
                progress?.Report("Creating External Connection with Microsoft Graph...");
                // Create connection
                var connectionResponse = await httpClient.PostAsync("external/connections", connectionContent);
                
                if (!connectionResponse.IsSuccessStatusCode)
                {
                    var errorContent = await connectionResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create External Connection: {StatusCode} - {Error}", connectionResponse.StatusCode, errorContent);
                    throw new InvalidOperationException($"Failed to create external connection: {connectionResponse.StatusCode} - {errorContent}");
                }

                _logger.LogInformation("External Connection created successfully. Waiting for connection to stabilize...");
                progress?.Report("External Connection created! Preparing schema registration...");
                // Wait a moment for connection to be created
                await Task.Delay(2000);

                _logger.LogInformation("Creating schema with {PropertyCount} properties", schema.Values.Count);
                // Create and register the schema
                var schemaRequest = new
                {
                    baseType = "microsoft.graph.externalItem",
                    properties = schema.Values.Select(p => new
                    {
                        name = p.Name,
                        type = p.Type?.ToString().ToLower() ?? "string",
                        isSearchable = p.IsSearchable,
                        isQueryable = p.IsQueryable,
                        isRetrievable = p.IsRetrievable
                    }).ToList()
                };

                var schemaJson = JsonConvert.SerializeObject(schemaRequest);
                var schemaContent = new StringContent(schemaJson, System.Text.Encoding.UTF8, "application/json");

                _logger.LogInformation("Registering schema with Microsoft Graph API");
                progress?.Report("Submitting schema to Microsoft Graph for registration...");
                var schemaResponse = await httpClient.PatchAsync($"external/connections/{connectionId}/schema", schemaContent);
                
                if (!schemaResponse.IsSuccessStatusCode)
                {
                    var errorContent = await schemaResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create schema: {StatusCode} - {Error}", schemaResponse.StatusCode, errorContent);
                    throw new InvalidOperationException($"Failed to create schema: {schemaResponse.StatusCode} - {errorContent}");
                }

                _logger.LogInformation("Schema submitted successfully. Waiting for schema registration to complete...");
                progress?.Report("Schema submitted! Monitoring registration status...");
                // Wait for schema registration
                await WaitForSchemaRegistrationHttp(httpClient, connectionId, progress, cancellationToken);

                _logger.LogInformation("External Connection and Schema creation completed successfully. Connection ID: {ConnectionId}", connectionId);
                return new SchemaCreationResult
                {
                    SchemaId = connectionId,
                    ConnectionId = connectionId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating schema and connection with user context");
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionWithDelegatedPermissionsAsync(
            ClaimsPrincipal user,
            string jsonSample,
            string connectionName,
            string connectionDescription)
        {
            try
            {
                _logger.LogInformation("Starting External Connection creation with delegated permissions");
                
                // Use the authenticated user's context with delegated permissions
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);
                
                // Parse JSON sample to create schema
                var schema = CreateSchemaFromJson(jsonSample);
                // Create connection ID that fits 3-32 character limit
                var connectionId = $"copilot{Guid.NewGuid():N}"[..20]; // First 20 chars: "copilot" + 13 chars from GUID
                
                _logger.LogInformation("Creating External Connection with ID: {ConnectionId}", connectionId);
                
                // Create the external connection using REST API
                var connection = new
                {
                    id = connectionId,
                    name = connectionName,
                    description = connectionDescription
                };

                var connectionJson = JsonConvert.SerializeObject(connection);
                var connectionContent = new StringContent(connectionJson, System.Text.Encoding.UTF8, "application/json");

                // Create connection
                try
                {
                    var connectionResponse = await httpClient.PostAsync("external/connections", connectionContent);
                    var connectionResponseContent = await connectionResponse.Content.ReadAsStringAsync();
                    
                    if (!connectionResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("Failed to create External Connection: {Error}", connectionResponseContent);
                        
                        var errorMessage = "Failed to create External Connection. ";
                        
                        if (connectionResponseContent.Contains("Current authenticated context is not valid") || 
                            connectionResponseContent.Contains("user sign-in") ||
                            connectionResponseContent.Contains("Insufficient privileges"))
                        {
                            errorMessage += "The OAuth application needs admin consent for External Connection permissions.\n\n" +
                                          "🔧 SOLUTION:\n" +
                                          "1. Contact your Azure AD administrator to grant admin consent for the OAuth application\n" +
                                          "2. Or use the Azure CLI Bootstrap option which may have different permission requirements";
                        }
                        else
                        {
                            errorMessage += $"HTTP {connectionResponse.StatusCode}: {connectionResponseContent}";
                        }
                        
                        return new SchemaCreationResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                    
                    _logger.LogInformation("External Connection created successfully");
                }
                catch (Exception connectionEx)
                {
                    _logger.LogError(connectionEx, "Failed to create External Connection");
                    
                    var errorMessage = "Failed to create External Connection. ";
                    
                    if (connectionEx.Message.Contains("Current authenticated context is not valid") || 
                        connectionEx.Message.Contains("user sign-in") ||
                        connectionEx.Message.Contains("Insufficient privileges"))
                    {
                        errorMessage += "The OAuth application needs admin consent for External Connection permissions.\n\n" +
                                      "🔧 SOLUTION:\n" +
                                      "1. Contact your Azure AD administrator to grant admin consent for the OAuth application\n" +
                                      "2. Or try using the Azure CLI Bootstrap option";
                    }
                    else
                    {
                        errorMessage += connectionEx.Message;
                    }
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = errorMessage
                    };
                }

                // Create schema
                try
                {
                    // Create proper schema format for External Connection API
                    var schemaRequest = new
                    {
                        baseType = "microsoft.graph.externalItem",
                        properties = schema.Select(kvp => new
                        {
                            name = kvp.Value.Name,
                            type = GetApiPropertyType(kvp.Value.Type),
                            isSearchable = kvp.Value.IsSearchable,
                            isQueryable = kvp.Value.IsQueryable,
                            isRetrievable = kvp.Value.IsRetrievable,
                            isRefinable = kvp.Value.IsRefinable
                        }).ToArray()
                    };

                    var schemaJson = JsonConvert.SerializeObject(schemaRequest);
                    _logger.LogInformation("Schema JSON being sent: {SchemaJson}", schemaJson);
                    var schemaContent = new StringContent(schemaJson, System.Text.Encoding.UTF8, "application/json");

                    var schemaResponse = await httpClient.PostAsync($"external/connections/{connectionId}/schema", schemaContent);
                    var schemaResponseContent = await schemaResponse.Content.ReadAsStringAsync();
                    
                    if (!schemaResponse.IsSuccessStatusCode)
                    {
                        _logger.LogError("Failed to create schema: {Error}", schemaResponseContent);
                        return new SchemaCreationResult
                        {
                            Success = false,
                            ErrorMessage = $"External Connection created but schema creation failed: HTTP {schemaResponse.StatusCode}: {schemaResponseContent}"
                        };
                    }
                    
                    _logger.LogInformation("Schema created successfully");
                    
                    return new SchemaCreationResult
                    {
                        Success = true,
                        ConnectionId = connectionId,
                        ErrorMessage = $"External Connection '{connectionName}' and schema created successfully!"
                    };
                }
                catch (Exception schemaEx)
                {
                    _logger.LogError(schemaEx, "Failed to create schema");
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = $"External Connection created but schema creation failed: {schemaEx.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CreateSchemaAndConnectionWithDelegatedPermissionsAsync");
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionAsync(
            string tenantId, 
            string clientId, 
            string clientSecret, 
            string jsonSample,
            string connectionName,
            string connectionDescription)
        {
            try
            {
                _logger.LogInformation("Starting External Connection creation with client credentials");
                _logger.LogInformation("Tenant ID: {TenantId}, Client ID: {ClientId}", tenantId, clientId);
                
                var graphClient = _graphService.GetGraphServiceClientForApp(tenantId, clientId, clientSecret);
                
                _logger.LogInformation("Proceeding directly to External Connection creation (authentication will be tested during actual API calls)");
                
                // Parse JSON sample to create schema
                var schema = CreateSchemaFromJson(jsonSample);
                // Create connection ID that fits 3-32 character limit
                var connectionId = $"copilot{Guid.NewGuid():N}"[..20]; // First 20 chars: "copilot" + 13 chars from GUID
                
                _logger.LogInformation("Creating External Connection with ID: {ConnectionId}", connectionId);
                
                // Create the external connection
                var connection = new Microsoft.Graph.Models.ExternalConnectors.ExternalConnection
                {
                    Id = connectionId,
                    Name = connectionName,
                    Description = connectionDescription,
                    Configuration = new Microsoft.Graph.Models.ExternalConnectors.Configuration
                    {
                        AuthorizedAppIds = new List<string> { clientId }
                    }
                };

                // Create connection
                try
                {
                    await graphClient.External.Connections.PostAsync(connection);
                    _logger.LogInformation("External Connection created successfully");
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
                {
                    _logger.LogError(odataEx, "Failed to create External Connection - ODataError");
                    _logger.LogError("ODataError Code: {Code}, Message: {Message}", 
                        odataEx.Error?.Code, odataEx.Error?.Message);
                    
                    var errorMessage = "Failed to create External Connection. ";
                    var errorCode = odataEx.Error?.Code ?? "";
                    var errorMsg = odataEx.Error?.Message ?? odataEx.Message;
                    
                    if (errorMsg.Contains("Current authenticated context is not valid") || 
                        errorMsg.Contains("user sign-in") ||
                        errorMsg.Contains("Insufficient privileges"))
                    {
                        errorMessage += "The app registration needs admin consent for External Connection permissions.\n\n" +
                                      "🔧 SOLUTION:\n" +
                                      $"1. Grant admin consent: https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}\n" +
                                      "2. Wait 2-3 minutes for permissions to propagate\n" +
                                      "3. Use the 'Retry Connection Creation' button\n\n" +
                                      "The app registration was created successfully, but it needs administrator consent for:\n" +
                                      "• ExternalConnection.ReadWrite.OwnedBy (Application permission)\n" +
                                      "• ExternalItem.ReadWrite.OwnedBy (Application permission)\n\n" +
                                      "Note: Application permissions always require admin consent - this is a Microsoft security requirement.";
                    }
                    else if (errorMsg.Contains("invalid_client") || errorMsg.Contains("AADSTS70002"))
                    {
                        errorMessage += "The client credentials are invalid. Please verify:\n" +
                                      "• The Client ID and Client Secret are correct\n" +
                                      "• The app registration exists in this tenant\n" +
                                      "• The client secret hasn't expired";
                    }
                    else if (errorMsg.Contains("AADSTS7000215"))
                    {
                        errorMessage += "Invalid client secret. Please:\n" +
                                      "1. Wait 2-3 minutes for the new secret to become active\n" +
                                      "2. Try the 'Retry Connection Creation' button\n" +
                                      "3. Verify the client secret value (not ID) was used";
                    }
                    else if (errorMsg.Contains("insufficient_claims") || 
                             errorMsg.Contains("AADSTS65001") ||
                             errorMsg.Contains("Forbidden") ||
                             errorMsg.Contains("403"))
                    {
                        errorMessage += "Admin consent is required for External Connection permissions.\n\n" +
                                      $"🔧 SOLUTION:\n" +
                                      $"1. Grant admin consent: https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}\n" +
                                      "2. Wait 2-3 minutes for permissions to propagate\n" +
                                      "3. Use the 'Retry Connection Creation' button";
                    }
                    else
                    {
                        errorMessage += $"Unexpected error: {errorMsg}\n\n" +
                                      "This could be due to:\n" +
                                      "• Missing admin consent\n" +
                                      "• Timing issues with newly created credentials\n" +
                                      "• Tenant-specific restrictions\n\n" +
                                      $"Try granting admin consent: https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}";
                    }
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = errorMessage
                    };
                }

                // Wait a moment for connection to be created
                await Task.Delay(2000);

                _logger.LogInformation("Creating schema with {PropertyCount} properties", schema.Values.Count);

                // Log each property for debugging
                foreach (var prop in schema.Values)
                {
                    var labelsStr = prop.Labels != null && prop.Labels.Any() 
                        ? string.Join(", ", prop.Labels.Select(l => l.ToString())) 
                        : "None";
                    
                    _logger.LogInformation("Schema Property: Name={Name}, Type={Type}, IsSearchable={IsSearchable}, IsQueryable={IsQueryable}, IsRetrievable={IsRetrievable}, IsRefinable={IsRefinable}, Labels=[{Labels}]",
                        prop.Name, prop.Type, prop.IsSearchable, prop.IsQueryable, prop.IsRetrievable, prop.IsRefinable, labelsStr);
                        
                    // Validate property name (must be alphanumeric + underscore only)
                    if (!System.Text.RegularExpressions.Regex.IsMatch(prop.Name ?? "", @"^[a-zA-Z0-9_]+$"))
                    {
                        _logger.LogWarning("Property name '{Name}' contains invalid characters. Only alphanumeric and underscore allowed.", prop.Name);
                    }
                }

                // Validate we have at least title, url, and iconUrl with proper labels (required for Copilot)
                var titleProp = schema.Values.FirstOrDefault(p => p.Name == "title");
                var urlProp = schema.Values.FirstOrDefault(p => p.Name == "url");
                var iconUrlProp = schema.Values.FirstOrDefault(p => p.Name == "iconUrl");
                
                if (titleProp == null)
                {
                    var errorMsg = "Schema must contain a 'title' property with Title label";
                    _logger.LogError(errorMsg);
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }
                
                if (urlProp == null)
                {
                    var errorMsg = "Schema must contain a 'url' property with Url label";
                    _logger.LogError(errorMsg);
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                if (iconUrlProp == null)
                {
                    var errorMsg = "Schema must contain an 'iconUrl' property with IconUrl label (required for Copilot)";
                    _logger.LogError(errorMsg);
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                // Create and register the schema
                var schemaRequest = new Microsoft.Graph.Models.ExternalConnectors.Schema
                {
                    BaseType = "microsoft.graph.externalItem",
                    Properties = schema.Values.ToList()
                };

                _logger.LogInformation("Submitting schema registration for connection {ConnectionId}", connectionId);
                _logger.LogInformation("Schema JSON being sent: {SchemaJson}", System.Text.Json.JsonSerializer.Serialize(schemaRequest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                
                try
                {
                    await graphClient.External.Connections[connectionId].Schema.PatchAsync(schemaRequest);
                    _logger.LogInformation("Schema registration submitted");
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError schemaOdataEx)
                {
                    _logger.LogError(schemaOdataEx, "Failed to create schema - ODataError");
                    _logger.LogError("Schema ODataError Code: {Code}, Message: {Message}", 
                        schemaOdataEx.Error?.Code, schemaOdataEx.Error?.Message);
                    
                    // Log additional error details if available
                    if (schemaOdataEx.Error?.Details != null && schemaOdataEx.Error.Details.Any())
                    {
                        _logger.LogError("Additional error details:");
                        foreach (var detail in schemaOdataEx.Error.Details)
                        {
                            _logger.LogError("  - Code: {Code}, Message: {Message}, Target: {Target}", 
                                detail.Code, detail.Message, detail.Target);
                        }
                    }
                    
                    // Try to get inner exception details
                    if (schemaOdataEx.InnerException != null)
                    {
                        _logger.LogError("Inner exception: {InnerException}", schemaOdataEx.InnerException.Message);
                    }
                    
                    var errorMsg = schemaOdataEx.Error?.Message ?? schemaOdataEx.Message;
                    throw new InvalidOperationException($"Schema creation failed: {errorMsg}. Check the schema properties logged above for issues.", schemaOdataEx);
                }

                // Wait for schema registration
                await WaitForSchemaRegistration(graphClient, connectionId);

                return new SchemaCreationResult
                {
                    SchemaId = connectionId,
                    ConnectionId = connectionId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating schema and connection");
                
                var errorMessage = ex.Message;
                
                // Provide specific guidance based on the error type
                if (ex.Message.Contains("ClientSecretCredential") || 
                    ex.Message.Contains("authentication") ||
                    ex.Message.Contains("invalid_client"))
                {
                    errorMessage = "Authentication failed during External Connection creation.\n\n" +
                                 "🔧 SOLUTIONS:\n" +
                                 "1. Wait 2-3 minutes for newly created credentials to propagate\n" +
                                 "2. Grant admin consent for the app registration\n" +
                                 "3. Use the 'Retry Connection Creation' button\n\n" +
                                 $"Original error: {ex.Message}";
                }
                else if (ex.Message.Contains("Current authenticated context is not valid") ||
                         ex.Message.Contains("user sign-in") ||
                         ex.Message.Contains("Forbidden") ||
                         ex.Message.Contains("403"))
                {
                    errorMessage = "Admin consent is required for External Connection permissions.\n\n" +
                                 "The app registration was created successfully, but needs administrator approval.\n\n" +
                                 "🔧 SOLUTION:\n" +
                                 $"1. Grant admin consent using the 'Grant Admin Consent Now' button above\n" +
                                 "2. Wait 2-3 minutes for permissions to propagate\n" +
                                 "3. Use the 'Retry Connection Creation' button";
                }
                
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = errorMessage
                };
            }
        }

        private Dictionary<string, Microsoft.Graph.Models.ExternalConnectors.Property> CreateSchemaFromJson(string jsonSample)
        {
            var properties = new Dictionary<string, Microsoft.Graph.Models.ExternalConnectors.Property>();

            try
            {
                var jsonObject = JObject.Parse(jsonSample);
                
                // Flatten nested properties recursively
                FlattenJsonProperties(jsonObject, "", properties);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing JSON sample for schema creation");
                throw;
            }

            // Add standard properties only if they don't already exist
            if (!properties.ContainsKey("title"))
            {
                properties["title"] = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = "title",
                    Type = Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                    IsSearchable = true,
                    IsQueryable = true,
                    IsRetrievable = true,
                    IsRefinable = false,
                    Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>
                    {
                        Microsoft.Graph.Models.ExternalConnectors.Label.Title
                    }
                };
            }
            else
            {
                // If title exists, add the label to it
                if (properties["title"].Labels == null)
                {
                    properties["title"].Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>();
                }
                var titleLabels = properties["title"].Labels;
                if (titleLabels != null && !titleLabels.Contains(Microsoft.Graph.Models.ExternalConnectors.Label.Title))
                {
                    titleLabels.Add(Microsoft.Graph.Models.ExternalConnectors.Label.Title);
                }
            }

            if (!properties.ContainsKey("url"))
            {
                properties["url"] = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = "url",
                    Type = Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                    IsSearchable = false,
                    IsQueryable = false,
                    IsRetrievable = true,
                    IsRefinable = false,
                    Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>
                    {
                        Microsoft.Graph.Models.ExternalConnectors.Label.Url
                    }
                };
            }
            else
            {
                // If url exists, add the label to it
                if (properties["url"].Labels == null)
                {
                    properties["url"].Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>();
                }
                var urlLabels = properties["url"].Labels;
                if (urlLabels != null && !urlLabels.Contains(Microsoft.Graph.Models.ExternalConnectors.Label.Url))
                {
                    urlLabels.Add(Microsoft.Graph.Models.ExternalConnectors.Label.Url);
                }
            }

            // Add iconUrl property - required for Copilot
            if (!properties.ContainsKey("iconUrl"))
            {
                properties["iconUrl"] = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = "iconUrl",
                    Type = Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                    IsSearchable = false,
                    IsQueryable = false,
                    IsRetrievable = true,
                    IsRefinable = false,
                    Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>
                    {
                        Microsoft.Graph.Models.ExternalConnectors.Label.IconUrl
                    }
                };
            }
            else
            {
                // If iconUrl exists, add the label to it
                if (properties["iconUrl"].Labels == null)
                {
                    properties["iconUrl"].Labels = new List<Microsoft.Graph.Models.ExternalConnectors.Label?>();
                }
                var iconLabels = properties["iconUrl"].Labels;
                if (iconLabels != null && !iconLabels.Contains(Microsoft.Graph.Models.ExternalConnectors.Label.IconUrl))
                {
                    iconLabels.Add(Microsoft.Graph.Models.ExternalConnectors.Label.IconUrl);
                }
            }

            return properties;
        }

        private void FlattenJsonProperties(JObject jsonObject, string prefix, Dictionary<string, Microsoft.Graph.Models.ExternalConnectors.Property> properties)
        {
            // Reserved property names that should not be included in the schema
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "id", "content", "acl", "acls", "properties" 
            };
            
            foreach (var property in jsonObject.Properties())
            {
                var originalPropertyName = property.Name;
                var propertyValue = property.Value;
                
                // Skip reserved property names
                if (reservedNames.Contains(originalPropertyName))
                {
                    // If it's the "properties" object, flatten its contents
                    if (originalPropertyName.Equals("properties", StringComparison.OrdinalIgnoreCase) && 
                        propertyValue.Type == JTokenType.Object)
                    {
                        var nestedObject = propertyValue as JObject;
                        if (nestedObject != null)
                        {
                            FlattenJsonProperties(nestedObject, "", properties);
                        }
                    }
                    continue;
                }
                
                // Skip if it's a nested object - flatten it instead
                if (propertyValue.Type == JTokenType.Object)
                {
                    var nestedObject = propertyValue as JObject;
                    if (nestedObject != null)
                    {
                        // Recursively flatten nested properties
                        FlattenJsonProperties(nestedObject, "", properties);
                    }
                    continue;
                }
                
                // Skip arrays of objects for now (complex type not supported in simple schemas)
                if (propertyValue.Type == JTokenType.Array)
                {
                    var array = propertyValue as JArray;
                    if (array != null && array.Count > 0 && array[0].Type == JTokenType.Object)
                    {
                        continue;
                    }
                }
                
                var propertyName = NormalizePropertyName(originalPropertyName);
                
                // Skip if we've already added this property
                if (properties.ContainsKey(propertyName))
                {
                    continue;
                }
                
                var propertyType = GetPropertyType(propertyValue);
                
                // Only String and StringCollection can be searchable per Microsoft Graph API requirements
                bool canBeSearchable = propertyType == Microsoft.Graph.Models.ExternalConnectors.PropertyType.String || 
                                      propertyType == Microsoft.Graph.Models.ExternalConnectors.PropertyType.StringCollection;
                
                var schemaProperty = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = propertyName,
                    Type = propertyType,
                    IsSearchable = canBeSearchable,
                    IsQueryable = true,
                    IsRetrievable = true,
                    IsRefinable = false
                };

                // Note: Searchable and refinable are mutually exclusive
                // We prioritize searchable for better search experience

                properties[propertyName] = schemaProperty;
            }
        }

        private string GetApiPropertyType(Microsoft.Graph.Models.ExternalConnectors.PropertyType? propertyType)
        {
            return propertyType switch
            {
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.String => "string",
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.Int64 => "int64",
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.Double => "double",
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.Boolean => "boolean",
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.DateTime => "dateTime",
                Microsoft.Graph.Models.ExternalConnectors.PropertyType.StringCollection => "stringCollection",
                _ => "string"
            };
        }

        private Microsoft.Graph.Models.ExternalConnectors.PropertyType GetPropertyType(JToken? value)
        {
            if (value == null) return Microsoft.Graph.Models.ExternalConnectors.PropertyType.String;

            return value.Type switch
            {
                JTokenType.String => Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                JTokenType.Integer => Microsoft.Graph.Models.ExternalConnectors.PropertyType.Int64,
                JTokenType.Float => Microsoft.Graph.Models.ExternalConnectors.PropertyType.Double,
                JTokenType.Boolean => Microsoft.Graph.Models.ExternalConnectors.PropertyType.Boolean,
                JTokenType.Date => Microsoft.Graph.Models.ExternalConnectors.PropertyType.DateTime,
                JTokenType.Array => Microsoft.Graph.Models.ExternalConnectors.PropertyType.StringCollection,
                _ => Microsoft.Graph.Models.ExternalConnectors.PropertyType.String
            };
        }

        private async Task WaitForSchemaRegistration(GraphServiceClient graphClient, string connectionId)
        {
            var maxAttempts = 30; // 5 minutes max
            var attempt = 0;

            while (attempt < maxAttempts)
            {
                try
                {
                    var connection = await graphClient.External.Connections[connectionId].GetAsync();
                    if (connection?.State == Microsoft.Graph.Models.ExternalConnectors.ConnectionState.Ready)
                    {
                        _logger.LogInformation($"Schema registration completed for connection {connectionId}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error checking schema registration status for connection {connectionId}");
                }

                await Task.Delay(10000); // Wait 10 seconds
                attempt++;
            }

            _logger.LogWarning($"Schema registration did not complete within expected time for connection {connectionId}");
        }

        private async Task WaitForSchemaRegistrationHttp(HttpClient httpClient, string connectionId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var maxAttempts = 36; // Wait up to 6 minutes (36 * 10 seconds) instead of 3 minutes
            var attempt = 0;

            _logger.LogInformation("Starting schema registration monitoring for connection {ConnectionId}", connectionId);
            progress?.Report("Starting schema registration monitoring...");

            // Wait a moment before starting to check status to allow schema submission to process
            await Task.Delay(5000, cancellationToken);

            while (attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    _logger.LogInformation("Checking schema registration status... Attempt {Attempt}/{MaxAttempts}", attempt + 1, maxAttempts);
                    
                    // Update progress with current attempt
                    var minutesElapsed = (attempt * 10) / 60.0;
                    progress?.Report($"Registering schema with Microsoft Graph... ({minutesElapsed:F1} minutes elapsed)");
                    
                    var response = await httpClient.GetAsync($"external/connections/{connectionId}", cancellationToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var connectionResponse = JsonConvert.DeserializeObject<JObject>(content);
                        
                        var status = connectionResponse?["state"]?.ToString() ?? "unknown";
                        _logger.LogInformation("Connection state: {Status}", status);
                        
                        if (status == "ready")
                        {
                            _logger.LogInformation("Schema registration completed successfully for connection {ConnectionId}", connectionId);
                            progress?.Report("Schema registration completed successfully!");
                            return;
                        }
                        
                        if (status == "draft")
                        {
                            _logger.LogInformation("Schema registration in progress for connection {ConnectionId}", connectionId);
                            progress?.Report($"Schema registration in progress... ({minutesElapsed:F1} minutes elapsed)");
                        }
                        else if (status == "limitExceeded")
                        {
                            _logger.LogError("Connection limit exceeded for connection {ConnectionId}", connectionId);
                            throw new InvalidOperationException($"Connection limit exceeded for connection {connectionId}");
                        }
                        else if (status == "obsolete")
                        {
                            _logger.LogError("Connection became obsolete for connection {ConnectionId}", connectionId);
                            throw new InvalidOperationException($"Connection became obsolete for connection {connectionId}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to check schema status: {StatusCode}", response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking schema registration status for connection {ConnectionId}, attempt {Attempt}", connectionId, attempt + 1);
                }

                await Task.Delay(10000, cancellationToken); // Wait 10 seconds
                attempt++;
            }

            _logger.LogWarning("Schema registration did not complete within expected time for connection {ConnectionId}. This may still complete in the background.", connectionId);
            throw new TimeoutException($"Schema registration did not complete within 6 minutes for connection {connectionId}. The process may still be running in the background.");
        }

        private string NormalizePropertyName(string originalName)
        {
            if (string.IsNullOrEmpty(originalName))
                return "property";

            // Replace invalid characters with camelCase equivalents
            var normalized = originalName
                .Replace("_", "") // Remove underscores
                .Replace("-", "") // Remove hyphens
                .Replace(" ", "") // Remove spaces
                .Replace(".", "") // Remove dots
                .Replace("/", "") // Remove slashes
                .Replace("\\", "") // Remove backslashes
                .Replace("@", "At") // Replace @ with "At"
                .Replace("#", "Hash") // Replace # with "Hash"
                .Replace("$", "Dollar") // Replace $ with "Dollar"
                .Replace("%", "Percent") // Replace % with "Percent"
                .Replace("&", "And") // Replace & with "And"
                .Replace("*", "Star") // Replace * with "Star"
                .Replace("+", "Plus") // Replace + with "Plus"
                .Replace("=", "Equals") // Replace = with "Equals"
                .Replace("?", "Question") // Replace ? with "Question"
                .Replace("!", "Exclamation") // Replace ! with "Exclamation"
                .Replace("~", "Tilde") // Replace ~ with "Tilde"
                .Replace("`", "Backtick") // Replace ` with "Backtick"
                .Replace("^", "Caret") // Replace ^ with "Caret"
                .Replace("|", "Pipe") // Replace | with "Pipe"
                .Replace("{", "LeftBrace") // Replace { with "LeftBrace"
                .Replace("}", "RightBrace") // Replace } with "RightBrace"
                .Replace("[", "LeftBracket") // Replace [ with "LeftBracket"
                .Replace("]", "RightBracket") // Replace ] with "RightBracket"
                .Replace("(", "LeftParen") // Replace ( with "LeftParen"
                .Replace(")", "RightParen") // Replace ) with "RightParen"
                .Replace("<", "LessThan") // Replace < with "LessThan"
                .Replace(">", "GreaterThan") // Replace > with "GreaterThan"
                .Replace(",", "Comma") // Replace , with "Comma"
                .Replace(";", "Semicolon") // Replace ; with "Semicolon"
                .Replace(":", "Colon"); // Replace : with "Colon"

            // Ensure it starts with a letter
            if (!char.IsLetter(normalized[0]))
            {
                normalized = "prop" + normalized;
            }

            // Truncate if too long (max 64 characters for property names)
            if (normalized.Length > 64)
            {
                normalized = normalized[..64];
            }

            return normalized;
        }

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionFromMappingAsync(
            ClaimsPrincipal user,
            SchemaMappingConfiguration mappingConfig,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting External Connection creation from schema mapping");
                progress?.Report("Initializing External Connection creation from custom schema...");
                
                // Use user's delegated permissions
                using var httpClient = await _graphService.GetAuthenticatedHttpClientAsync(user);
                
                // Create connection ID that fits 3-32 character limit
                var connectionId = GenerateConnectionId(mappingConfig.ConnectionName);
                _logger.LogInformation("Generated connection ID: {ConnectionId}", connectionId);
                
                progress?.Report("Creating External Connection...");
                
                // Create the external connection
                var connection = new
                {
                    id = connectionId,
                    name = mappingConfig.ConnectionName,
                    description = mappingConfig.ConnectionDescription
                };

                var connectionJson = JsonConvert.SerializeObject(connection);
                var connectionContent = new StringContent(connectionJson, System.Text.Encoding.UTF8, "application/json");
                
                var connectionResponse = await httpClient.PostAsync("external/connections", connectionContent);
                
                if (!connectionResponse.IsSuccessStatusCode)
                {
                    var errorContent = await connectionResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create External Connection: {StatusCode} - {Error}", connectionResponse.StatusCode, errorContent);
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create external connection: {connectionResponse.StatusCode} - {errorContent}"
                    };
                }

                _logger.LogInformation("External Connection created successfully. Creating schema...");
                progress?.Report("External Connection created! Creating schema from field mappings...");
                
                // Wait a moment for connection to be created
                await Task.Delay(2000, cancellationToken);

                // Create schema from mapping configuration
                var properties = new List<object>();
                
                // Track if we have required labels
                bool hasTitleLabel = false;
                bool hasUrlLabel = false;
                
                foreach (var field in mappingConfig.Fields)
                {
                    var labels = new List<string>();
                    
                    if (field.SemanticLabel != null && field.SemanticLabel != SemanticLabel.None)
                    {
                        var labelValue = field.SemanticLabel.ToString()!.ToLowerInvariant();
                        labels.Add(labelValue);
                        
                        if (labelValue == "title") hasTitleLabel = true;
                        if (labelValue == "url") hasUrlLabel = true;
                    }
                    
                    properties.Add(new
                    {
                        name = field.FieldName,
                        type = GetApiPropertyTypeFromFieldType(field.DataType),
                        isSearchable = field.IsSearchable,
                        isQueryable = field.IsQueryable,
                        isRetrievable = field.IsRetrievable,
                        isRefinable = field.IsRefinable,
                        labels = labels.ToArray()
                    });
                }
                
                // Ensure we have required title property
                if (!hasTitleLabel)
                {
                    _logger.LogWarning("No field with 'title' label found. Adding default 'title' property.");
                    properties.Add(new
                    {
                        name = "title",
                        type = "string",
                        isSearchable = true,
                        isQueryable = true,
                        isRetrievable = true,
                        isRefinable = false,
                        labels = new[] { "title" }
                    });
                }
                
                // Ensure we have required url property
                if (!hasUrlLabel)
                {
                    _logger.LogWarning("No field with 'url' label found. Adding default 'url' property.");
                    properties.Add(new
                    {
                        name = "url",
                        type = "string",
                        isSearchable = false,
                        isQueryable = false,
                        isRetrievable = true,
                        isRefinable = false,
                        labels = new[] { "url" }
                    });
                }
                
                var schemaRequest = new
                {
                    baseType = "microsoft.graph.externalItem",
                    properties = properties.ToArray()
                };

                var schemaJson = JsonConvert.SerializeObject(schemaRequest);
                _logger.LogInformation("Schema JSON being sent: {SchemaJson}", schemaJson);
                var schemaContent = new StringContent(schemaJson, System.Text.Encoding.UTF8, "application/json");

                progress?.Report("Registering schema with Microsoft Graph...");
                var schemaResponse = await httpClient.PatchAsync($"external/connections/{connectionId}/schema", schemaContent);
                
                if (!schemaResponse.IsSuccessStatusCode)
                {
                    var errorContent = await schemaResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create schema: {StatusCode} - {Error}", schemaResponse.StatusCode, errorContent);
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create schema: {schemaResponse.StatusCode} - {errorContent}"
                    };
                }

                _logger.LogInformation("Schema submitted successfully. Waiting for registration to complete...");
                progress?.Report("Schema submitted! Waiting for registration to complete...");
                
                // Wait for schema registration
                await WaitForSchemaRegistrationHttp(httpClient, connectionId, progress, cancellationToken);

                _logger.LogInformation("External Connection and Schema creation completed successfully. Connection ID: {ConnectionId}", connectionId);
                progress?.Report($"✅ Connection '{mappingConfig.ConnectionName}' created successfully!");
                
                return new SchemaCreationResult
                {
                    SchemaId = connectionId,
                    ConnectionId = connectionId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating schema and connection from mapping");
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionFromMappingWithAzureCliAsync(
            SchemaMappingConfiguration mappingConfig,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting External Connection creation from schema mapping using Azure CLI");
                progress?.Report("Initializing External Connection creation using Azure CLI...");
                
                // Use Azure CLI authenticated HTTP client
                var graphClient = _graphService.GetGraphServiceClientWithAzureCli();
                using var httpClient = await GetHttpClientFromGraphServiceClient(graphClient);
                
                // Create connection ID that fits 3-32 character limit
                var connectionId = GenerateConnectionId(mappingConfig.ConnectionName);
                _logger.LogInformation("Generated connection ID: {ConnectionId}", connectionId);
                
                progress?.Report("Creating External Connection with Azure CLI authentication...");
                
                // Create the external connection
                var connection = new
                {
                    id = connectionId,
                    name = mappingConfig.ConnectionName,
                    description = mappingConfig.ConnectionDescription
                };

                var connectionJson = JsonConvert.SerializeObject(connection);
                var connectionContent = new StringContent(connectionJson, System.Text.Encoding.UTF8, "application/json");
                
                var connectionResponse = await httpClient.PostAsync("external/connections", connectionContent);
                
                if (!connectionResponse.IsSuccessStatusCode)
                {
                    var errorContent = await connectionResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create External Connection: {StatusCode} - {Error}", connectionResponse.StatusCode, errorContent);
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create external connection: {connectionResponse.StatusCode} - {errorContent}"
                    };
                }

                _logger.LogInformation("External Connection created successfully. Creating schema...");
                progress?.Report("External Connection created! Creating schema from field mappings...");
                
                // Wait a moment for connection to be created
                await Task.Delay(2000, cancellationToken);

                // Create schema from mapping configuration
                var properties = new List<object>();
                
                // Track if we have required labels
                bool hasTitleLabel = false;
                bool hasUrlLabel = false;
                
                foreach (var field in mappingConfig.Fields)
                {
                    var labels = new List<string>();
                    
                    if (field.SemanticLabel != null && field.SemanticLabel != SemanticLabel.None)
                    {
                        var labelValue = field.SemanticLabel.ToString()!.ToLowerInvariant();
                        labels.Add(labelValue);
                        
                        if (labelValue == "title") hasTitleLabel = true;
                        if (labelValue == "url") hasUrlLabel = true;
                    }
                    
                    properties.Add(new
                    {
                        name = field.FieldName,
                        type = GetApiPropertyTypeFromFieldType(field.DataType),
                        isSearchable = field.IsSearchable,
                        isQueryable = field.IsQueryable,
                        isRetrievable = field.IsRetrievable,
                        isRefinable = field.IsRefinable,
                        labels = labels.ToArray()
                    });
                }
                
                // Ensure we have required title property
                if (!hasTitleLabel)
                {
                    _logger.LogWarning("No field with 'title' label found. Adding default 'title' property.");
                    properties.Add(new
                    {
                        name = "title",
                        type = "string",
                        isSearchable = true,
                        isQueryable = true,
                        isRetrievable = true,
                        isRefinable = false,
                        labels = new[] { "title" }
                    });
                }
                
                // Ensure we have required url property
                if (!hasUrlLabel)
                {
                    _logger.LogWarning("No field with 'url' label found. Adding default 'url' property.");
                    properties.Add(new
                    {
                        name = "url",
                        type = "string",
                        isSearchable = false,
                        isQueryable = false,
                        isRetrievable = true,
                        isRefinable = false,
                        labels = new[] { "url" }
                    });
                }
                
                var schemaRequest = new
                {
                    baseType = "microsoft.graph.externalItem",
                    properties = properties.ToArray()
                };

                var schemaJson = JsonConvert.SerializeObject(schemaRequest);
                _logger.LogInformation("Schema JSON being sent: {SchemaJson}", schemaJson);
                var schemaContent = new StringContent(schemaJson, System.Text.Encoding.UTF8, "application/json");

                progress?.Report("Registering schema with Microsoft Graph...");
                var schemaResponse = await httpClient.PatchAsync($"external/connections/{connectionId}/schema", schemaContent);
                
                if (!schemaResponse.IsSuccessStatusCode)
                {
                    var errorContent = await schemaResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create schema: {StatusCode} - {Error}", schemaResponse.StatusCode, errorContent);
                    
                    return new SchemaCreationResult
                    {
                        Success = false,
                        ErrorMessage = $"Failed to create schema: {schemaResponse.StatusCode} - {errorContent}"
                    };
                }

                _logger.LogInformation("Schema submitted successfully. Waiting for registration to complete...");
                progress?.Report("Schema submitted! Waiting for registration to complete...");
                
                // Wait for schema registration
                await WaitForSchemaRegistrationHttp(httpClient, connectionId, progress, cancellationToken);

                _logger.LogInformation("External Connection and Schema creation completed successfully. Connection ID: {ConnectionId}", connectionId);
                progress?.Report($"✅ Connection '{mappingConfig.ConnectionName}' created successfully!");
                
                return new SchemaCreationResult
                {
                    SchemaId = connectionId,
                    ConnectionId = connectionId,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating schema and connection from mapping with Azure CLI");
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string GenerateConnectionId(string connectionName)
        {
            // Create connection ID that fits 3-32 character limit
            var sanitized = System.Text.RegularExpressions.Regex.Replace(connectionName.ToLowerInvariant(), @"[^a-z0-9]", "");
            
            if (sanitized.Length > 20)
                sanitized = sanitized[..20];
            
            if (sanitized.Length < 3)
                sanitized = "copilot" + sanitized;
            
            // Ensure it doesn't start with a number
            if (char.IsDigit(sanitized[0]))
                sanitized = "c" + sanitized[1..];
                
            return sanitized;
        }

        private string GetApiPropertyTypeFromFieldType(FieldDataType fieldType)
        {
            return fieldType switch
            {
                FieldDataType.String => "string",
                FieldDataType.Int32 => "int32",
                FieldDataType.Int64 => "int64",
                FieldDataType.Double => "double",
                FieldDataType.DateTime => "dateTime",
                FieldDataType.Boolean => "boolean",
                FieldDataType.StringCollection => "stringCollection",
                FieldDataType.Object => "string", // Objects are serialized as strings
                _ => "string"
            };
        }

        private async Task<HttpClient> GetHttpClientFromGraphServiceClient(GraphServiceClient graphClient)
        {
            // Extract the credential from GraphServiceClient and create an HttpClient
            var credential = new Azure.Identity.AzureCliCredential();
            var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var token = await credential.GetTokenAsync(tokenRequestContext);
            
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            httpClient.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
            
            return httpClient;
        }

        /// <summary>
        /// Creates schema, external connection, and deploys the ingestion service container - complete end-to-end automation
        /// </summary>
        public async Task<CompleteDeploymentResult> CreateCompleteExternalConnectionAsync(
            string tenantId,
            string clientId,
            string clientSecret,
            string jsonSample,
            string connectionName,
            string connectionDescription,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                progress?.Report("🚀 Starting complete external connection deployment...");
                
                // Step 1: Create schema and connection
                progress?.Report("📊 Creating Microsoft Graph schema and external connection...");
                var schemaResult = await CreateSchemaAndConnectionAsync(
                    tenantId, clientId, clientSecret, 
                    jsonSample, connectionName, connectionDescription);
                
                if (!schemaResult.Success)
                {
                    return new CompleteDeploymentResult
                    {
                        Success = false,
                        ErrorMessage = $"Schema creation failed: {schemaResult.ErrorMessage}",
                        SchemaResult = schemaResult
                    };
                }

                // Step 2: Parse JSON and create schema mapping
                progress?.Report("🔍 Parsing JSON structure and creating field mappings...");
                var mappingConfig = _jsonParser.ParseJsonToSchema(jsonSample);
                
                // Apply semantic labels
                progress?.Report("🏷️ Applying semantic labels to schema fields...");
                _semanticMapper.AssignSemanticLabels(mappingConfig.Fields);

                // Step 2.5: Wait for credential propagation
                progress?.Report("⏳ Waiting for app registration permissions to propagate (45 seconds)...");
                await Task.Delay(45000, cancellationToken); // Wait 45 seconds for credential propagation

                // Step 3: Deploy ingestion service container
                progress?.Report("🐳 Building and deploying ingestion service container...");
                var deploymentResult = await _containerService.DeployIngestionServiceAsync(
                    schemaResult.ConnectionId!,
                    tenantId,
                    clientId,
                    clientSecret,
                    mappingConfig);

                if (!deploymentResult.Success)
                {
                    _logger.LogWarning("Schema created successfully but container deployment failed for connection {ConnectionId}: {Error}", 
                        schemaResult.ConnectionId, deploymentResult.ErrorMessage);
                    
                    return new CompleteDeploymentResult
                    {
                        Success = false,
                        ErrorMessage = $"Schema created but ingestion service deployment failed: {deploymentResult.ErrorMessage}",
                        SchemaResult = schemaResult,
                        DeploymentResult = deploymentResult
                    };
                }

                progress?.Report("✅ Deployment completed successfully!");
                
                _logger.LogInformation("Complete deployment successful - Connection: {ConnectionId}, Service URL: {ServiceUrl}",
                    schemaResult.ConnectionId, deploymentResult.ServiceUrl);

                return new CompleteDeploymentResult
                {
                    Success = true,
                    SchemaResult = schemaResult,
                    DeploymentResult = deploymentResult,
                    MappingConfiguration = mappingConfig
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Complete deployment failed");
                return new CompleteDeploymentResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Enhanced version that works with user context (delegated permissions)
        /// </summary>
        public async Task<CompleteDeploymentResult> CreateCompleteExternalConnectionWithUserContextAsync(
            ClaimsPrincipal user,
            string jsonSample,
            string connectionName,
            string connectionDescription,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                progress?.Report("🚀 Starting complete external connection deployment with user context...");
                
                // Step 1: Create schema and connection with user context
                progress?.Report("📊 Creating Microsoft Graph schema and external connection...");
                var schemaResult = await CreateSchemaAndConnectionWithUserContextAsync(
                    user, jsonSample, connectionName, connectionDescription, progress, cancellationToken);
                
                if (!schemaResult.Success)
                {
                    return new CompleteDeploymentResult
                    {
                        Success = false,
                        ErrorMessage = $"Schema creation failed: {schemaResult.ErrorMessage}",
                        SchemaResult = schemaResult
                    };
                }

                // For user context, we need to extract the tenant info differently
                // This is a limitation - we'd need the user to provide app registration details
                // for container deployment, or we'd create a service-to-service app registration
                
                progress?.Report("⚠️ Note: Container deployment requires app registration details");
                _logger.LogInformation("Schema created with user context for connection {ConnectionId}. Container deployment requires separate app registration.", 
                    schemaResult.ConnectionId);

                return new CompleteDeploymentResult
                {
                    Success = true,
                    SchemaResult = schemaResult,
                    RequiresManualContainerDeployment = true,
                    InstructionsMessage = "Schema created successfully. To deploy the ingestion service, please provide app registration credentials or use the PowerShell deployment script."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Complete deployment with user context failed");
                return new CompleteDeploymentResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }

    // Supporting model for complete deployment result
    public class CompleteDeploymentResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public SchemaCreationResult? SchemaResult { get; set; }
        public IngestionServiceDeploymentResult? DeploymentResult { get; set; }
        public SchemaMappingConfiguration? MappingConfiguration { get; set; }
        public bool RequiresManualContainerDeployment { get; set; }
        public string? InstructionsMessage { get; set; }
    }
}
