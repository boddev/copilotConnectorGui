using Docker.DotNet;
using Docker.DotNet.Models;
using System.Text.Json;
using CopilotConnectorGui.Models;
using ICSharpCode.SharpZipLib.Tar;

namespace CopilotConnectorGui.Services
{
    public class ContainerManagementService
    {
        private readonly ILogger<ContainerManagementService> _logger;
        private readonly IConfiguration _configuration;
        private readonly DockerClient _dockerClient;
        private readonly Dictionary<string, string> _runningServices = new();

        public ContainerManagementService(ILogger<ContainerManagementService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Initialize Docker client
            var dockerUri = _configuration.GetConnectionString("Docker") ?? "npipe://./pipe/docker_engine";
            _dockerClient = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
        }

        public async Task<IngestionServiceDeploymentResult> DeployIngestionServiceAsync(
            string connectionId, 
            string tenantId,
            string clientId, 
            string clientSecret,
            SchemaMappingConfiguration schemaConfig,
            int? preferredPort = null,
            bool forceRebuild = false)
        {
            try
            {
                _logger.LogInformation("Deploying ingestion service for connection: {ConnectionId}", connectionId);

                // Find available port
                var servicePort = preferredPort ?? await FindAvailablePortAsync();

                // Call the PowerShell script to deploy the service
                var result = await ExecuteDeploymentScriptAsync(connectionId, tenantId, clientId, clientSecret, schemaConfig, servicePort, forceRebuild);
                
                if (result.Success)
                {
                    _runningServices[connectionId] = result.ContainerId ?? string.Empty;
                    _logger.LogInformation("Successfully deployed ingestion service for connection {ConnectionId} on port {Port}", 
                        connectionId, servicePort);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy ingestion service for connection: {ConnectionId}", connectionId);
                return new IngestionServiceDeploymentResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task<IngestionServiceDeploymentResult> ExecuteDeploymentScriptAsync(
            string connectionId, 
            string tenantId,
            string clientId, 
            string clientSecret,
            SchemaMappingConfiguration schemaConfig,
            int servicePort,
            bool forceRebuild = false)
        {
            try
            {
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "Deploy-IngestionService.ps1");
                
                if (!File.Exists(scriptPath))
                {
                    return new IngestionServiceDeploymentResult
                    {
                        Success = false,
                        ErrorMessage = $"Deployment script not found at: {scriptPath}"
                    };
                }

                // Schema will be fetched from Microsoft Graph by the ingestion service on startup
                // No need to pass it as a parameter
                _logger.LogInformation("Deploying ingestion service - schema will be fetched from Graph on startup");

                // Serialize ACL configuration if available
                string? aclConfigJson = null;
                if (schemaConfig?.AllowedGroupIds != null && schemaConfig.AllowedGroupIds.Count > 0)
                {
                    var aclList = schemaConfig.AllowedGroupIds.Select(groupId => new
                    {
                        Type = "group",
                        Value = groupId,
                        AccessType = "grant"
                    }).ToList();
                    
                    aclConfigJson = System.Text.Json.JsonSerializer.Serialize(aclList);
                    _logger.LogInformation("Passing {AclCount} ACL group(s) to ingestion service", schemaConfig.AllowedGroupIds.Count);
                }

                // Build PowerShell command
                var argumentsList = new List<string>
                {
                    "-ExecutionPolicy", "Bypass",
                    "-File", $"\"{scriptPath}\"",
                    "-ConnectionId", $"\"{connectionId}\"",
                    "-TenantId", $"\"{tenantId}\"",
                    "-ClientId", $"\"{clientId}\"",
                    "-ClientSecret", $"\"{clientSecret}\"",
                    "-Port", servicePort.ToString(),
                    "-StopExisting"
                };

                // Add ACL configuration if available
                if (!string.IsNullOrWhiteSpace(aclConfigJson))
                {
                    // Escape the JSON for PowerShell
                    var escapedAclConfig = aclConfigJson.Replace("\"", "`\"");
                    argumentsList.Add("-AclConfig");
                    argumentsList.Add($"\"{escapedAclConfig}\"");
                }

                // Add rebuild flag if requested
                if (forceRebuild)
                {
                    argumentsList.Add("-RebuildImage");
                }

                var arguments = argumentsList.ToArray();

                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.Arguments = string.Join(" ", arguments);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Scripts");

                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        _logger.LogDebug("PowerShell Output: {Line}", e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        _logger.LogWarning("PowerShell Error: {Line}", e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                var output = outputBuilder.ToString();
                var errors = errorBuilder.ToString();

                if (process.ExitCode == 0)
                {
                    // Parse the deployment result from the output
                    var deploymentResult = ParseDeploymentResult(output, connectionId, servicePort);
                    return deploymentResult;
                }
                else
                {
                    _logger.LogError("PowerShell script failed with exit code {ExitCode}. Output: {Output}. Errors: {Errors}", 
                        process.ExitCode, output, errors);
                    
                    // Check for Docker-specific errors and provide helpful messages
                    var errorMessage = $"Deployment script failed: {errors}";
                    
                    if (errors.Contains("dockerDesktopLinuxEngine") || errors.Contains("docker_engine"))
                    {
                        errorMessage = "Docker Desktop is not running. Please start Docker Desktop and try again.";
                    }
                    else if (errors.Contains("docker: command not found") || errors.Contains("docker is not recognized"))
                    {
                        errorMessage = "Docker is not installed or not in PATH. Please install Docker Desktop.";
                    }
                    else if (errors.Contains("error during connect"))
                    {
                        errorMessage = "Cannot connect to Docker engine. Please ensure Docker Desktop is running and try again.";
                    }
                    
                    return new IngestionServiceDeploymentResult
                    {
                        Success = false,
                        ErrorMessage = errorMessage
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute deployment script");
                return new IngestionServiceDeploymentResult
                {
                    Success = false,
                    ErrorMessage = $"Script execution error: {ex.Message}"
                };
            }
        }

        private IngestionServiceDeploymentResult ParseDeploymentResult(string output, string connectionId, int servicePort)
        {
            try
            {
                // Look for the DEPLOYMENT_RESULT JSON in the output
                var lines = output.Split('\n');
                var resultLine = lines.FirstOrDefault(l => l.Contains("DEPLOYMENT_RESULT:"));
                
                if (resultLine != null)
                {
                    var jsonStart = resultLine.IndexOf('{');
                    if (jsonStart >= 0)
                    {
                        var json = resultLine.Substring(jsonStart);
                        var scriptResult = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        
                        if (scriptResult != null)
                        {
                            var success = scriptResult.TryGetValue("Success", out var successObj) && 
                                         successObj is JsonElement successElement && 
                                         successElement.GetBoolean();

                            if (success)
                            {
                                var containerId = scriptResult.TryGetValue("ContainerId", out var containerIdObj) &&
                                                containerIdObj is JsonElement containerIdElement ? 
                                                containerIdElement.GetString() : null;

                                return new IngestionServiceDeploymentResult
                                {
                                    Success = true,
                                    ContainerId = containerId,
                                    ContainerName = $"ingestion-{connectionId.ToLower()}",
                                    ServiceUrl = $"http://localhost:{servicePort}",
                                    Port = servicePort,
                                    ConnectionId = connectionId
                                };
                            }
                            else
                            {
                                var errorMessage = scriptResult.TryGetValue("ErrorMessage", out var errorObj) &&
                                                 errorObj is JsonElement errorElement ? 
                                                 errorElement.GetString() : "Unknown error";

                                return new IngestionServiceDeploymentResult
                                {
                                    Success = false,
                                    ErrorMessage = errorMessage
                                };
                            }
                        }
                    }
                }

                // Fallback: if no structured result, check for success indicators in output
                if (output.Contains("🎉 Deployment completed successfully!") || output.Contains("✅ Ingestion service is healthy"))
                {
                    return new IngestionServiceDeploymentResult
                    {
                        Success = true,
                        ContainerName = $"ingestion-{connectionId.ToLower()}",
                        ServiceUrl = $"http://localhost:{servicePort}",
                        Port = servicePort,
                        ConnectionId = connectionId
                    };
                }

                return new IngestionServiceDeploymentResult
                {
                    Success = false,
                    ErrorMessage = "Could not parse deployment result from script output"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse deployment result");
                return new IngestionServiceDeploymentResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to parse result: {ex.Message}"
                };
            }
        }

        public async Task<bool> StopIngestionServiceAsync(string connectionId)
        {
            try
            {
                if (!_runningServices.TryGetValue(connectionId, out var containerId))
                {
                    // Try to find container by label
                    var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["label"] = new Dictionary<string, bool> { [$"copilot.connection.id={connectionId}"] = true }
                        }
                    });

                    var container = containers.FirstOrDefault();
                    if (container == null)
                    {
                        _logger.LogWarning("No container found for connection: {ConnectionId}", connectionId);
                        return false;
                    }

                    containerId = container.ID;
                }

                await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 30
                });

                await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
                {
                    Force = true
                });

                _runningServices.Remove(connectionId);
                
                _logger.LogInformation("Successfully stopped ingestion service for connection: {ConnectionId}", connectionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop ingestion service for connection: {ConnectionId}", connectionId);
                return false;
            }
        }

        public async Task<bool> RedeployIngestionServiceAsync(string connectionId)
        {
            try
            {
                _logger.LogInformation("Redeploying ingestion service for connection: {ConnectionId}", connectionId);

                // Get existing container information
                var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool> { [$"copilot.connection.id={connectionId}"] = true }
                    }
                });

