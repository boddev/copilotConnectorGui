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

        public SchemaService(GraphService graphService, ILogger<SchemaService> logger)
        {
            _graphService = graphService;
            _logger = logger;
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
                catch (Exception connectionEx)
                {
                    _logger.LogError(connectionEx, "Failed to create External Connection");
                    
                    var errorMessage = "Failed to create External Connection. ";
                    
                    if (connectionEx.Message.Contains("Current authenticated context is not valid") || 
                        connectionEx.Message.Contains("user sign-in") ||
                        connectionEx.Message.Contains("Insufficient privileges"))
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
                    else if (connectionEx.Message.Contains("invalid_client") || connectionEx.Message.Contains("AADSTS70002"))
                    {
                        errorMessage += "The client credentials are invalid. Please verify:\n" +
                                      "• The Client ID and Client Secret are correct\n" +
                                      "• The app registration exists in this tenant\n" +
                                      "• The client secret hasn't expired";
                    }
                    else if (connectionEx.Message.Contains("AADSTS7000215"))
                    {
                        errorMessage += "Invalid client secret. Please:\n" +
                                      "1. Wait 2-3 minutes for the new secret to become active\n" +
                                      "2. Try the 'Retry Connection Creation' button\n" +
                                      "3. Verify the client secret value (not ID) was used";
                    }
                    else if (connectionEx.Message.Contains("insufficient_claims") || 
                             connectionEx.Message.Contains("AADSTS65001") ||
                             connectionEx.Message.Contains("Forbidden") ||
                             connectionEx.Message.Contains("403"))
                    {
                        errorMessage += "Admin consent is required for External Connection permissions.\n\n" +
                                      $"🔧 SOLUTION:\n" +
                                      $"1. Grant admin consent: https://login.microsoftonline.com/{tenantId}/adminconsent?client_id={clientId}\n" +
                                      "2. Wait 2-3 minutes for permissions to propagate\n" +
                                      "3. Use the 'Retry Connection Creation' button";
                    }
                    else
                    {
                        errorMessage += $"Unexpected error: {connectionEx.Message}\n\n" +
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

                // Create and register the schema
                var schemaRequest = new Microsoft.Graph.Models.ExternalConnectors.Schema
                {
                    BaseType = "microsoft.graph.externalItem",
                    Properties = schema.Values.ToList()
                };

                await graphClient.External.Connections[connectionId].Schema.PatchAsync(schemaRequest);
                _logger.LogInformation("Schema registration submitted");

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
                
                foreach (var property in jsonObject.Properties())
                {
                    var originalPropertyName = property.Name;
                    var propertyName = NormalizePropertyName(originalPropertyName);
                    var propertyValue = property.Value;
                    
                    var schemaProperty = new Microsoft.Graph.Models.ExternalConnectors.Property
                    {
                        Name = propertyName,
                        Type = GetPropertyType(propertyValue),
                        IsSearchable = true,
                        IsQueryable = true,
                        IsRetrievable = true,
                        IsRefinable = false
                    };

                    // Set some common properties as refinable if they're strings
                    if (schemaProperty.Type == Microsoft.Graph.Models.ExternalConnectors.PropertyType.String &&
                        (propertyName.ToLower().Contains("category") || 
                         propertyName.ToLower().Contains("type") ||
                         propertyName.ToLower().Contains("status")))
                    {
                        schemaProperty.IsRefinable = true;
                    }

                    properties[propertyName] = schemaProperty;
                }

                // Add standard properties
                properties["title"] = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = "title",
                    Type = Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                    IsSearchable = true,
                    IsQueryable = true,
                    IsRetrievable = true,
                    IsRefinable = false
                };

                properties["url"] = new Microsoft.Graph.Models.ExternalConnectors.Property
                {
                    Name = "url",
                    Type = Microsoft.Graph.Models.ExternalConnectors.PropertyType.String,
                    IsSearchable = false,
                    IsQueryable = false,
                    IsRetrievable = true,
                    IsRefinable = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing JSON sample for schema creation");
                throw;
            }

            return properties;
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
                var schemaRequest = new
                {
                    baseType = "microsoft.graph.externalItem",
                    properties = mappingConfig.Fields.Select(field => new
                    {
                        name = field.FieldName,
                        type = GetApiPropertyTypeFromFieldType(field.DataType),
                        isSearchable = field.IsSearchable,
                        isQueryable = field.IsQueryable,
                        isRetrievable = field.IsRetrievable,
                        isRefinable = field.IsRefinable,
                        labels = field.SemanticLabel != null && field.SemanticLabel != SemanticLabel.None 
                            ? new[] { field.SemanticLabel.ToString()!.ToLowerInvariant() } 
                            : Array.Empty<string>()
                    }).ToArray()
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
                var schemaRequest = new
                {
                    baseType = "microsoft.graph.externalItem",
                    properties = mappingConfig.Fields.Select(field => new
                    {
                        name = field.FieldName,
                        type = GetApiPropertyTypeFromFieldType(field.DataType),
                        isSearchable = field.IsSearchable,
                        isQueryable = field.IsQueryable,
                        isRetrievable = field.IsRetrievable,
                        isRefinable = field.IsRefinable,
                        labels = field.SemanticLabel != null && field.SemanticLabel != SemanticLabel.None 
                            ? new[] { field.SemanticLabel.ToString()!.ToLowerInvariant() } 
                            : Array.Empty<string>()
                    }).ToArray()
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
    }
}
