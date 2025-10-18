using CopilotConnectorGui.Models;
using System.Text.RegularExpressions;

namespace CopilotConnectorGui.Services
{
    /// <summary>
    /// Provides comprehensive validation for Microsoft Graph External Connection schemas
    /// based on official Microsoft Learn documentation and API specifications.
    /// </summary>
    public class ExternalConnectionValidationService
    {
        // Official semantic labels from Microsoft Graph External Connections API documentation
        private static readonly string[] ValidSemanticLabels = new string[]
        {
            "title",
            "url", 
            "createdBy",
            "lastModifiedBy",
            "authors",
            "createdDateTime",
            "lastModifiedDateTime",
            "fileName",
            "fileExtension",
            "unknownFutureValue",
            "containerName",
            "containerUrl",
            "iconUrl"
        };

        // Restricted characters for property names and aliases based on Microsoft Graph documentation
        // These characters are not allowed: control characters, whitespace, or any of the following:
        // :, ;, ,, (, ), [, ], {, }, %, $, +, !, *, =, &, ?, @, #, /, ~, ', ', <, >, `, ^
        private static readonly Regex PropertyNameRegex = new Regex(@"^[a-zA-Z0-9]+$", RegexOptions.Compiled);
        private static readonly Regex DisplayTemplateIdRegex = new Regex(@"^[a-zA-Z0-9]+$", RegexOptions.Compiled);
        
        // Constants from Microsoft Graph External Connections API documentation
        private const int MaxPropertyNameLength = 32;
        private const int MaxAliasLength = 32;
        private const int MaxDisplayTemplateIdLength = 16;
        private const int MinSchemaProperties = 1;
        private const int MaxSchemaProperties = 128;
        private const int MaxConnectionNameLength = 128;

        /// <summary>
        /// Validates a property name according to Microsoft Graph External Connection rules.
        /// </summary>
        /// <param name="propertyName">The property name to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidatePropertyName(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return ValidationResult.Failure("Property name cannot be empty or whitespace.");
            }

            if (propertyName.Length > MaxPropertyNameLength)
            {
                return ValidationResult.Failure($"Property name '{propertyName}' exceeds maximum length of {MaxPropertyNameLength} characters. Current length: {propertyName.Length}");
            }

            if (!PropertyNameRegex.IsMatch(propertyName))
            {
                return ValidationResult.Failure($"Property name '{propertyName}' contains invalid characters. Only alphanumeric characters (a-z, A-Z, 0-9) are allowed. No control characters, whitespace, or special symbols (:, ;, ,, (, ), [, ], {{, }}, %, $, +, !, *, =, &, ?, @, #, /, ~, ', ', <, >, `, ^) are permitted.");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates an alias according to Microsoft Graph External Connection rules.
        /// </summary>
        /// <param name="alias">The alias to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return ValidationResult.Failure("Alias cannot be empty or whitespace.");
            }

            if (alias.Length > MaxAliasLength)
            {
                return ValidationResult.Failure($"Alias '{alias}' exceeds maximum length of {MaxAliasLength} characters. Current length: {alias.Length}");
            }

