param(
    [Parameter(Mandatory=$true)]
    [string]$ConnectionId,
    
    [Parameter(Mandatory=$true)]
    [string]$TenantId,
    
    [Parameter(Mandatory=$true)]
    [string]$ClientId,
    
    [Parameter(Mandatory=$true)]
    [string]$ClientSecret,
    
    [Parameter(Mandatory=$false)]
    [int]$Port = 8080,
    
    [Parameter(Mandatory=$false)]
    [string]$ImageName = "copilot-ingestion-service",
    
    [Parameter(Mandatory=$false)]
    [string]$AclConfig = "",
    
    [Parameter(Mandatory=$false)]
    [switch]$RebuildImage,
    
    [Parameter(Mandatory=$false)]
    [switch]$StopExisting
)

$ErrorActionPreference = "Stop"

function Write-ColoredOutput {
    param([string]$Message, [string]$Color = "Green")
    Write-Host $Message -ForegroundColor $Color
}

function Write-ErrorOutput {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
}

function Write-InfoOutput {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

Write-InfoOutput "Starting ingestion service deployment"

# Validate Docker
try {
    $dockerVersion = docker --version
    Write-InfoOutput "Docker available: $dockerVersion"
    
    # Test Docker connectivity with retry logic
    $maxRetries = 3
    $retryCount = 0
    $dockerConnected = $false
    
    do {
        try {
            $dockerInfo = docker info --format "{{.ServerVersion}}" 2>$null
            if ($LASTEXITCODE -eq 0 -and $dockerInfo) {
                $dockerConnected = $true
                Write-InfoOutput "Docker engine is running (version: $dockerInfo)"
                break
            }
        }
        catch {
            # Docker not responding yet
        }
        
        $retryCount++
        if ($retryCount -lt $maxRetries) {
            Write-InfoOutput "Docker engine not ready, waiting... (attempt $retryCount/$maxRetries)"
            Start-Sleep -Seconds 10
        }
    } while ($retryCount -lt $maxRetries)
    
    if (-not $dockerConnected) {
        Write-ErrorOutput "Docker is installed but not running or accessible"
        Write-ErrorOutput "Please ensure Docker Desktop is running and try again"
        Write-InfoOutput "You can start Docker Desktop manually or wait for it to initialize"
        exit 1
    }
}
catch {
    Write-ErrorOutput "Docker not available or not running"
    Write-ErrorOutput "Please install and start Docker Desktop"
    exit 1
}

# Validate inputs
if ([string]::IsNullOrWhiteSpace($ConnectionId) -or $ConnectionId.Length -lt 3) {
    Write-ErrorOutput "ConnectionId must be at least 3 characters"
    exit 1
}

# Setup variables
$normalizedConnectionId = $ConnectionId.ToLower() -replace '[^a-z0-9-]', '-'
$containerName = "ingestion-$normalizedConnectionId"
$fullImageName = "${ImageName}:${normalizedConnectionId}"

Write-ColoredOutput "Starting Deployment" "Yellow"
Write-InfoOutput "Connection ID: $ConnectionId"
Write-InfoOutput "Container Name: $containerName"
Write-InfoOutput "Image Name: $fullImageName"
Write-InfoOutput "Port: $Port"

# Stop existing container if requested
if ($StopExisting) {
    Write-InfoOutput "Stopping existing container..."
    try {
        docker stop $containerName 2>$null
        docker rm $containerName 2>$null
        Write-ColoredOutput "Existing container stopped"
    }
    catch {
        Write-InfoOutput "No existing container to stop"
    }
}

# Check if image exists
$imageExists = $false
try {
    $existingImage = docker images -q $fullImageName 2>$null
    if ($existingImage -and -not $RebuildImage) {
        $imageExists = $true
        Write-InfoOutput "Docker image exists: $fullImageName"
    }
}
catch {
    Write-InfoOutput "Image doesn't exist"
}

# Build image if needed
if (-not $imageExists -or $RebuildImage) {
    Write-InfoOutput "Building Docker image..."
    
    $ingestionServicePath = Join-Path (Split-Path $PSScriptRoot -Parent) "IngestionService"
    if (-not (Test-Path $ingestionServicePath)) {
        Write-ErrorOutput "IngestionService directory not found: $ingestionServicePath"
        exit 1
    }
    
    Push-Location $ingestionServicePath
    
    try {
        Write-InfoOutput "Building from: $(Get-Location)"
        Write-InfoOutput "Dockerfile check: $(Test-Path 'Dockerfile')"
        
        Write-InfoOutput "Running: docker build -t $fullImageName ."
        
        # Execute docker build directly and let it stream to console
        # Using ProcessStartInfo to avoid PowerShell's error stream interpretation issues
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = "docker"
        $processInfo.Arguments = "build -t $fullImageName ."
        $processInfo.WorkingDirectory = $ingestionServicePath  # Set the working directory!
        $processInfo.RedirectStandardOutput = $false
        $processInfo.RedirectStandardError = $false
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $false
        
        $dockerProcess = [System.Diagnostics.Process]::Start($processInfo)
        $dockerProcess.WaitForExit()
        $buildExitCode = $dockerProcess.ExitCode
        
        if ($buildExitCode -ne 0) {
            Write-ErrorOutput "Docker build failed with exit code: $buildExitCode"
            Write-ErrorOutput "Please check the output above for details"
            exit 1
        }
        
        Write-ColoredOutput "Image built successfully: $fullImageName"
        
        # Verify the image was created
        $imageCheck = docker images -q $fullImageName 2>$null
        if (-not $imageCheck) {
            Write-ErrorOutput "Image was not created successfully"
            exit 1
        }
        
        Write-InfoOutput "Image verified: $fullImageName"
    }
    finally {
        Pop-Location
    }
}

# Find available port
$actualPort = $Port
$maxAttempts = 50
$attemptCount = 0

do {
    $portInUse = $false
    try {
        $tcpConnection = Test-NetConnection -ComputerName "localhost" -Port $actualPort -InformationLevel Quiet -WarningAction SilentlyContinue
        if ($tcpConnection) {
            $portInUse = $true
            $actualPort++
            $attemptCount++
        }
    }
    catch {
        break
    }
} while ($portInUse -and $attemptCount -lt $maxAttempts)

if ($attemptCount -ge $maxAttempts) {
    Write-ErrorOutput "Could not find available port after $maxAttempts attempts"
    exit 1
}

if ($actualPort -ne $Port) {
    Write-InfoOutput "Using port $actualPort instead of $Port"
}

# Schema will be fetched from Microsoft Graph by the service on startup
Write-InfoOutput "Schema will be fetched from Microsoft Graph on service startup"

# Prepare environment variables
$envVars = @(
    "CONNECTION_ID=$ConnectionId",
    "TENANT_ID=$TenantId",
    "CLIENT_ID=$ClientId",
    "CLIENT_SECRET=$ClientSecret",
    "SERVICE_PORT=$actualPort",
    "ASPNETCORE_ENVIRONMENT=Production"
)

# Add ACL configuration if provided
if (-not [string]::IsNullOrWhiteSpace($AclConfig)) {
    $envVars += "ACL_CONFIG=$AclConfig"
    Write-InfoOutput "ACL configuration provided and will be applied to all items"
}

Write-InfoOutput "Starting container..."

# Run container
try {
    $dockerRunArgs = @(
        "run", "-d", "--name", $containerName,
        "-p", "${actualPort}:8080",
        "--restart", "unless-stopped"
    )
    
    foreach ($env in $envVars) {
        $dockerRunArgs += "-e"
        $dockerRunArgs += $env
    }
    
    $dockerRunArgs += "--label"
    $dockerRunArgs += "copilot.connection.id=$ConnectionId"
    $dockerRunArgs += "--label"
    $dockerRunArgs += "copilot.service.type=ingestion"
    $dockerRunArgs += "--label"
    $dockerRunArgs += "copilot.managed=true"
    $dockerRunArgs += $fullImageName
    
    $containerId = & docker @dockerRunArgs
    
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorOutput "Failed to start container"
        exit 1
    }
    
    Write-ColoredOutput "Container started successfully"
    Write-InfoOutput "Container ID: $containerId"
}
catch {
    Write-ErrorOutput "Error starting container: $_"
    exit 1
}

