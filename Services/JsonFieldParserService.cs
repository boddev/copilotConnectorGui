using System.Text.Json;
using CopilotConnectorGui.Models;

namespace CopilotConnectorGui.Services
{
    public class JsonFieldParserService
    {
        public SchemaMappingConfiguration ParseJsonToSchema(string jsonSample, string connectionName = "", string connectionDescription = "")
        {
            var config = new SchemaMappingConfiguration
            {
                ConnectionName = connectionName,
                ConnectionDescription = connectionDescription,
                OriginalJson = jsonSample
            };

            try
            {
                using var document = JsonDocument.Parse(jsonSample);
                config.Fields = ParseJsonElement(document.RootElement, "");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Invalid JSON format: {ex.Message}", ex);
            }

            return config;
        }

        private List<SchemaFieldDefinition> ParseJsonElement(JsonElement element, string basePath)
        {
            var fields = new List<SchemaFieldDefinition>();

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var fieldPath = string.IsNullOrEmpty(basePath) ? property.Name : $"{basePath}.{property.Name}";
                        var field = CreateFieldDefinition(property.Name, property.Value, fieldPath);
                        
                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            field.IsNested = true;
                            field.NestedFields = ParseJsonElement(property.Value, fieldPath);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0)
                        {
                            field.IsArray = true;
                            var firstElement = property.Value.EnumerateArray().FirstOrDefault();
                            if (firstElement.ValueKind == JsonValueKind.Object)
                            {
                                field.IsNested = true;
                                field.NestedFields = ParseJsonElement(firstElement, $"{fieldPath}[0]");
                            }
                        }

                        fields.Add(field);
                    }
                    break;

