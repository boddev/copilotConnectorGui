# Container Update Guide

## Overview
This guide explains how to update a running ingestion service container with new code changes without losing your existing schema and connection configuration.

## Key Concept: Separation of Concerns
- **Schema & Connection**: Stored in Microsoft Graph (cloud)
- **Container**: Only contains the application code and environment variables (TENANT_ID, CLIENT_ID, CLIENT_SECRET, CONNECTION_ID)

This means you can safely update/restart containers without affecting your schema!

## Method 1: Using the GUI (Recommended) ✨

The **Ingestion Services** page now includes a **Redeploy** button for each container.

### Steps:
1. Navigate to the **Ingestion Services** page in the GUI
2. Find the container you want to update
3. Click the **"Redeploy"** button (orange circular arrow icon)
4. Wait for the process to complete (~30-60 seconds)

### What Happens Behind the Scenes:
1. Extracts environment variables from the existing container (TENANT_ID, CLIENT_ID, CLIENT_SECRET)
2. Gets the existing port mapping
3. Stops and removes the old container
4. Rebuilds the Docker image with the latest code from the IngestionService folder
5. Creates a new container with the same configuration
6. The new container automatically fetches the existing schema from Microsoft Graph on startup

### Benefits:
- ✅ One-click update process
- ✅ Preserves all configuration
- ✅ No manual commands needed
- ✅ Progress feedback in the UI
- ✅ Automatic health check after deployment

## Method 2: Manual Docker Commands (Advanced)

If you prefer using Docker CLI directly:

### Step 1: Get Existing Configuration
```powershell
# Get container details
$container = docker ps -a --filter "label=copilot.connection.id=YOUR_CONNECTION_ID" --format "{{.ID}}"
docker inspect $container

# Note the environment variables and port mapping
```

### Step 2: Rebuild the Image
```powershell
cd "c:\Users\BrianODo\source\repos\copilotConnectorGui\IngestionService"
docker build -t ingestion-service-YOUR_CONNECTION_ID:latest .
```

### Step 3: Stop and Remove Old Container
```powershell
docker stop $container
docker rm $container
```

### Step 4: Run New Container with Same Config
```powershell
docker run -d `
  --name ingestion-YOUR_CONNECTION_ID `
  -p YOUR_PORT:8080 `
  -e TENANT_ID="your-tenant-id" `
  -e CLIENT_ID="your-client-id" `
  -e CLIENT_SECRET="your-client-secret" `
  -e CONNECTION_ID="YOUR_CONNECTION_ID" `
  --label copilot.managed=true `
  --label copilot.connection.id=YOUR_CONNECTION_ID `
  ingestion-service-YOUR_CONNECTION_ID:latest
```

## Method 3: Using the PowerShell Deployment Script

```powershell
cd "c:\Users\BrianODo\source\repos\copilotConnectorGui\Scripts"

.\Deploy-IngestionService.ps1 `
  -ConnectionId "YOUR_CONNECTION_ID" `
  -TenantId "your-tenant-id" `
  -ClientId "your-client-id" `
  -ClientSecret "your-client-secret" `
  -Port YOUR_PORT `
  -StopExisting
```

The `-StopExisting` flag will automatically stop and remove the old container before deploying the new one.

## What Happens When You Update?

### ✅ Preserved:
- **Schema definition** (stored in Microsoft Graph)
- **External connection** (stored in Microsoft Graph)
- **Connection ID** and credentials
- **Port mapping**
- **All previously ingested data**

### 🔄 Updated:
- **Application code** (new endpoints, bug fixes, features)
- **Container image**
- **Container ID** (gets a new ID, but same name)

## Testing the Updated Container

After redeploying:

1. **Check Health**:
   - Use the "Health" button in the GUI
   - Or visit: `http://localhost:YOUR_PORT/health`

2. **Test the New /raw Endpoint**:
   ```powershell
   $testData = @{
       id = "test-product-1"
       name = "Test Product"
       price = 29.99
       category = "Electronics"
       manufacturer = @{
           name = "ACME Corp"
           country = "USA"
       }
   }
   
   Invoke-RestMethod -Uri "http://localhost:YOUR_PORT/api/external-items/raw?connectionId=YOUR_CONNECTION_ID" `
       -Method Post `
       -ContentType "application/json" `
       -Body ($testData | ConvertTo-Json)
   ```

3. **View Swagger Documentation**:
   - Visit: `http://localhost:YOUR_PORT/swagger`

## Troubleshooting

### Container won't start after redeploy
- Check Docker logs: `docker logs ingestion-YOUR_CONNECTION_ID`
- Verify environment variables are correct
- Ensure port is not in use by another service

### Schema not loading
- The container fetches schema from Microsoft Graph on startup
- Check that CLIENT_ID and CLIENT_SECRET have correct permissions
- Verify the connection still exists in Microsoft 365 admin center

### Port already in use
- Use a different port in the redeploy process
- Or stop the service using the old port first

## Best Practices

1. **Test Locally First**: Make code changes and test locally before redeploying production containers
2. **Use the GUI**: The Redeploy button automates the entire process safely
3. **Monitor Health**: Check the health status after each redeploy
4. **Keep Backups**: Document your environment variables in a secure location
5. **Incremental Updates**: Update one container at a time if you have multiple connections

## Advanced: Zero-Downtime Updates

For production environments, you can achieve zero-downtime updates:

1. Deploy a new container on a different port
2. Verify it's healthy
3. Update your routing/load balancer
4. Stop the old container

This requires additional infrastructure but ensures continuous availability.

## Summary

The new **Redeploy** button in the GUI makes updating containers trivial:
- ✅ Preserves your schema and connection
- ✅ Updates the code automatically
- ✅ Maintains existing configuration
- ✅ Takes ~30-60 seconds
- ✅ No data loss

**You can now iterate on your ingestion service code without recreating your entire Copilot connector setup!**
