using IngestionService.Models;
using System.Text.Json;

namespace IngestionService.Services
{
    public class SchemaValidationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SchemaValidationService> _logger;
        private readonly GraphIngestionService _graphService;
        private SchemaConfiguration? _schemaConfiguration;
        private bool _initialized = false;

        public SchemaValidationService(
            IConfiguration configuration, 
            ILogger<SchemaValidationService> logger,
            GraphIngestionService graphService)
        {
            _configuration = configuration;
            _logger = logger;
            _graphService = graphService;
            // Don't load schema in constructor - wait for InitializeAsync
        }

        public async Task InitializeAsync()
        {
            if (!_initialized)
            {
                _schemaConfiguration = await LoadSchemaConfigurationAsync();
                _initialized = true;
            }
        }

        public ValidationResult ValidateExternalItem(ExternalItemRequest request)
        {
            if (_schemaConfiguration == null)
            {
                throw new InvalidOperationException("SchemaValidationService not initialized. Call InitializeAsync first.");
            }

            var result = new ValidationResult { IsValid = true };

            // Validate ID
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                result.Errors.Add("Item ID is required");
                result.IsValid = false;
            }
            else if (request.Id.Length > 128)
            {
                result.Errors.Add("Item ID cannot exceed 128 characters");
                result.IsValid = false;
            }
            else if (!IsValidId(request.Id))
            {
                result.Errors.Add("Item ID contains invalid characters. Only letters, numbers, hyphens, and underscores are allowed");
                result.IsValid = false;
            }

            // Validate required fields
            foreach (var requiredField in _schemaConfiguration.RequiredFields)
            {
                if (!request.Properties.ContainsKey(requiredField))
                {
                    result.Errors.Add($"Required field '{requiredField}' is missing");
                    result.IsValid = false;
                }
                else if (request.Properties[requiredField] == null)
                {
                    result.Errors.Add($"Required field '{requiredField}' cannot be null");
                    result.IsValid = false;
                }
            }

            // Validate field types and constraints
            foreach (var property in request.Properties)
            {
                var fieldName = property.Key;
                var fieldValue = property.Value;

                if (_schemaConfiguration.Fields.TryGetValue(fieldName, out var fieldInfo))
                {
                    var typeValidation = ValidateFieldType(fieldName, fieldValue, fieldInfo);
                    if (!typeValidation.IsValid)
                    {
                        result.Errors.AddRange(typeValidation.Errors);
                        result.IsValid = false;
                    }
                }
                else
                {
                    // Allow unknown fields but log a warning
                    _logger.LogWarning("Unknown field '{FieldName}' in external item '{ItemId}'", fieldName, request.Id);
                }
            }

            // Validate content
            if (!string.IsNullOrEmpty(request.Content) && request.Content.Length > 4000000) // 4MB limit
            {
                result.Errors.Add("Content cannot exceed 4MB");
                result.IsValid = false;
            }

            // Validate ACLs
            if (request.Acls != null)
            {
                foreach (var acl in request.Acls)
                {
                    var aclValidation = ValidateAcl(acl);
                    if (!aclValidation.IsValid)
                    {
                        result.Errors.AddRange(aclValidation.Errors);
                        result.IsValid = false;
                    }
                }
            }