                var existingContainer = containers.FirstOrDefault();
                if (existingContainer == null)
                {
                    _logger.LogWarning("No container found for connection: {ConnectionId}", connectionId);
                    return false;
                }

                // Inspect container to get environment variables
                var containerDetails = await _dockerClient.Containers.InspectContainerAsync(existingContainer.ID);
                var envVars = containerDetails.Config.Env ?? new List<string>();
                
                // Parse environment variables
                string? tenantId = null;
                string? clientId = null;
                string? clientSecret = null;
                int? port = null;

                foreach (var env in envVars)
                {
                    var parts = env.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        switch (parts[0])
                        {
                            case "TENANT_ID":
                                tenantId = parts[1];
                                break;
                            case "CLIENT_ID":
                                clientId = parts[1];
                                break;
                            case "CLIENT_SECRET":
                                clientSecret = parts[1];
                                break;
                        }
                    }
                }

                // Get the port from port bindings
                var portBinding = existingContainer.Ports?.FirstOrDefault(p => p.PrivatePort == 8080);
                port = portBinding?.PublicPort;

                if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || 
                    string.IsNullOrEmpty(clientSecret) || !port.HasValue)
                {
                    _logger.LogError("Failed to extract environment variables from existing container");
                    return false;
                }

                _logger.LogInformation("Redeploying with TenantId: {TenantId}, ClientId: {ClientId}, Port: {Port}", 
                    tenantId, clientId, port);

                // Redeploy using the same configuration with forced rebuild
                // Schema will be fetched from Microsoft Graph (not needed as parameter)
                var result = await DeployIngestionServiceAsync(
                    connectionId,
                    tenantId,
                    clientId,
                    clientSecret,
                    new SchemaMappingConfiguration(), // Empty - will be fetched from Graph
                    port,
                    forceRebuild: true);  // FORCE REBUILD to get latest code

                return result.Success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to redeploy ingestion service for connection: {ConnectionId}", connectionId);
                return false;
            }
        }

        public async Task<List<IngestionServiceInfo>> ListIngestionServicesAsync()
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool> { ["copilot.managed=true"] = true }
                    }
                });

                var services = new List<IngestionServiceInfo>();

                foreach (var container in containers)
                {
                    var connectionId = container.Labels?.TryGetValue("copilot.connection.id", out var id) == true ? id : "unknown";
                    var port = ExtractPortFromContainer(container);
                    
                    services.Add(new IngestionServiceInfo
                    {
                        ConnectionId = connectionId,
                        ContainerId = container.ID,
                        ContainerName = container.Names?.FirstOrDefault()?.TrimStart('/') ?? "unknown",
                        Status = container.State,
                        ServiceUrl = container.State == "running" ? $"http://localhost:{port}" : null,
                        Port = port,
                        CreatedAt = DateTime.Parse(container.Created.ToString())
                    });
                }

                return services;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list ingestion services");
                return new List<IngestionServiceInfo>();
            }
        }

        public async Task<ServiceHealthStatus> CheckServiceHealthAsync(string connectionId)
        {
            try
            {
                var services = await ListIngestionServicesAsync();
                var service = services.FirstOrDefault(s => s.ConnectionId == connectionId);
                
                if (service == null)
                {
                    return new ServiceHealthStatus { IsHealthy = false, ErrorMessage = "Service not found" };
                }

                if (service.Status != "running")
                {
                    return new ServiceHealthStatus { IsHealthy = false, ErrorMessage = $"Container status: {service.Status}" };
                }

                // Test HTTP endpoint
                if (!string.IsNullOrEmpty(service.ServiceUrl))
                {
                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    
                    var response = await httpClient.GetAsync($"{service.ServiceUrl}/health");
                    return new ServiceHealthStatus 
                    { 
                        IsHealthy = response.IsSuccessStatusCode,
                        ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP {response.StatusCode}"
                    };
                }

                return new ServiceHealthStatus { IsHealthy = true };
            }
            catch (Exception ex)
            {
                return new ServiceHealthStatus { IsHealthy = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<DockerImageBuildResult> BuildIngestionServiceImageAsync(string imageName, SchemaMappingConfiguration schemaConfig)
        {
            try
            {
                _logger.LogInformation("Building Docker image: {ImageName}", imageName);

                // Create build context with ingestion service files
                using var buildContext = await CreateBuildContextAsync(schemaConfig);
                
                var buildParams = new ImageBuildParameters
                {
                    Tags = new[] { imageName },
                    Dockerfile = "Dockerfile",
                    Remove = true,
                    ForceRemove = true
                };

                var progress = new Progress<JSONMessage>(message =>
                {
                    if (!string.IsNullOrEmpty(message.Stream))
                    {
                        _logger.LogDebug("Docker build: {Line}", message.Stream.Trim());
                    }
                });

                await _dockerClient.Images.BuildImageFromDockerfileAsync(
                    buildParams,
                    buildContext,
                    null, // authConfigs
                    null, // headers
                    progress);
                
                _logger.LogInformation("Docker image build completed: {ImageName}", imageName);

                return new DockerImageBuildResult { Success = true, ImageName = imageName };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build Docker image: {ImageName}", imageName);
                return new DockerImageBuildResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<Stream> CreateBuildContextAsync(SchemaMappingConfiguration schemaConfig)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"ingestion-build-{Guid.NewGuid():N}");
            
            try
            {
                Directory.CreateDirectory(tempDir);

                // Copy ingestion service files to temp directory
                var sourceDir = Path.Combine(Directory.GetCurrentDirectory(), "IngestionService");
                
                if (!Directory.Exists(sourceDir))
                {
                    throw new DirectoryNotFoundException($"IngestionService directory not found at: {sourceDir}");
                }

                await CopyDirectoryRecursivelyAsync(sourceDir, tempDir);

                // Create tar stream for Docker build context
                var tarStream = new MemoryStream();
                await CreateTarFromDirectoryAsync(tempDir, tarStream);
                
                // Only seek if the stream is not disposed/closed
                if (tarStream.CanSeek)
                {
                    tarStream.Seek(0, SeekOrigin.Begin);
                }
                else
                {
                    throw new InvalidOperationException("Tar stream was closed during creation");
                }
                
                return tarStream;
            }
            catch
            {
                // Cleanup temp directory on error
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp directory after error: {TempDir}", tempDir);
                }
                throw;
            }
            finally
            {
                // Schedule cleanup after a delay to allow Docker to read the files
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(30000); // Wait 30 seconds before cleanup
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup temp directory: {TempDir}", tempDir);
                    }
                });
            }
        }

        private Task CopyDirectoryRecursivelyAsync(string sourceDir, string destDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var destFile = Path.Combine(destDir, relativePath);
                var destFileDir = Path.GetDirectoryName(destFile);
                
                if (!string.IsNullOrEmpty(destFileDir))
                {
                    Directory.CreateDirectory(destFileDir);
                }

                File.Copy(file, destFile);
            }
            return Task.CompletedTask;
        }

        private Task CreateTarFromDirectoryAsync(string sourceDir, Stream tarStream)
        {
            var tarArchive = TarArchive.CreateOutputTarArchive(tarStream);
            tarArchive.RootPath = sourceDir.Replace('\\', '/');
            
            // Don't close the stream when the archive is closed
            tarArchive.IsStreamOwner = false;
            
            try
            {
                // Add all files to the tar archive
                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                    
                    // Create tar entry from file
                    var tarEntry = TarEntry.CreateEntryFromFile(file);
                    tarEntry.Name = relativePath;
                    
                    tarArchive.WriteEntry(tarEntry, true);
                }
                
                // Add directories
                var directories = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);
                foreach (var dir in directories)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, dir).Replace('\\', '/') + "/";
                    var tarEntry = TarEntry.CreateTarEntry(relativePath);
                    tarEntry.TarHeader.TypeFlag = TarHeader.LF_DIR;
                    tarArchive.WriteEntry(tarEntry, false);
                }
            }
            finally
            {
                // Close the archive - won't close the underlying stream because IsStreamOwner = false
                tarArchive.Close();
            }
            
            return Task.CompletedTask;
        }

        private async Task<int> FindAvailablePortAsync()
        {
            for (int port = 8001; port <= 9000; port++)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMilliseconds(100);
                    await client.GetAsync($"http://localhost:{port}/health");
                }
                catch
                {
                    // Port is available if we can't connect
                    return port;
                }
            }

            throw new InvalidOperationException("No available ports found in range 8001-9000");
        }

        private object ConvertToIngestionSchema(string connectionId, SchemaMappingConfiguration guiSchema)
        {
            // Convert GUI's SchemaMappingConfiguration to IngestionService's SchemaConfiguration format
            var fields = new Dictionary<string, object>();
            var requiredFields = new List<string>();

            foreach (var field in guiSchema.Fields)
            {
                fields[field.FieldName] = new
                {
                    Name = field.FieldName,
                    Type = ConvertDataType(field.DataType),
                    IsSearchable = field.IsSearchable,
                    IsQueryable = field.IsQueryable,
                    IsRetrievable = field.IsRetrievable,
                    IsRefinable = field.IsRefinable,
                    SemanticLabel = field.SemanticLabel?.ToString().ToLower()
                };

                if (field.IsRequired)
                {
                    requiredFields.Add(field.FieldName);
                }
            }

            return new
            {
                ConnectionId = connectionId,
                Fields = fields,
                RequiredFields = requiredFields
            };
        }

        private string ConvertDataType(FieldDataType dataType)
        {
            return dataType switch
            {
                FieldDataType.String => "String",
                FieldDataType.Int32 => "Int32",
                FieldDataType.Int64 => "Int64",
                FieldDataType.Double => "Double",
                FieldDataType.DateTime => "DateTime",
                FieldDataType.Boolean => "Boolean",
                FieldDataType.StringCollection => "StringCollection",
                _ => "String"
            };
        }

        private async Task StopAndRemoveContainerIfExistsAsync(string containerName)
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [containerName] = true }
                    }
                });

                var existingContainer = containers.FirstOrDefault();
                if (existingContainer != null)
                {
                    _logger.LogInformation("Stopping existing container: {ContainerName}", containerName);
                    
                    await _dockerClient.Containers.StopContainerAsync(existingContainer.ID, new ContainerStopParameters
                    {
                        WaitBeforeKillSeconds = 10
                    });

                    await _dockerClient.Containers.RemoveContainerAsync(existingContainer.ID, new ContainerRemoveParameters
                    {
                        Force = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop/remove existing container: {ContainerName}", containerName);
            }
        }

        private async Task<ServiceHealthStatus> WaitForContainerHealthAsync(string containerId, string containerName, int port)
        {
            var maxAttempts = 30;
            var delayBetweenAttempts = TimeSpan.FromSeconds(2);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Check container status
                    var containerInfo = await _dockerClient.Containers.InspectContainerAsync(containerId);
                    
                    if (containerInfo.State.Status != "running")
                    {
                        return new ServiceHealthStatus 
                        { 
                            IsHealthy = false, 
                            ErrorMessage = $"Container stopped unexpectedly. Status: {containerInfo.State.Status}" 
                        };
                    }

                    // Test HTTP endpoint
                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(5);
                    
                    var response = await httpClient.GetAsync($"http://localhost:{port}/health");
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Container {ContainerName} is healthy after {Attempts} attempts", 
                            containerName, attempt);
                        return new ServiceHealthStatus { IsHealthy = true };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Health check attempt {Attempt}/{MaxAttempts} failed for {ContainerName}", 
                        attempt, maxAttempts, containerName);
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(delayBetweenAttempts);
                }
            }

            return new ServiceHealthStatus 
            { 
                IsHealthy = false, 
                ErrorMessage = $"Container failed to become healthy after {maxAttempts} attempts" 
            };
        }

        private static int ExtractPortFromContainer(ContainerListResponse container)
        {
            var portBinding = container.Ports?.FirstOrDefault(p => p.PrivatePort == 8080);
            return portBinding?.PublicPort ?? 0;
        }

        public void Dispose()
        {
            _dockerClient?.Dispose();
        }
    }

    // Supporting Models
    public class IngestionServiceDeploymentResult
    {
        public bool Success { get; set; }
        public string? ContainerId { get; set; }
        public string? ContainerName { get; set; }
        public string? ServiceUrl { get; set; }
        public int Port { get; set; }
        public string? ConnectionId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class IngestionServiceInfo
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string ContainerId { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ServiceUrl { get; set; }
        public int Port { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ServiceHealthStatus
    {
        public bool IsHealthy { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class DockerImageBuildResult
    {
        public bool Success { get; set; }
        public string? ImageName { get; set; }
        public string? ErrorMessage { get; set; }
    }
}