                case JsonValueKind.Array:
                    if (element.GetArrayLength() > 0)
                    {
                        var firstElement = element.EnumerateArray().FirstOrDefault();
                        if (firstElement.ValueKind == JsonValueKind.Object)
                        {
                            fields.AddRange(ParseJsonElement(firstElement, basePath));
                        }
                    }
                    break;
            }

            return fields;
        }

        private SchemaFieldDefinition CreateFieldDefinition(string fieldName, JsonElement value, string jsonPath)
        {
            var field = new SchemaFieldDefinition
            {
                FieldName = SanitizeFieldName(fieldName),
                DisplayName = CreateDisplayName(fieldName),
                JsonPath = jsonPath,
                SampleValue = GetSampleValue(value)
            };

            field.DataType = DetermineDataType(value);
            field.IsRequired = false; // Default to optional, user can change
            
            // Set default searchability based on data type
            switch (field.DataType)
            {
                case FieldDataType.String:
                    field.IsSearchable = true;
                    field.IsQueryable = true;
                    field.IsRetrievable = true;
                    field.IsRefinable = false;
                    break;
                case FieldDataType.DateTime:
                    field.IsSearchable = false;
                    field.IsQueryable = true;
                    field.IsRetrievable = true;
                    field.IsRefinable = true;
                    break;
                case FieldDataType.Boolean:
                    field.IsSearchable = false;
                    field.IsQueryable = true;
                    field.IsRetrievable = true;
                    field.IsRefinable = false; // Boolean fields cannot be refinable in Microsoft Graph
                    break;
                case FieldDataType.StringCollection:
                    field.IsSearchable = true;
                    field.IsQueryable = true;
                    field.IsRetrievable = true;
                    field.IsRefinable = true;
                    break;
                default:
                    field.IsSearchable = false;
                    field.IsQueryable = true;
                    field.IsRetrievable = true;
                    field.IsRefinable = false;
                    break;
            }

            return field;
        }

        private FieldDataType DetermineDataType(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => IsDateTime(value.GetString()) ? FieldDataType.DateTime : FieldDataType.String,
                JsonValueKind.Number => value.TryGetInt32(out _) ? FieldDataType.Int32 : 
                                       value.TryGetInt64(out _) ? FieldDataType.Int64 : FieldDataType.Double,
                JsonValueKind.True or JsonValueKind.False => FieldDataType.Boolean,
                JsonValueKind.Array => DetermineArrayDataType(value),
                JsonValueKind.Object => FieldDataType.Object,
                _ => FieldDataType.String
            };
        }

        private FieldDataType DetermineArrayDataType(JsonElement arrayElement)
        {
            if (arrayElement.GetArrayLength() == 0)
                return FieldDataType.StringCollection;

            var firstElement = arrayElement.EnumerateArray().FirstOrDefault();
            return firstElement.ValueKind switch
            {
                JsonValueKind.String => FieldDataType.StringCollection,
                JsonValueKind.Object => FieldDataType.Object,
                _ => FieldDataType.StringCollection
            };
        }

        private bool IsDateTime(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return DateTime.TryParse(value, out _) || 
                   DateTimeOffset.TryParse(value, out _);
        }

        private string SanitizeFieldName(string fieldName)
        {
            // Microsoft Graph External Connections only allow alphanumeric characters
            // Convert to camelCase and remove invalid characters
            
            // Split by common separators and non-alphanumeric characters
            var parts = System.Text.RegularExpressions.Regex.Split(fieldName, @"[^a-zA-Z0-9]+")
                .Where(part => !string.IsNullOrEmpty(part))
                .ToArray();
            
            if (parts.Length == 0)
                return "field"; // fallback name
            
            // Convert to camelCase: first part lowercase, subsequent parts title case
            var sanitized = parts[0].ToLowerInvariant();
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    sanitized += char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
                }
            }
            
            // Ensure it starts with a letter (if it doesn't, prepend 'field')
            if (sanitized.Length == 0 || !char.IsLetter(sanitized[0]))
                sanitized = "field" + (sanitized.Length > 0 ? char.ToUpperInvariant(sanitized[0]) + sanitized.Substring(1) : "");
                
            // Ensure maximum length of 32 characters
            if (sanitized.Length > 32)
                sanitized = sanitized.Substring(0, 32);
                
            return sanitized;
        }

        private string CreateDisplayName(string fieldName)
        {
            // Convert camelCase/PascalCase to Title Case
            var result = System.Text.RegularExpressions.Regex.Replace(fieldName, @"([A-Z])", " $1").Trim();
            return char.ToUpper(result[0]) + result.Substring(1);
        }

        private object? GetSampleValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt32(out var intVal) ? intVal : 
                                       value.TryGetDouble(out var doubleVal) ? doubleVal : null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => value.GetArrayLength() > 0 ? "[Array]" : "[]",
                JsonValueKind.Object => "[Object]",
                _ => null
            };
        }

        public List<SchemaFieldDefinition> FlattenFields(List<SchemaFieldDefinition> fields, string prefix = "")
        {
            var flattenedFields = new List<SchemaFieldDefinition>();

            foreach (var field in fields)
            {
                if (field.IsNested && field.NestedFields.Any())
                {
                    // Add nested fields with prefixed names
                    var nestedFlattened = FlattenFields(field.NestedFields, 
                        string.IsNullOrEmpty(prefix) ? field.FieldName : SanitizeFieldName($"{prefix}_{field.FieldName}"));
                    flattenedFields.AddRange(nestedFlattened);
                }
                else
                {
                    // Add the field itself with prefix if any
                    var flatField = new SchemaFieldDefinition
                    {
                        FieldName = string.IsNullOrEmpty(prefix) ? field.FieldName : SanitizeFieldName($"{prefix}_{field.FieldName}"),
                        DisplayName = string.IsNullOrEmpty(prefix) ? field.DisplayName : $"{CreateDisplayName(prefix)} {field.DisplayName}",
                        DataType = field.DataType,
                        IsRequired = field.IsRequired,
                        IsSearchable = field.IsSearchable,
                        IsQueryable = field.IsQueryable,
                        IsRetrievable = field.IsRetrievable,
                        IsRefinable = field.IsRefinable,
                        SemanticLabel = field.SemanticLabel,
                        JsonPath = field.JsonPath,
                        SampleValue = field.SampleValue,
                        IsArray = field.IsArray,
                        IsNested = false,
                        NestedFields = new()
                    };
                    flattenedFields.Add(flatField);
                }
            }

            return flattenedFields;
        }
    }
}