            return result;
        }

        private ValidationResult ValidateFieldType(string fieldName, object fieldValue, SchemaFieldInfo fieldInfo)
        {
            var result = new ValidationResult { IsValid = true };

            if (fieldValue == null)
            {
                return result; // Null values are handled by required field validation
            }

            try
            {
                switch (fieldInfo.Type.ToLower())
                {
                    case "string":
                        var stringValue = GetStringValue(fieldValue);
                        if (stringValue == null)
                        {
                            result.Errors.Add($"Field '{fieldName}' must be a string");
                            result.IsValid = false;
                        }
                        else if (stringValue.Length > 2048) // Graph API limit for string fields
                        {
                            result.Errors.Add($"Field '{fieldName}' cannot exceed 2048 characters");
                            result.IsValid = false;
                        }
                        break;

                    case "int32":
                    case "integer":
                        if (!IsInteger(fieldValue))
                        {
                            result.Errors.Add($"Field '{fieldName}' must be an integer");
                            result.IsValid = false;
                        }
                        break;

                    case "double":
                    case "number":
                        if (!IsNumber(fieldValue))
                        {
                            result.Errors.Add($"Field '{fieldName}' must be a number");
                            result.IsValid = false;
                        }
                        break;

                    case "boolean":
                        if (fieldValue is not bool)
                        {
                            result.Errors.Add($"Field '{fieldName}' must be a boolean");
                            result.IsValid = false;
                        }
                        break;

                    case "datetime":
                        if (!IsDateTime(fieldValue))
                        {
                            result.Errors.Add($"Field '{fieldName}' must be a valid datetime");
                            result.IsValid = false;
                        }
                        break;

                    case "stringcollection":
                        if (!IsStringCollection(fieldValue))
                        {
                            result.Errors.Add($"Field '{fieldName}' must be an array of strings");
                            result.IsValid = false;
                        }
                        break;

                    default:
                        _logger.LogWarning("Unknown field type '{FieldType}' for field '{FieldName}'", fieldInfo.Type, fieldName);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating field '{FieldName}' of type '{FieldType}'", fieldName, fieldInfo.Type);
                result.Errors.Add($"Validation error for field '{fieldName}': {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        private ValidationResult ValidateAcl(ExternalItemAcl acl)
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(acl.Value))
            {
                result.Errors.Add("ACL value is required");
                result.IsValid = false;
            }

            if (!new[] { "user", "group", "everyone", "everyoneExceptGuests" }.Contains(acl.Type.ToLower()))
            {
                result.Errors.Add($"Invalid ACL type '{acl.Type}'. Must be one of: user, group, everyone, everyoneExceptGuests");
                result.IsValid = false;
            }

            if (!new[] { "grant", "deny" }.Contains(acl.AccessType.ToLower()))
            {
                result.Errors.Add($"Invalid ACL access type '{acl.AccessType}'. Must be either 'grant' or 'deny'");
                result.IsValid = false;
            }

            return result;
        }

        public SchemaConfiguration GetSchemaConfiguration()
        {
            return _schemaConfiguration ?? GetDefaultSchemaConfiguration();
        }

        private async Task<SchemaConfiguration> LoadSchemaConfigurationAsync()
        {
            _logger.LogInformation("Fetching schema configuration from Microsoft Graph");
            
            // Always fetch schema from Graph - this is the single source of truth
            var graphSchema = await FetchSchemaFromGraph();
            if (graphSchema != null)
            {
                _logger.LogInformation("Schema loaded successfully with {FieldCount} fields", graphSchema.Fields.Count);
                return graphSchema;
            }
            
            _logger.LogWarning("Could not fetch schema from Graph, using default configuration");
            _logger.LogWarning("This may indicate:");
            _logger.LogWarning("  1. The external connection does not exist in Microsoft Graph");
            _logger.LogWarning("  2. The schema has not been registered for this connection");
            _logger.LogWarning("  3. The service principal lacks ExternalConnection.Read.All permission");
            _logger.LogWarning("Please check the connection in the Microsoft 365 admin center or create it via the GUI");
            
            return GetDefaultSchemaConfiguration();
        }

        private SchemaConfiguration GetDefaultSchemaConfiguration()
        {
            return new SchemaConfiguration
            {
                ConnectionId = _configuration["CONNECTION_ID"] ?? "default",
                Fields = new Dictionary<string, SchemaFieldInfo>
                {
                    ["title"] = new SchemaFieldInfo
                    {
                        Name = "title",
                        Type = "String",
                        IsSearchable = true,
                        IsQueryable = true,
                        IsRetrievable = true,
                        IsRefinable = false,
                        SemanticLabel = "title"
                    },
                    ["url"] = new SchemaFieldInfo
                    {
                        Name = "url",
                        Type = "String",
                        IsSearchable = false,
                        IsQueryable = true,
                        IsRetrievable = true,
                        IsRefinable = false,
                        SemanticLabel = "url"
                    }
                },
                RequiredFields = new List<string> { "title" }
            };
        }

        private async Task<SchemaConfiguration?> FetchSchemaFromGraph()
        {
            try
            {
                var connectionId = _configuration["CONNECTION_ID"];
                if (string.IsNullOrEmpty(connectionId))
                {
                    _logger.LogWarning("Cannot fetch schema from Graph: CONNECTION_ID not configured");
                    return null;
                }

                _logger.LogInformation("Fetching schema from Graph for connection: {ConnectionId}", connectionId);

                var graphClient = await _graphService.GetGraphClientAsync();
                if (graphClient == null)
                {
                    _logger.LogWarning("Graph client not available");
                    return null;
                }

                // First, verify the connection exists
                try
                {
                    _logger.LogInformation("Attempting to retrieve connection details for: {ConnectionId}", connectionId);
                    
                    var connection = await graphClient.External.Connections[connectionId].GetAsync();
                    
                    if (connection == null)
                    {
                        _logger.LogError("External connection '{ConnectionId}' returned null from Microsoft Graph", connectionId);
                        return null;
                    }
                    
                    _logger.LogInformation("Successfully found external connection: {ConnectionName} (ID: {ConnectionId}, State: {State})", 
                        connection.Name, connection.Id, connection.State);
                }
                catch (Microsoft.Graph.Models.ODataErrors.ODataError odataConnEx)
                {
                    _logger.LogError("OData error retrieving connection '{ConnectionId}'", connectionId);
                    _logger.LogError("  Error Code: {ErrorCode}", odataConnEx.Error?.Code);
                    _logger.LogError("  Error Message: {ErrorMessage}", odataConnEx.Error?.Message);
                    _logger.LogError("  HTTP Status: {StatusCode}", odataConnEx.ResponseStatusCode);
                    
                    // Check for permission-related errors
                    if (odataConnEx.ResponseStatusCode == 403 || odataConnEx.Error?.Code == "Forbidden" || odataConnEx.Error?.Code == "Authorization_RequestDenied")
                    {
                        _logger.LogError("❌ PERMISSIONS ERROR: The app registration does not have sufficient permissions!");
                        _logger.LogError("📋 Required Microsoft Graph Application Permissions:");
                        _logger.LogError("   1. ExternalConnection.Read.All (or ExternalConnection.ReadWrite.OwnedBy)");
                        _logger.LogError("   2. ExternalItem.ReadWrite.All");
                        _logger.LogError("🔧 To fix:");
                        _logger.LogError("   - Go to Azure Portal → App Registrations → [Your App] → API Permissions");
                        _logger.LogError("   - Add the required permissions (Application type, not Delegated)");
                        _logger.LogError("   - Click 'Grant admin consent for [tenant]'");
                        _logger.LogError("   - Restart this service");
                    }
                    
                    if (odataConnEx.Error?.InnerError != null)
                    {
                        _logger.LogError("  Inner Error: {InnerError}", odataConnEx.Error.InnerError.AdditionalData);
                    }
                    
                    return null;
                }
                catch (Exception connEx)
                {
                    _logger.LogError(connEx, "Unexpected error retrieving external connection '{ConnectionId}'. Exception type: {ExceptionType}", 
                        connectionId, connEx.GetType().FullName);
                    _logger.LogError("  Exception Message: {Message}", connEx.Message);
                    _logger.LogError("  Stack Trace: {StackTrace}", connEx.StackTrace);
                    
                    if (connEx.InnerException != null)
                    {
                        _logger.LogError("  Inner Exception: {InnerException}", connEx.InnerException.ToString());
                    }
                    
                    return null;
                }

                // Fetch the schema from Graph
                var schema = await graphClient.External.Connections[connectionId].Schema.GetAsync();
                
                if (schema?.BaseType == null)
                {
                    _logger.LogWarning("No schema found in Graph for connection: {ConnectionId}", connectionId);
                    return null;
                }

                // Convert Graph schema to our SchemaConfiguration model
                var schemaConfig = new SchemaConfiguration
                {
                    ConnectionId = connectionId,
                    Fields = new Dictionary<string, SchemaFieldInfo>(),
                    RequiredFields = new List<string>()
                };

                if (schema.Properties != null)
                {
                    foreach (var property in schema.Properties)
                    {
                        if (property.Name == null) continue;

                        var fieldInfo = new SchemaFieldInfo
                        {
                            Name = property.Name,
                            Type = GetFieldType(property),
                            IsSearchable = property.IsSearchable ?? false,
                            IsQueryable = property.IsQueryable ?? false,
                            IsRetrievable = property.IsRetrievable ?? false,
                            IsRefinable = property.IsRefinable ?? false,
                            SemanticLabel = GetSemanticLabel(property.Labels)
                        };

                        schemaConfig.Fields[property.Name] = fieldInfo;

                        // Graph doesn't expose "required" directly, but we can infer from semantic labels
                        // Title is typically required
                        if (fieldInfo.SemanticLabel?.ToLower() == "title")
                        {
                            schemaConfig.RequiredFields.Add(property.Name);
                        }
                    }
                }

                _logger.LogInformation("Successfully fetched schema from Graph with {FieldCount} fields", 
                    schemaConfig.Fields.Count);

                return schemaConfig;
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError odataEx)
            {
                _logger.LogError(odataEx, "OData error fetching schema from Microsoft Graph. Error code: {ErrorCode}, Message: {ErrorMessage}", 
                    odataEx.Error?.Code, 
                    odataEx.Error?.Message);
                
                // Log additional details if available
                if (odataEx.Error?.Details != null)
                {
                    foreach (var detail in odataEx.Error.Details)
                    {
                        _logger.LogError("Additional error detail - Code: {Code}, Message: {Message}", 
                            detail.Code, detail.Message);
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching schema from Microsoft Graph");
                return null;
            }
        }

        private string GetFieldType(Microsoft.Graph.Models.ExternalConnectors.Property property)
        {
            // Map Graph property types to our internal types
            return property.Type?.ToString() ?? "String";
        }

        private string? GetSemanticLabel(List<Microsoft.Graph.Models.ExternalConnectors.Label?>? labels)
        {
            if (labels == null || labels.Count == 0)
                return null;

            // Return the first non-null label as a string
            var firstLabel = labels.FirstOrDefault(l => l != null);
            return firstLabel?.ToString()?.ToLower();
        }

        private static bool IsValidId(string id)
        {
            // ID can only contain letters, numbers, hyphens, and underscores
            return id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        private static string? GetStringValue(object value)
        {
            if (value is string stringValue)
                return stringValue;

            if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
                return jsonElement.GetString();

            return null;
        }

        private static bool IsInteger(object value)
        {
            return value is int || value is long || value is short || 
                   (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt32(out _));
        }

        private static bool IsNumber(object value)
        {
            return value is int || value is long || value is float || value is double || value is decimal ||
                   (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number);
        }

        private static bool IsDateTime(object value)
        {
            if (value is DateTime)
                return true;

            if (value is DateTimeOffset)
                return true;

            if (value is string stringValue)
                return DateTime.TryParse(stringValue, out _) || DateTimeOffset.TryParse(stringValue, out _);

            if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
                return DateTime.TryParse(jsonElement.GetString(), out _);

            return false;
        }

        private static bool IsStringCollection(object value)
        {
            if (value is string[] || value is List<string>)
                return true;

            if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
            }

            return false;
        }
    }
}