using System.ComponentModel.DataAnnotations;

namespace CopilotConnectorGui.Models
{
    public class TenantConfigurationModel
    {
        [Required(ErrorMessage = "Tenant ID is required")]
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", 
            ErrorMessage = "Tenant ID must be a valid GUID")]
        public string TenantId { get; set; } = string.Empty;

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
