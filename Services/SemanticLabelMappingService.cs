using CopilotConnectorGui.Models;

namespace CopilotConnectorGui.Services
{
    public class SemanticLabelMappingService
    {
        private readonly Dictionary<SemanticLabel, SemanticLabelInfo> _semanticLabelInfo;

        public SemanticLabelMappingService()
        {
            _semanticLabelInfo = InitializeSemanticLabelInfo();
        }

        public List<SemanticLabelInfo> GetAllSemanticLabels()
        {
            return _semanticLabelInfo.Values.OrderBy(x => x.DisplayName).ToList();
        }

        public SemanticLabelInfo? GetSemanticLabelInfo(SemanticLabel label)
        {
            return _semanticLabelInfo.TryGetValue(label, out var info) ? info : null;
        }

        public void AssignSemanticLabels(List<SchemaFieldDefinition> fields)
        {
            foreach (var field in fields)
            {
                field.SemanticLabel = DetermineSemanticLabel(field);
                
                if (field.NestedFields.Any())
                {
                    AssignSemanticLabels(field.NestedFields);
                }
            }
        }

        private SemanticLabel DetermineSemanticLabel(SchemaFieldDefinition field)
        {
            var fieldNameLower = field.FieldName.ToLowerInvariant();
            var displayNameLower = field.DisplayName.ToLowerInvariant();

            // Check each semantic label for matches
            foreach (var (label, info) in _semanticLabelInfo)
            {
                if (label == SemanticLabel.None) continue;

                // Check if field name matches common field names for this semantic label
                if (info.CommonFieldNames.Any(commonName => 
                    fieldNameLower.Contains(commonName.ToLowerInvariant()) ||
                    displayNameLower.Contains(commonName.ToLowerInvariant())))
                {
                    // Verify data type compatibility if specified
                    if (info.PreferredDataType == field.DataType || info.PreferredDataType == FieldDataType.String)
                    {
                        return label;
                    }
                }
            }

            // Special pattern matching
            if (field.DataType == FieldDataType.DateTime)
            {
                if (fieldNameLower.Contains("created") || fieldNameLower.Contains("date"))
                    return SemanticLabel.CreatedDateTime;
                if (fieldNameLower.Contains("modified") || fieldNameLower.Contains("updated"))
                    return SemanticLabel.LastModifiedDateTime;
            }

            if (field.DataType == FieldDataType.String)
            {
                if (fieldNameLower.Contains("url") || fieldNameLower.Contains("link"))
                    return SemanticLabel.Url;
                if (fieldNameLower.Contains("filename") || fieldNameLower.Contains("file"))
                    return SemanticLabel.FileName;
                if (fieldNameLower.Contains("extension") || fieldNameLower.Contains("type"))
                    return SemanticLabel.FileExtension;
            }

            return SemanticLabel.None;
        }

        public bool ValidateSemanticLabelAssignment(SchemaFieldDefinition field, SemanticLabel newLabel)
        {
            if (newLabel == SemanticLabel.None)
                return true;

            var labelInfo = GetSemanticLabelInfo(newLabel);
            if (labelInfo == null)
                return false;

            // Check data type compatibility
            return labelInfo.PreferredDataType == field.DataType || 
                   labelInfo.PreferredDataType == FieldDataType.String;
        }

        public List<SemanticLabel> GetCompatibleSemanticLabels(SchemaFieldDefinition field)
        {
            var compatibleLabels = new List<SemanticLabel> { SemanticLabel.None };

            foreach (var (label, info) in _semanticLabelInfo)
            {
                if (label == SemanticLabel.None) continue;

                if (info.PreferredDataType == field.DataType || 
                    info.PreferredDataType == FieldDataType.String ||
                    field.DataType == FieldDataType.String)
                {
                    compatibleLabels.Add(label);
                }
            }

            return compatibleLabels;
        }