# Wait for health
Write-InfoOutput "Waiting for service health..."
$healthCheckUrl = "http://localhost:$actualPort/health"
$maxHealthAttempts = 30
$healthAttempt = 0
$isHealthy = $false

do {
    Start-Sleep -Seconds 2
    $healthAttempt++
    
    try {
        $response = Invoke-WebRequest -Uri $healthCheckUrl -Method GET -TimeoutSec 5 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            $isHealthy = $true
            break
        }
    }
    catch {
        # Continue waiting
    }
    
    Write-InfoOutput "Health check $healthAttempt/$maxHealthAttempts"
    
} while ($healthAttempt -lt $maxHealthAttempts)

if ($isHealthy) {
    Write-ColoredOutput "Service is healthy!" "Green"
    Write-ColoredOutput "Service URL: http://localhost:$actualPort" "Green"
    Write-ColoredOutput "Swagger: http://localhost:$actualPort/swagger" "Green"
    
    $result = @{
        Success = $true
        ConnectionId = $ConnectionId
        ContainerName = $containerName
        ContainerId = $containerId.Trim()
        ServiceUrl = "http://localhost:$actualPort"
        Port = $actualPort
        HealthCheckUrl = $healthCheckUrl
        SwaggerUrl = "http://localhost:$actualPort/swagger"
    } | ConvertTo-Json -Compress
    
    Write-Output "DEPLOYMENT_RESULT: $result"
}
else {
    Write-ErrorOutput "Service failed health check"
    
    Write-InfoOutput "Container logs:"
    docker logs $containerId
    
    $result = @{
        Success = $false
        ErrorMessage = "Service failed health check"
        ContainerName = $containerName
        ContainerId = $containerId.Trim()
    } | ConvertTo-Json -Compress
    
    Write-Output "DEPLOYMENT_RESULT: $result"
    exit 1
}

Write-ColoredOutput "Deployment completed!" "Green"