            if (!PropertyNameRegex.IsMatch(alias))
            {
                return ValidationResult.Failure($"Alias '{alias}' contains invalid characters. Only alphanumeric characters (a-z, A-Z, 0-9) are allowed. No control characters, whitespace, or special symbols (:, ;, ,, (, ), [, ], {{, }}, %, $, +, !, *, =, &, ?, @, #, /, ~, ', ', <, >, `, ^) are permitted.");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates a semantic label against the official Microsoft Graph enum values.
        /// </summary>
        /// <param name="semanticLabel">The semantic label to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateSemanticLabel(string semanticLabel)
        {
            if (string.IsNullOrWhiteSpace(semanticLabel))
            {
                return ValidationResult.Success(); // Semantic labels are optional
            }

            if (!ValidSemanticLabels.Contains(semanticLabel, StringComparer.OrdinalIgnoreCase))
            {
                return ValidationResult.Failure($"Semantic label '{semanticLabel}' is not a valid value. Valid semantic labels are: {string.Join(", ", ValidSemanticLabels)}");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates a display template ID according to Microsoft Graph External Connection rules.
        /// </summary>
        /// <param name="displayTemplateId">The display template ID to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateDisplayTemplateId(string displayTemplateId)
        {
            if (string.IsNullOrWhiteSpace(displayTemplateId))
            {
                return ValidationResult.Failure("Display template ID cannot be empty or whitespace.");
            }

            if (displayTemplateId.Length > MaxDisplayTemplateIdLength)
            {
                return ValidationResult.Failure($"Display template ID '{displayTemplateId}' exceeds maximum length of {MaxDisplayTemplateIdLength} characters. Current length: {displayTemplateId.Length}");
            }

            if (!DisplayTemplateIdRegex.IsMatch(displayTemplateId))
            {
                return ValidationResult.Failure($"Display template ID '{displayTemplateId}' contains invalid characters. Only alphanumeric characters (a-z, A-Z, 0-9) are allowed.");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates a connection name according to Microsoft Graph External Connection rules.
        /// </summary>
        /// <param name="connectionName">The connection name to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateConnectionName(string connectionName)
        {
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                return ValidationResult.Failure("Connection name cannot be empty or whitespace.");
            }

            if (connectionName.Length > MaxConnectionNameLength)
            {
                return ValidationResult.Failure($"Connection name '{connectionName}' exceeds maximum length of {MaxConnectionNameLength} characters. Current length: {connectionName.Length}");
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates a complete schema definition including all properties and ensures uniqueness of semantic labels.
        /// </summary>
        /// <param name="schemaFields">List of schema field definitions to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateSchema(IEnumerable<SchemaFieldDefinition> schemaFields)
        {
            var fieldsList = schemaFields?.ToList() ?? new List<SchemaFieldDefinition>();

            // Validate schema-level limits
            if (fieldsList.Count < MinSchemaProperties)
            {
                return ValidationResult.Failure($"Schema must contain at least {MinSchemaProperties} property. Current count: {fieldsList.Count}");
            }

            if (fieldsList.Count > MaxSchemaProperties)
            {
                return ValidationResult.Failure($"Schema exceeds maximum of {MaxSchemaProperties} properties. Current count: {fieldsList.Count}");
            }

            var errors = new List<string>();
            var semanticLabelsUsed = new HashSet<SemanticLabel>();
            var propertyNamesUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in fieldsList)
            {
                // Validate property name (using FieldName property)
                var nameValidation = ValidatePropertyName(field.FieldName);
                if (!nameValidation.IsSuccess)
                {
                    errors.Add(nameValidation.ErrorMessage);
                }

                // Check for duplicate property names
                if (!propertyNamesUsed.Add(field.FieldName))
                {
                    errors.Add($"Duplicate property name '{field.FieldName}' found. Property names must be unique within a schema.");
                }

                // Validate semantic label if present and not None
                if (field.SemanticLabel.HasValue && field.SemanticLabel.Value != Models.SemanticLabel.None)
                {
                    // Check for duplicate semantic labels - each label must be unique per schema
                    if (!semanticLabelsUsed.Add(field.SemanticLabel.Value))
                    {
                        errors.Add($"Duplicate semantic label '{field.SemanticLabel.Value}' found on property '{field.FieldName}'. Each semantic label can only be assigned to one property per schema.");
                    }

                    // Validate data type compatibility
                    var dataTypeValidation = ValidateSemanticLabelDataTypeCompatibility(field);
                    if (!dataTypeValidation.IsSuccess)
                    {
                        errors.Add(dataTypeValidation.ErrorMessage);
                    }

                    // Validate retrievability requirement
                    var retrievabilityValidation = ValidateSemanticLabelRetrievability(field);
                    if (!retrievabilityValidation.IsSuccess)
                    {
                        errors.Add(retrievabilityValidation.ErrorMessage);
                    }
                }
            }

            if (errors.Any())
            {
                return ValidationResult.Failure(string.Join(" ", errors));
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates that semantic labels assigned to properties are appropriate for the property's data type.
        /// </summary>
        /// <param name="field">The schema field to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateSemanticLabelDataTypeCompatibility(SchemaFieldDefinition field)
        {
            if (!field.SemanticLabel.HasValue || field.SemanticLabel.Value == Models.SemanticLabel.None)
            {
                return ValidationResult.Success(); // No semantic label to validate
            }

            // Validate data type compatibility for semantic labels
            var errors = new List<string>();

            switch (field.SemanticLabel.Value)
            {
                case Models.SemanticLabel.CreatedDateTime:
                case Models.SemanticLabel.LastModifiedDateTime:
                    if (field.DataType != FieldDataType.DateTime)
                    {
                        errors.Add($"Semantic label '{field.SemanticLabel.Value}' requires DateTime data type, but property '{field.FieldName}' has type '{field.DataType}'.");
                    }
                    break;

                case Models.SemanticLabel.Url:
                case Models.SemanticLabel.ContainerUrl:
                case Models.SemanticLabel.IconUrl:
                case Models.SemanticLabel.Title:
                case Models.SemanticLabel.FileName:
                case Models.SemanticLabel.FileExtension:
                case Models.SemanticLabel.ContainerName:
                case Models.SemanticLabel.CreatedBy:
                case Models.SemanticLabel.LastModifiedBy:
                    if (field.DataType != FieldDataType.String)
                    {
                        errors.Add($"Semantic label '{field.SemanticLabel.Value}' requires String data type, but property '{field.FieldName}' has type '{field.DataType}'.");
                    }
                    break;

                case Models.SemanticLabel.Authors:
                    if (field.DataType != FieldDataType.StringCollection && field.DataType != FieldDataType.String)
                    {
                        errors.Add($"Semantic label '{field.SemanticLabel.Value}' requires String or StringCollection data type, but property '{field.FieldName}' has type '{field.DataType}'.");
                    }
                    break;
            }

            if (errors.Any())
            {
                return ValidationResult.Failure(string.Join(" ", errors));
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Validates that properties with semantic labels meet retrievability requirements.
        /// According to Microsoft documentation, properties assigned to labels must be marked as retrievable.
        /// </summary>
        /// <param name="field">The schema field to validate</param>
        /// <returns>ValidationResult with success status and any error messages</returns>
        public ValidationResult ValidateSemanticLabelRetrievability(SchemaFieldDefinition field)
        {
            if (!field.SemanticLabel.HasValue || field.SemanticLabel.Value == Models.SemanticLabel.None)
            {
                return ValidationResult.Success(); // No semantic label to validate
            }

            if (!field.IsRetrievable)
            {
                return ValidationResult.Failure($"Property '{field.FieldName}' has semantic label '{field.SemanticLabel.Value}' but is not marked as retrievable. Properties assigned to semantic labels must be marked as retrievable.");
            }

            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Represents the result of a validation operation.
    /// </summary>
    public class ValidationResult
    {
        public bool IsSuccess { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        private ValidationResult(bool isSuccess, string errorMessage = "")
        {
            IsSuccess = isSuccess;
            ErrorMessage = errorMessage;
        }

        public static ValidationResult Success() => new ValidationResult(true);
        public static ValidationResult Failure(string errorMessage) => new ValidationResult(false, errorMessage);
    }
}