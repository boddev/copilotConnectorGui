namespace IngestionService.Models
{
    public class ExternalItemRequest
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        public string? Content { get; set; }
        public List<ExternalItemAcl>? Acls { get; set; }
    }

    public class ExternalItemAcl
    {
        public string Type { get; set; } = "user";
        public string Value { get; set; } = string.Empty;
        public string AccessType { get; set; } = "grant";
    }

    public class ExternalItemResponse
    {
        public bool Success { get; set; }
        public string? ItemId { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string>? ValidationErrors { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class BatchExternalItemRequest
    {
        public List<ExternalItemRequest> Items { get; set; } = new List<ExternalItemRequest>();
    }

    public class BatchExternalItemResponse
    {
        public bool Success { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public List<ExternalItemResponse> Results { get; set; } = new List<ExternalItemResponse>();
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class SchemaConfiguration
    {
        public string ConnectionId { get; set; } = string.Empty;
        public Dictionary<string, SchemaFieldInfo> Fields { get; set; } = new Dictionary<string, SchemaFieldInfo>();
        public List<string> RequiredFields { get; set; } = new List<string>();
        public List<ExternalItemAcl>? DefaultAcls { get; set; }
    }

    public class SchemaFieldInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool IsSearchable { get; set; }
        public bool IsQueryable { get; set; }
        public bool IsRetrievable { get; set; }
        public bool IsRefinable { get; set; }
        public string? SemanticLabel { get; set; }
    }
}