# Ingestion Service Configuration Files

These configuration files allow you to run the ingestion service independently without the GUI.

## Files Generated

For each connection, the following files are created:

1. **`[connection-id]-config.json`** - For Docker container deployment (mount as `/app/ingestion-config.json`)
2. **`[connection-id]-appsettings.json`** - For running with `dotnet run` (copy to `IngestionService/appsettings.json`)
3. **`[connection-id]-acls.json`** - ACL configuration (reference only)

## Deployment Options

### Option 1: Docker with Config File Mount (Recommended)

```bash
docker run -d \
  -v "$(pwd)/IngestionServiceConfigs/[connection-id]-config.json:/app/ingestion-config.json:ro" \
  -p 8080:8080 \
  --name ingestion-[connection-id] \
  copilot-ingestion-service:latest
```

### Option 2: Docker with Environment Variables

Extract values from the config file and pass as environment variables:

```bash
docker run -d \
  -e TENANT_ID="your-tenant-id" \
  -e CLIENT_ID="your-client-id" \
  -e CLIENT_SECRET="your-client-secret" \
  -e CONNECTION_ID="your-connection-id" \
  -e ACL_CONFIG_BASE64="base64-encoded-acl-json" \
  -p 8080:8080 \
  --name ingestion-[connection-id] \
  copilot-ingestion-service:latest
```

### Option 3: Run Locally with .NET

1. Copy the `[connection-id]-appsettings.json` to `IngestionService/appsettings.json`
2. Navigate to the IngestionService directory
3. Run: `dotnet run`

```bash
cd IngestionService
cp ../IngestionServiceConfigs/[connection-id]-appsettings.json appsettings.json
dotnet run
```

### Option 4: Build and Deploy Your Own Container

```bash
# Build the image
cd IngestionService
docker build -t my-ingestion-service:latest .

# Run with config file
docker run -d \
  -v "$(pwd)/../IngestionServiceConfigs/[connection-id]-config.json:/app/ingestion-config.json:ro" \
  -p 8080:8080 \
  my-ingestion-service:latest
```

## Configuration File Formats

### ingestion-config.json
```json
{
  "tenantId": "your-azure-ad-tenant-id",
  "clientId": "app-registration-client-id",
  "clientSecret": "app-registration-client-secret",
  "connectionId": "graph-external-connection-id",
  "defaultUrlBase": "https://example.com/items",
  "defaultAcls": [
    {
      "type": "group",
      "value": "entra-id-group-object-id",
      "accessType": "grant"
    }
  ]
}
```

### appsettings.json
Standard .NET configuration format with uppercase environment variable keys.

## API Endpoints

Once running, the service provides these endpoints:

- **POST** `/api/external-items/raw` - Ingest items (auto-transform from JSON)
- **POST** `/api/external-items` - Ingest items (structured format)
- **POST** `/api/external-items/batch` - Batch ingest
- **GET** `/health` - Health check
- **GET** `/api/external-items/schema` - View schema configuration

## Testing

Test the service with curl:

```bash
curl -X POST http://localhost:8080/api/external-items/raw \
  -H "Content-Type: application/json" \
  -d '{
    "id": "test-001",
    "title": "Test Item",
    "description": "Test description",
    "price": 29.99
  }'
```

## Security Notes

- **Never commit config files to version control** - they contain secrets
- Use environment variables or secrets management in production
- Rotate client secrets periodically
- Restrict network access to the service appropriately

## Troubleshooting

### Service won't start
- Verify all credentials are correct
- Check that the connection ID exists in Microsoft Graph
- Ensure the app registration has proper permissions and admin consent

### Items fail to ingest
- Check that the connection is in "Ready" state
- Verify the schema matches your data structure
- Review service logs: `docker logs [container-id]`

### ACLs not working
- Confirm the group IDs are correct Entra ID object IDs
- Verify the groups exist in the tenant
- Check that users are members of the specified groups
