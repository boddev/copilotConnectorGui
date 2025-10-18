using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ExternalConnectors;
using Azure.Identity;
using IngestionService.Models;
using System.Text.Json;

namespace IngestionService.Services
{
    public class GraphIngestionService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<GraphIngestionService> _logger;
        private GraphServiceClient? _graphServiceClient;
        private string _connectionId = string.Empty;

        public GraphIngestionService(IConfiguration configuration, ILogger<GraphIngestionService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            const int maxRetries = 5;
            const int retryDelaySeconds = 30;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _connectionId = _configuration["CONNECTION_ID"] ?? throw new InvalidOperationException("CONNECTION_ID not configured");
                    
                    var tenantId = _configuration["TENANT_ID"] ?? throw new InvalidOperationException("TENANT_ID not configured");
                    var clientId = _configuration["CLIENT_ID"] ?? throw new InvalidOperationException("CLIENT_ID not configured");
                    var clientSecret = _configuration["CLIENT_SECRET"] ?? throw new InvalidOperationException("CLIENT_SECRET not configured");

                    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                    
                    _graphServiceClient = new GraphServiceClient(credential);
                    
                    // Skip authentication test during initialization - validate on first actual API call
                    _logger.LogInformation("Graph service client created successfully. Authentication will be validated on first API call.");
                    
                    _logger.LogInformation("Graph service initialized successfully for connection: {ConnectionId} (attempt {Attempt})", _connectionId, attempt);
                    return; // Success - exit the retry loop
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Failed to initialize Graph service (attempt {Attempt}/{MaxRetries}). Retrying in {DelaySeconds} seconds...", 
                        attempt, maxRetries, retryDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize Graph service after {MaxRetries} attempts", maxRetries);
                    throw;
                }
            }
        }

        public async Task<ExternalItemResponse> CreateExternalItemAsync(ExternalItemRequest request)
        {
            try
            {
                if (_graphServiceClient == null)
                {
                    throw new InvalidOperationException("Graph service not initialized");
                }

                var externalItem = new ExternalItem
                {
                    Id = request.Id,
                    Properties = new Properties
                    {
                        AdditionalData = ConvertPropertiesToAdditionalData(request.Properties)
                    },
                    Content = new ExternalItemContent
                    {
                        Value = request.Content ?? string.Empty,
                        Type = ExternalItemContentType.Text
                    },
                    Acl = request.Acls?.Select(acl => new Acl
                    {
                        Type = Enum.Parse<AclType>(acl.Type, true),
                        Value = acl.Value,
                        AccessType = Enum.Parse<AccessType>(acl.AccessType, true)
                    }).ToList()
                };

                await _graphServiceClient.External.Connections[_connectionId].Items[request.Id]
                    .PutAsync(externalItem);

                _logger.LogInformation("Successfully created external item: {ItemId}", request.Id);

                return new ExternalItemResponse
                {
                    Success = true,
                    ItemId = request.Id,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create external item: {ItemId}", request.Id);
                return new ExternalItemResponse
                {
                    Success = false,
                    ItemId = request.Id,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<ExternalItemResponse> UpdateExternalItemAsync(ExternalItemRequest request)
        {
            try
            {
                if (_graphServiceClient == null)
                {
                    throw new InvalidOperationException("Graph service not initialized");
                }

                // Check if item exists first
                try
                {
                    await _graphServiceClient.External.Connections[_connectionId].Items[request.Id]
                        .GetAsync();
                }
                catch (Exception)
                {
                    return new ExternalItemResponse
                    {
                        Success = false,
                        ItemId = request.Id,
                        ErrorMessage = $"External item with ID '{request.Id}' not found"
                    };
                }

                var externalItem = new ExternalItem
                {
                    Id = request.Id,
                    Properties = new Properties
                    {
                        AdditionalData = ConvertPropertiesToAdditionalData(request.Properties)
                    },
                    Content = new ExternalItemContent
                    {
                        Value = request.Content ?? string.Empty,
                        Type = ExternalItemContentType.Text
                    },
                    Acl = request.Acls?.Select(acl => new Acl
                    {
                        Type = Enum.Parse<AclType>(acl.Type, true),
                        Value = acl.Value,
                        AccessType = Enum.Parse<AccessType>(acl.AccessType, true)
                    }).ToList()
                };

                await _graphServiceClient.External.Connections[_connectionId].Items[request.Id]
                    .PutAsync(externalItem);

                _logger.LogInformation("Successfully updated external item: {ItemId}", request.Id);

                return new ExternalItemResponse
                {
                    Success = true,
                    ItemId = request.Id,
                    UpdatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update external item: {ItemId}", request.Id);
                return new ExternalItemResponse
                {
                    Success = false,
                    ItemId = request.Id,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<ExternalItemResponse> DeleteExternalItemAsync(string itemId)
        {
            try
            {
                if (_graphServiceClient == null)
                {
                    throw new InvalidOperationException("Graph service not initialized");
                }

                // Check if item exists first
                try
                {
                    await _graphServiceClient.External.Connections[_connectionId].Items[itemId]
                        .GetAsync();
                }
                catch (Exception)
                {
                    return new ExternalItemResponse
                    {
                        Success = false,
                        ItemId = itemId,
                        ErrorMessage = $"External item with ID '{itemId}' not found"
                    };
                }

                await _graphServiceClient.External.Connections[_connectionId].Items[itemId]
                    .DeleteAsync();

                _logger.LogInformation("Successfully deleted external item: {ItemId}", itemId);

                return new ExternalItemResponse
                {
                    Success = true,
                    ItemId = itemId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete external item: {ItemId}", itemId);
                return new ExternalItemResponse
                {
                    Success = false,
                    ItemId = itemId,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<BatchExternalItemResponse> BatchCreateExternalItemsAsync(List<ExternalItemRequest> items)
        {
            var results = new List<ExternalItemResponse>();
            var tasks = new List<Task<ExternalItemResponse>>();

            // Process in parallel but limit concurrency
            var semaphore = new SemaphoreSlim(5, 5); // Max 5 concurrent operations

            foreach (var item in items)
            {
                tasks.Add(ProcessItemWithSemaphore(item, semaphore));
            }

            var responses = await Task.WhenAll(tasks);
            results.AddRange(responses);

            var successCount = results.Count(r => r.Success);
            var errorCount = results.Count(r => !r.Success);

            return new BatchExternalItemResponse
            {
                Success = errorCount == 0,
                SuccessCount = successCount,
                ErrorCount = errorCount,
                Results = results
            };
        }

        private async Task<ExternalItemResponse> ProcessItemWithSemaphore(ExternalItemRequest item, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                return await CreateExternalItemAsync(item);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public Task<GraphServiceClient?> GetGraphClientAsync()
        {
            return Task.FromResult(_graphServiceClient);
        }

        public async Task<bool> TestGraphConnectivityAsync()
        {
            try
            {
                if (_graphServiceClient == null)
                {
                    return false;
                }

                // Test by listing external connections instead of accessing a specific one
                await _graphServiceClient.External.Connections.GetAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph connectivity test failed");
                return false;
            }
        }

        private static Dictionary<string, object> ConvertPropertiesToAdditionalData(Dictionary<string, object> properties)
        {
            var additionalData = new Dictionary<string, object>();

            foreach (var kvp in properties)
            {
                // Convert complex objects to JSON if needed
                if (kvp.Value is JsonElement jsonElement)
                {
                    additionalData[kvp.Key] = ConvertJsonElement(jsonElement);
                }
                else
                {
                    additionalData[kvp.Key] = kvp.Value;
                }
            }

            return additionalData;
        }

        private static object ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.TryGetInt32(out var intValue) ? intValue : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
                JsonValueKind.Null => null!,
                _ => element.ToString()
            };
        }
    }
}