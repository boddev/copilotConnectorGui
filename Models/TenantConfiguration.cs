using System.ComponentModel.DataAnnotations;

namespace CopilotConnectorGui.Models
{
    public class TenantConfigurationModel
    {
        [Required(ErrorMessage = "Tenant ID is required")]
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", 
            ErrorMessage = "Tenant ID must be a valid GUID")]
        public string TenantId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Connection name is required")]
        [StringLength(128, MinimumLength = 3, ErrorMessage = "Connection name must be between 3 and 128 characters")]
        [RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9\s\-_\.]*[a-zA-Z0-9]$|^[a-zA-Z0-9]$", 
            ErrorMessage = "Connection name must start and end with alphanumeric characters. Can contain letters, numbers, spaces, hyphens, underscores, and periods.")]
        public string ConnectionName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Connection description is required")]
        [StringLength(500, MinimumLength = 10, ErrorMessage = "Connection description must be between 10 and 500 characters")]
        public string ConnectionDescription { get; set; } = string.Empty;

        [Required(ErrorMessage = "JSON sample is required")]
        [MinLength(10, ErrorMessage = "JSON sample must be at least 10 characters")]
        public string JsonSample { get; set; } = string.Empty;
    }

    public class AppRegistrationResult
    {
        public string ApplicationId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class SchemaCreationResult
    {
        public string? SchemaId { get; set; }
        public string? ConnectionId { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