        private Dictionary<SemanticLabel, SemanticLabelInfo> InitializeSemanticLabelInfo()
        {
            return new Dictionary<SemanticLabel, SemanticLabelInfo>
            {
                [SemanticLabel.None] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.None,
                    DisplayName = "None",
                    Description = "No semantic label assigned",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = Array.Empty<string>()
                },
                [SemanticLabel.Title] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.Title,
                    DisplayName = "Title",
                    Description = "The main name or heading of the item that you want shown in search and other experiences",
                    PreferredDataType = FieldDataType.String,
                    IsRequired = false,
                    CommonFieldNames = new[] { "title", "name", "subject", "heading", "documentTitle", "ticketSubject", "reportName" }
                },
                [SemanticLabel.Url] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.Url,
                    DisplayName = "URL",
                    Description = "The target URL of the item in the data source. The direct link to open the item in its original system",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "url", "link", "href", "uri", "webUrl", "documentLink", "ticketUrl", "recordUrl" }
                },
                [SemanticLabel.CreatedBy] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.CreatedBy,
                    DisplayName = "Created By",
                    Description = "Identifies the user who originally created the item in the data source. Useful for filtering and context",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "createdBy", "creator", "author", "authorEmail", "submittedBy", "createdByUser" }
                },
                [SemanticLabel.LastModifiedBy] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.LastModifiedBy,
                    DisplayName = "Last Modified By",
                    Description = "The name of the user who most recently edited the item in the data source",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "lastModifiedBy", "modifiedBy", "updatedBy", "editorEmail", "lastChangedBy" }
                },
                [SemanticLabel.Authors] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.Authors,
                    DisplayName = "Authors",
                    Description = "The names of all the people who participated/collaborated on the item in the data source",
                    PreferredDataType = FieldDataType.StringCollection,
                    CommonFieldNames = new[] { "authors", "author", "authorName", "writers", "reportAuthor", "collaborators" }
                },
                [SemanticLabel.CreatedDateTime] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.CreatedDateTime,
                    DisplayName = "Created Date Time",
                    Description = "The date and time that the item was created in the data source",
                    PreferredDataType = FieldDataType.DateTime,
                    CommonFieldNames = new[] { "createdDateTime", "created", "createdAt", "createdOn", "submissionDate", "entryDate", "dateCreated" }
                },
                [SemanticLabel.LastModifiedDateTime] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.LastModifiedDateTime,
                    DisplayName = "Last Modified Date Time",
                    Description = "The date and time that the item was last modified in the data source",
                    PreferredDataType = FieldDataType.DateTime,
                    CommonFieldNames = new[] { "lastModifiedDateTime", "lastModified", "modified", "updated", "lastUpdated", "modifiedOn", "changeDate" }
                },
                [SemanticLabel.FileName] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.FileName,
                    DisplayName = "File Name",
                    Description = "The name of the file in the data source",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "fileName", "filename", "name", "file", "documentName" }
                },
                [SemanticLabel.FileExtension] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.FileExtension,
                    DisplayName = "File Extension",
                    Description = "The extension of the file in the data source",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "fileExtension", "extension", "fileType", "type", "format", "documentType", "attachmentType" }
                },
                [SemanticLabel.IconUrl] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.IconUrl,
                    DisplayName = "Icon URL",
                    Description = "The URL of an icon",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "iconUrl", "icon", "thumbnail", "thumbnailUrl", "logo", "previewImage" }
                },
                [SemanticLabel.ContainerName] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.ContainerName,
                    DisplayName = "Container Name",
                    Description = "The name of the container. Ex: A project or OneDrive folder can be a container",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "containerName", "projectName", "folderName", "groupName", "siteName", "libraryName" }
                },
                [SemanticLabel.ContainerUrl] = new SemanticLabelInfo
                {
                    Label = SemanticLabel.ContainerUrl,
                    DisplayName = "Container URL",
                    Description = "The URL of the container",
                    PreferredDataType = FieldDataType.String,
                    CommonFieldNames = new[] { "containerUrl", "projectUrl", "folderLink", "groupPage", "siteUrl", "libraryUrl" }
                }
            };
        }
    }
}