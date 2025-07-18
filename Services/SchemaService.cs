using Microsoft.Graph;
using CopilotConnectorGui.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        public async Task<SchemaCreationResult> CreateSchemaAndConnectionAsync(
            string tenantId, 
            string clientId, 
            string clientSecret, 
            string jsonSample)
        {
            try
            {
                var graphClient = _graphService.GetGraphServiceClientForApp(tenantId, clientId, clientSecret);
                
                // Parse JSON sample to create schema
                var schema = CreateSchemaFromJson(jsonSample);
                var connectionId = $"copilot-connector-{Guid.NewGuid():N}";
                
                // Create the external connection
                var connection = new Microsoft.Graph.Models.ExternalConnectors.ExternalConnection
                {
                    Id = connectionId,
                    Name = "Copilot Connector",
                    Description = "External connection created by Copilot Connector GUI",
                    State = Microsoft.Graph.Models.ExternalConnectors.ConnectionState.Draft,
                    Configuration = new Microsoft.Graph.Models.ExternalConnectors.Configuration
                    {
                        AuthorizedAppIds = new List<string> { clientId }
                    }
                };

                // Create connection
                await graphClient.External.Connections.PostAsync(connection);

                // Wait a moment for connection to be created
                await Task.Delay(2000);

                // Create and register the schema
                var schemaRequest = new Microsoft.Graph.Models.ExternalConnectors.Schema
                {
                    BaseType = "microsoft.graph.externalItem",
                    Properties = schema.Values.ToList()
                };

                await graphClient.External.Connections[connectionId].Schema.PatchAsync(schemaRequest);

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
                return new SchemaCreationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
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
                    var propertyName = property.Name;
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
    }
}
