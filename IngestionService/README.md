# Copilot Connector Ingestion Service

A minimal API service for ingesting external items into Microsoft Graph External Connections.

## Overview

This service provides REST endpoints for creating, updating, and deleting external items in Microsoft Graph. Each ingestion service instance is designed to work with a specific external connection and its associated schema.

## Features

- **Minimal API Design**: Modern ASP.NET Core minimal APIs for clean, efficient endpoints
- **Schema Validation**: Automatic validation of external items against the connection schema
- **Batch Operations**: Support for batch create/update operations (up to 100 items)
- **Health Checks**: Built-in health monitoring and connectivity testing
- **Swagger Documentation**: Interactive API documentation
- **Container Ready**: Designed for containerized deployment

## API Endpoints

### External Items

- `POST /api/external-items` - Create a new external item
- `PUT /api/external-items/{id}` - Update an existing external item
- `DELETE /api/external-items/{id}` - Delete an external item
- `POST /api/external-items/batch` - Batch create/update multiple items
- `POST /api/external-items/validate` - Validate an item without creating it
- `GET /api/external-items/schema` - Get the schema configuration

### Health & Info

- `GET /health` - Basic health check
- `GET /health/detailed` - Detailed health check with Graph connectivity test
- `GET /info` - Service information and available endpoints
- `GET /swagger` - Interactive API documentation

## Configuration

The service requires the following environment variables:

```bash
CONNECTION_ID=your-connection-id
TENANT_ID=your-tenant-id
CLIENT_ID=your-client-id
CLIENT_SECRET=your-client-secret
SCHEMA_CONFIGURATION={"fields":{},"requiredFields":[]}
SERVICE_PORT=8080
```

## Request Format

### External Item Request

```json
{
  "id": "unique-item-id",
  "properties": {
    "title": "Document Title",
    "url": "https://example.com/document",
    "lastModifiedDateTime": "2023-12-01T10:00:00Z"
  },
  "content": "Full text content of the document",
  "acls": [
    {
      "type": "user",
      "value": "user@domain.com",
      "accessType": "grant"
    }
  ]
}
```

### Batch Request

```json
{
  "items": [
    {
      "id": "item-1",
      "properties": { "title": "First Document" }
    },
    {
      "id": "item-2", 
      "properties": { "title": "Second Document" }
    }
  ]
}
```

## Response Format

### Success Response

```json
{
  "success": true,
  "itemId": "unique-item-id",
  "createdAt": "2023-12-01T10:00:00Z"
}
```

### Error Response

```json
{
  "success": false,
  "itemId": "unique-item-id",
  "errorMessage": "Validation failed",
  "validationErrors": [
    "Required field 'title' is missing"
  ]
}
```

## Development

### Prerequisites

- .NET 9.0 SDK
- Visual Studio 2022 or VS Code
- Microsoft Graph API access

### Running Locally

1. Set the required environment variables
2. Run the service:

```bash
dotnet run
```

3. Open https://localhost:7000 for Swagger documentation

### Building for Production

```bash
dotnet publish -c Release -o ./publish
```

## Docker Deployment

The service is designed to be deployed as a Docker container:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY publish/ .
EXPOSE 8080
ENTRYPOINT ["dotnet", "IngestionService.dll"]
```

## Architecture

The service consists of three main components:

1. **GraphIngestionService**: Handles Microsoft Graph API interactions
2. **SchemaValidationService**: Validates items against the connection schema  
3. **Minimal API Endpoints**: REST endpoints for external communication

## Security

- API key authentication via `X-API-Key` header
- HTTPS enforcement in production
- Input validation and sanitization
- Rate limiting (configurable)

## Monitoring

- Health checks at `/health` and `/health/detailed`
- Structured logging with configurable levels
- Graph API connectivity monitoring
- Performance metrics (response times, error rates)

## License

This project is licensed under the MIT License.