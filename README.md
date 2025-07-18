# Copilot Connector GUI

A .NET 9 Blazor application that simplifies the creation of Microsoft Graph External Connections for Microsoft Copilot.

## Features

- **Automatic App Registration**: Creates Azure AD app registrations with required permissions
- **Schema Generation**: Automatically generates Graph connector schemas from JSON samples
- **External Connection Setup**: Creates and configures Microsoft Graph External Connections
- **Secure Authentication**: Uses Microsoft Identity Web for secure authentication
- **Easy Credential Management**: Provides easy copying of generated client IDs and secrets

## Prerequisites

- .NET 9 SDK
- Azure AD tenant with admin privileges
- Visual Studio 2022 or VS Code

## Required Permissions

The user running this application must have the following permissions in Azure AD:
- Application Developer or Application Administrator role
- Ability to grant admin consent to applications

## Setup Instructions

### 1. Register the Application in Azure AD

1. Go to [Azure Portal](https://portal.azure.com) > Azure Active Directory > App registrations
2. Click "New registration"
3. Name: "Copilot Connector GUI"
4. Supported account types: "Accounts in this organizational directory only"
5. Redirect URI: Web - `https://localhost:5001/signin-oidc`
6. Click "Register"

### 2. Configure API Permissions

1. In your app registration, go to "API permissions"
2. Click "Add a permission"
3. Select "Microsoft Graph"
4. Choose "Application permissions"
5. Add the following permissions:
   - `Application.ReadWrite.All`
   - `ExternalConnection.ReadWrite.OwnedBy`
   - `ExternalItem.ReadWrite.OwnedBy`
6. Click "Grant admin consent"

### 3. Create Client Secret

1. Go to "Certificates & secrets"
2. Click "New client secret"
3. Add description and select expiration
4. Copy the secret value

### 4. Update Configuration

Update `appsettings.json` with your app registration details:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "yourdomain.onmicrosoft.com",
    "TenantId": "your-tenant-id",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "CallbackPath": "/signin-oidc"
  }
}
```

## Usage

1. **Start the application**:
   ```bash
   dotnet run
   ```

2. **Access the application**: Navigate to `https://localhost:5001`

3. **Sign in**: Use your Azure AD credentials

4. **Provide Configuration**:
   - Enter your tenant ID
   - Paste a sample JSON object representing your data structure

5. **Submit**: The application will:
   - Create a new app registration
   - Generate a client secret
   - Grant required permissions
   - Create a Graph schema based on your JSON
   - Set up the external connection

6. **Copy Credentials**: Save the generated client ID and secret for your connector implementation

## JSON Sample Format

Provide a JSON object that represents the structure of data you want to index. For example:

```json
{
  "title": "Sample Document",
  "content": "This is the document content",
  "category": "Documentation",
  "tags": ["sample", "test"],
  "createdDate": "2025-01-01",
  "author": "John Doe",
  "priority": 1
}
```

## Security Considerations

- **Client Secrets**: Store generated client secrets securely
- **Permissions**: Only grant necessary permissions
- **Access Control**: Restrict application access to authorized users
- **Audit**: Monitor app registration and permission usage

## Troubleshooting

### Common Issues

1. **Permission Denied**: Ensure you have admin privileges in Azure AD
2. **Authentication Failed**: Verify app registration configuration
3. **Schema Creation Failed**: Check JSON format and Graph API permissions
4. **Connection Issues**: Verify network connectivity and firewall settings

### Error Messages

- `Invalid JSON format`: Check your JSON sample for syntax errors
- `Failed to create app registration`: Verify admin permissions
- `Schema registration timeout`: Large schemas may take longer to process

## API Permissions Reference

| Permission | Type | Description |
|------------|------|-------------|
| `Application.ReadWrite.All` | Application | Create and manage app registrations |
| `ExternalConnection.ReadWrite.OwnedBy` | Application | Create and manage external connections |
| `ExternalItem.ReadWrite.OwnedBy` | Application | Manage external items in owned connections |

## Development

### Project Structure

```
CopilotConnectorGui/
├── Models/
│   └── TenantConfiguration.cs
├── Services/
│   ├── GraphService.cs
│   ├── AppRegistrationService.cs
│   └── SchemaService.cs
├── Pages/
│   ├── Index.razor
│   └── Shared/
└── wwwroot/
```

### Key Components

- **GraphService**: Handles Microsoft Graph authentication and client creation
- **AppRegistrationService**: Manages app registration creation and permission assignment
- **SchemaService**: Creates schemas and external connections from JSON samples

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

This project is licensed under the MIT License.

## Support

For issues and questions:
1. Check the troubleshooting section
2. Review Azure AD logs
3. Enable detailed logging in the application
4. Create an issue in the repository
