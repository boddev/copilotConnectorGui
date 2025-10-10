using System.Text.Json.Serialization;

namespace CopilotConnectorGui.Models
{
    public class SchemaFieldDefinition
    {
        public string FieldName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public FieldDataType DataType { get; set; }
        public bool IsRequired { get; set; }
        public bool IsSearchable { get; set; } = true;
        public bool IsQueryable { get; set; } = true;
        public bool IsRetrievable { get; set; } = true;
        public bool IsRefinable { get; set; }
        public SemanticLabel? SemanticLabel { get; set; }
        public string JsonPath { get; set; } = string.Empty;
        public object? SampleValue { get; set; }
        public bool IsArray { get; set; }
        public bool IsNested { get; set; }
        public List<SchemaFieldDefinition> NestedFields { get; set; } = new();
    }

    public enum FieldDataType
    {
        String,
        Int32,
        Int64,
        Double,
        DateTime,
        Boolean,
        StringCollection,
        Object
    }

    public enum SemanticLabel
    {
        None,
        Title,
        Url,
        CreatedBy,
        LastModifiedBy,
        Authors,
        CreatedDateTime,
        LastModifiedDateTime,
        FileName,
        FileExtension,
        IconUrl,
        ContainerName,
        ContainerUrl
    }

    public class SchemaMappingConfiguration
    {
        public string ConnectionName { get; set; } = string.Empty;
        public string ConnectionDescription { get; set; } = string.Empty;
        public string OriginalJson { get; set; } = string.Empty;
        public List<SchemaFieldDefinition> Fields { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class SemanticLabelInfo
    {
        public SemanticLabel Label { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public FieldDataType PreferredDataType { get; set; }
        public bool IsRequired { get; set; }
        public string[] CommonFieldNames { get; set; } = Array.Empty<string>();
    }
}