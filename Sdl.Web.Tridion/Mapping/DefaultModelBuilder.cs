﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Sdl.Web.Common;
using Sdl.Web.Common.Configuration;
using Sdl.Web.Common.Extensions;
using Sdl.Web.Common.Interfaces;
using Sdl.Web.Common.Logging;
using Sdl.Web.Common.Mapping;
using Sdl.Web.Common.Models;
using Sdl.Web.Common.Models.Navigation;
using Sdl.Web.DataModel;
using Sdl.Web.Tridion.ContentManager;
using MvcData = Sdl.Web.Common.Models.MvcData;

namespace Sdl.Web.Tridion.Mapping
{
    /// <summary>
    /// Default Page and Entity Model Builder implementation (based on DXA R2 Data Model).
    /// </summary>
    public class DefaultModelBuilder : IPageModelBuilder, IEntityModelBuilder, IDataModelExtension
    {
        private class Validation
        {
            public SemanticSchema MainSchema { get; set; }
            public List<SemanticSchema> InheritedSchemas { set; get;  }
        }

        private class MappingData
        {
            public string ModelId { get; set; }
            public Type ModelType { get; set; }
            public SemanticSchemaField EmbeddedSemanticSchemaField { get; set; }
            public Validation PropertyValidation { get; set; }
            public int EmbedLevel { get; set; }
            public ViewModelData SourceViewModel { get; set; }
            public ContentModelData Fields { get; set; }
            public ContentModelData MetadataFields { get; set; }
            public string ContextXPath { get; set; }
            public Localization Localization { get; set; }
        }

        public Type ResolveDataModelType(string assemblyName, string typeName)
        {
            // perform type remapping as the type names returned from model-service do not match so we
            // remap here to correctly deserialize.
            switch (typeName)
            {
                case "TaxonomyNodeModelData":
                    return typeof(TaxonomyNode);
                case "SitemapItemModelData":
                    return typeof(SitemapItem);
            }
            // check for any extensions
            return Type.GetType($"Sdl.Web.DataModel.Extension.{typeName}");
        }

        /// <summary>
        /// Builds a strongly typed Page Model from a given DXA R2 Data Model.
        /// </summary>
        /// <param name="pageModel">The strongly typed Page Model to build. Is <c>null</c> for the first Page Model Builder in the pipeline.</param>
        /// <param name="pageModelData">The DXA R2 Data Model.</param>
        /// <param name="includePageRegions">Indicates whether Include Page Regions should be included.</param>
        /// <param name="localization">The context <see cref="Localization"/>.</param>
        public void BuildPageModel(ref PageModel pageModel, PageModelData pageModelData, bool includePageRegions, Localization localization)
        {
            using (new Tracer(pageModel, pageModelData, includePageRegions, localization))
            {
                MvcData mvcData = CreateMvcData(pageModelData.MvcData, "Page");
                Type modelType = ModelTypeRegistry.GetViewModelType(mvcData);

                if (modelType == typeof(PageModel))
                {
                    // Standard Page Model.
                    pageModel = new PageModel(pageModelData.Id)
                    {
                        ExtensionData = pageModelData.ExtensionData,
                        HtmlClasses = pageModelData.HtmlClasses,
                        XpmMetadata = localization.IsXpmEnabled ? pageModelData.XpmMetadata : null,
                    };
                }
                else if (pageModelData.SchemaId == null)
                {
                    // Custom Page Model, but no custom metadata.
                    pageModel = (PageModel) modelType.CreateInstance(pageModelData.Id);
                    pageModel.ExtensionData = pageModelData.ExtensionData;
                    pageModel.HtmlClasses = pageModelData.HtmlClasses;
                    pageModel.XpmMetadata = localization.IsXpmEnabled ? pageModelData.XpmMetadata : null;
                }
                else
                {
                    // Custom Page Model with custom metadata; do full-blown model mapping.
                    MappingData mappingData = new MappingData
                    {
                        SourceViewModel = pageModelData,
                        ModelId = pageModelData.Id,
                        ModelType = modelType,
                        PropertyValidation = new Validation
                        {
                            MainSchema = SemanticMapping.GetSchema(pageModelData.SchemaId, localization),
                            InheritedSchemas = GetInheritedSemanticSchemas(pageModelData, localization)
                        },
                        MetadataFields = pageModelData.Metadata,
                        Localization = localization
                    };
                    pageModel = (PageModel) CreateViewModel(mappingData);
                }

                pageModel.MvcData = mvcData;
                pageModel.Meta = pageModelData.Meta ?? new Dictionary<string, string>();
                pageModel.Title = PostProcessPageTitle(pageModelData, localization); // TODO TSI-2210: This should eventually be done in Model Service.
                pageModel.Url = pageModelData.UrlPath;
                if (pageModelData.Regions != null)
                {
                    IEnumerable<RegionModelData> regions = includePageRegions ? pageModelData.Regions : pageModelData.Regions.Where(r => r.IncludePageId == null);
                    pageModel.Regions.UnionWith(regions.Select(data => CreateRegionModel(data, localization)));
                    pageModel.IsVolatile |= pageModel.Regions.Any(region => region.IsVolatile);
                }
            }
        }

        private static List<SemanticSchema> GetInheritedSemanticSchemas(ViewModelData pageModelData, Localization localization)
        {
            List<SemanticSchema> schemas = new List<SemanticSchema>();
            if (pageModelData.ExtensionData != null && pageModelData.ExtensionData.ContainsKey("Schemas"))
            {
                string[] ids = pageModelData.ExtensionData["Schemas"] as string[];
                if (ids != null && ids.Length > 0)
                {
                    foreach (string inheritedSchemaId in ids)
                    {
                        SemanticSchema schema = SemanticMapping.GetSchema(inheritedSchemaId, localization);
                        if (schema == null)
                        {
                            continue;
                        }
                        schemas.Add(schema);
                    }
                }
            }

            return schemas;
        }

        /// <summary>
        /// Builds a strongly typed Entity Model based on a given DXA R2 Data Model.
        /// </summary>
        /// <param name="entityModel">The strongly typed Entity Model to build. Is <c>null</c> for the first Entity Model Builder in the pipeline.</param>
        /// <param name="entityModelData">The DXA R2 Data Model.</param>
        /// <param name="baseModelType">The base type for the Entity Model to build.</param>
        /// <param name="localization">The context <see cref="Localization"/>.</param>
        public void BuildEntityModel(ref EntityModel entityModel, EntityModelData entityModelData, Type baseModelType, Localization localization)
        {
            using (new Tracer(entityModel, entityModelData, baseModelType, localization))
            {
                MvcData mvcData = CreateMvcData(entityModelData.MvcData, "Entity");
                SemanticSchema semanticSchema = SemanticMapping.GetSchema(entityModelData.SchemaId, localization);

                Type modelType = (baseModelType == null) ?
                    ModelTypeRegistry.GetViewModelType(mvcData) :
                    semanticSchema.GetModelTypeFromSemanticMapping(baseModelType);

                MappingData mappingData = new MappingData
                {
                    SourceViewModel = entityModelData,
                    ModelType = modelType,
                    PropertyValidation = new Validation
                    {
                        MainSchema = semanticSchema,
                        InheritedSchemas = GetInheritedSemanticSchemas(entityModelData, localization)
                    },
                    Fields = entityModelData.Content,
                    MetadataFields = entityModelData.Metadata,
                    Localization = localization
                };

                entityModel = (EntityModel) CreateViewModel(mappingData);

                entityModel.Id = entityModelData.Id;
                entityModel.MvcData = mvcData ?? entityModel.GetDefaultView(localization);
            }
        }

        private static ViewModel CreateViewModel(MappingData mappingData)
        {
            ViewModelData viewModelData = mappingData.SourceViewModel;
            EntityModelData entityModelData = viewModelData as EntityModelData;

            ViewModel result;
            if (string.IsNullOrEmpty(mappingData.ModelId))
            {
                // Use parameterless constructor
                result = (ViewModel) mappingData.ModelType.CreateInstance();
            }
            else
            {
                // Pass model Identifier in constructor.
                result = (ViewModel) mappingData.ModelType.CreateInstance(mappingData.ModelId);
            }

            result.ExtensionData = viewModelData.ExtensionData;
            result.HtmlClasses = viewModelData.HtmlClasses;
            result.XpmMetadata = mappingData.Localization.IsXpmEnabled ? viewModelData.XpmMetadata : null;

            MediaItem mediaItem = result as MediaItem;
            if (mediaItem != null)
            {
                BinaryContentData binaryContent = entityModelData?.BinaryContent;
                if (binaryContent == null)
                {
                    throw new DxaException(
                        $"Unable to create Media Item ('{mappingData.ModelType.Name}') because the Data Model '{entityModelData?.Id}' does not contain Binary Content Data."
                        );
                }
                mediaItem.Url = binaryContent.Url;
                mediaItem.FileName = binaryContent.FileName;
                mediaItem.MimeType = binaryContent.MimeType;
                mediaItem.FileSize = binaryContent.FileSize;
            }

            EclItem eclItem = result as EclItem;
            if (eclItem != null)
            {
                ExternalContentData externalContent = entityModelData.ExternalContent;
                if (externalContent == null)
                {
                    throw new DxaException(
                        $"Unable to create ECL Item ('{mappingData.ModelType.Name}') because the Data Model '{entityModelData.Id}' does not contain External Content Data."
                        );
                }
                eclItem.EclDisplayTypeId = externalContent.DisplayTypeId;
                eclItem.EclTemplateFragment = externalContent.TemplateFragment;
                eclItem.EclExternalMetadata = externalContent.Metadata;
                eclItem.EclUri = externalContent.Id;
            }

            MapSemanticProperties(result, mappingData);

            return result;
        }

        private static void MapSemanticProperties(ViewModel viewModel, MappingData mappingData)
        {
            Type modelType = viewModel.GetType();
            IDictionary<string, List<SemanticProperty>> propertySemanticsMap = ModelTypeRegistry.GetPropertySemantics(modelType);
            IDictionary<string, string> xpmPropertyMetadata = new Dictionary<string, string>();
            Validation validation = mappingData.PropertyValidation;

            foreach (KeyValuePair<string, List<SemanticProperty>> propertySemantics in propertySemanticsMap)
            {
                PropertyInfo modelProperty = modelType.GetProperty(propertySemantics.Key);
                List<SemanticProperty> semanticProperties = propertySemantics.Value;

                bool isFieldMapped = false;
                string fieldXPath = null;
                foreach (SemanticProperty semanticProperty in semanticProperties)
                {
                    
                    if (semanticProperty.PropertyName == SemanticProperty.AllFields)
                    {
                        modelProperty.SetValue(viewModel, GetAllFieldsAsDictionary(mappingData));
                        isFieldMapped = true;
                        break;
                    }


                    if ((semanticProperty.PropertyName == SemanticProperty.Self) && validation.MainSchema.HasSemanticType(semanticProperty.SemanticType))
                    {
                        try
                        {
                            object mappedSelf = MapComponentLink((EntityModelData) mappingData.SourceViewModel, modelProperty.PropertyType, mappingData.Localization);
                            modelProperty.SetValue(viewModel, mappedSelf);
                            isFieldMapped = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"Self mapping failed for {modelType.Name}.{modelProperty.Name}: {ex.Message}");
                            continue;
                        }
                    }

                    FieldSemantics fieldSemantics = new FieldSemantics(
                        semanticProperty.SemanticType.Vocab,
                        semanticProperty.SemanticType.EntityName,
                        semanticProperty.PropertyName,
                        null);
                    SemanticSchemaField semanticSchemaField = (mappingData.EmbeddedSemanticSchemaField == null) ?
                        ValidateField(validation, fieldSemantics) :
                        mappingData.EmbeddedSemanticSchemaField.FindFieldBySemantics(fieldSemantics);
                    if (semanticSchemaField == null)
                    {
                        // No matching Semantic Schema Field found for this Semantic Property; maybe another one will match.
                        continue;
                    }

                    // Matching Semantic Schema Field found
                    fieldXPath = IsFieldFromMainSchema(validation, fieldSemantics) ? semanticSchemaField.GetXPath(mappingData.ContextXPath) : null;

                    ContentModelData fields = semanticSchemaField.IsMetadata ? mappingData.MetadataFields : mappingData.Fields;
                    object fieldValue = FindFieldValue(semanticSchemaField, fields, mappingData.EmbedLevel);
                    if (fieldValue == null)
                    {
                        // No field value found; maybe we will find a value for another Semantic Property.
                        continue;
                    }

                    try
                    {
                        object mappedPropertyValue = MapField(fieldValue, modelProperty.PropertyType, semanticSchemaField, mappingData);
                        modelProperty.SetValue(viewModel, mappedPropertyValue);
                    }
                    catch (Exception ex)
                    {
                        throw new DxaException(
                            $"Unable to map field '{semanticSchemaField.Name}' to property {modelType.Name}.{modelProperty.Name} of type '{modelProperty.PropertyType.FullName}'.",
                            ex);
                    }
                    isFieldMapped = true;
                    break;
                }

                if (fieldXPath != null)
                {
                    xpmPropertyMetadata.Add(modelProperty.Name, fieldXPath);
                }
                else if (!isFieldMapped && Log.IsDebugEnabled)
                {
                    string formattedSemanticProperties = string.Join(", ", semanticProperties.Select(sp => sp.ToString()));
                    Log.Debug(
                        $"Property {modelType.Name}.{modelProperty.Name} cannot be mapped to a CM field of {validation.MainSchema}. Semantic properties: {formattedSemanticProperties}.");
                }

            }

            EntityModel entityModel = viewModel as EntityModel;
            if ((entityModel != null) && mappingData.Localization.IsXpmEnabled)
            {
                entityModel.XpmPropertyMetadata = xpmPropertyMetadata;
            }
        }

        private static bool IsFieldFromMainSchema(Validation validation, FieldSemantics fieldSemantics)
        {
            return validation.MainSchema?.FindFieldBySemantics(fieldSemantics) != null;
        }

        private static SemanticSchemaField ValidateField(Validation validation, FieldSemantics fieldSemantics)
        {
            Log.Debug($"Field {fieldSemantics} is validated over the main {validation.MainSchema}.");
            SemanticSchemaField field = validation.MainSchema.FindFieldBySemantics(fieldSemantics);
            if (field == null)
            {
                foreach (SemanticSchema semanticSchema in validation.InheritedSchemas)
                {
                    Log.Debug(
                        $"Field {fieldSemantics} is validated over the inherited {semanticSchema}.");
                    field = semanticSchema.FindFieldBySemantics(fieldSemantics);
                    if (field != null)
                    {
                        break;
                    }
                }
            }

            if (field == null)
            {
                Log.Debug($"Field {fieldSemantics} has not been found.");
            }

            return field;
        }

        private static object FindFieldValue(SemanticSchemaField semanticSchemaField, ContentModelData fields, int embedLevel)
        {
            string[] pathSegments = semanticSchemaField.Path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            object fieldValue = null;
            foreach (string pathSegment in pathSegments.Skip(embedLevel + 1))
            {
                if ((fields == null) || !fields.TryGetValue(pathSegment, out fieldValue))
                {
                    return null;
                }
                if (fieldValue is ContentModelData[])
                {
                    fields = ((ContentModelData[]) fieldValue)[0];
                }
                else
                {
                    fields = fieldValue as ContentModelData;
                }
            }

            return fieldValue;
        }
       
        private static object MapField(object fieldValues, Type modelPropertyType, SemanticSchemaField semanticSchemaField, MappingData mappingData)
        {
            Type sourceType = fieldValues.GetType();
            bool isArray = sourceType.IsArray;
            if (isArray)
            {
                sourceType = sourceType.GetElementType();
            }

            bool isListProperty = modelPropertyType.IsGenericList();
            Type targetType = modelPropertyType.GetUnderlyingGenericListType() ?? modelPropertyType;

            // Convert.ChangeType cannot convert non-nullable types to nullable types, so don't try that.
            Type bareTargetType = modelPropertyType.GetUnderlyingNullableType() ?? targetType;

            IList mappedValues = targetType.CreateGenericList();
            
            if (typeof (EntityModel).IsAssignableFrom(targetType) && sourceType != typeof (EntityModelData) &&
                (sourceType == typeof (string) && string.IsNullOrEmpty((string) fieldValues)))
            {
                return isListProperty ? mappedValues : null;
            }

            switch (sourceType.Name)
            {
                case "String":
                    if (isArray)
                    {
                        foreach (string fieldValue in (string[]) fieldValues)
                        {
                            mappedValues.Add(MapString(fieldValue, bareTargetType));
                        }
                    }
                    else
                    {
                        mappedValues.Add(MapString((string) fieldValues, bareTargetType));
                    }
                    break;

                case "DateTime":
                case "Single":
                case "Double":
                    if (isArray)
                    {
                        foreach (object fieldValue in (Array) fieldValues)
                        {
                            mappedValues.Add(Convert.ChangeType(fieldValue, bareTargetType));
                        }
                    }
                    else
                    {
                        mappedValues.Add(Convert.ChangeType(fieldValues, bareTargetType));
                    }
                    break;

                case "RichTextData":
                    if (isArray)
                    {
                        foreach (RichTextData fieldValue in (RichTextData[]) fieldValues)
                        {
                            mappedValues.Add(MapRichText(fieldValue, targetType, mappingData.Localization));
                        }
                    }
                    else
                    {
                        mappedValues.Add(MapRichText((RichTextData) fieldValues, targetType, mappingData.Localization));
                    }
                    break;

                case "ContentModelData":
                    string fieldXPath = semanticSchemaField.GetXPath(mappingData.ContextXPath);
                    if (isArray)
                    {
                        int index = 1;
                        foreach (ContentModelData embeddedFields in (ContentModelData[]) fieldValues)
                        {
                            string indexedFieldXPath = $"{fieldXPath}[{index++}]";
                            mappedValues.Add(MapEmbeddedFields(embeddedFields, targetType, semanticSchemaField, indexedFieldXPath, mappingData));
                        }
                    }
                    else
                    {
                        string indexedFieldXPath = $"{fieldXPath}[1]";
                        mappedValues.Add(MapEmbeddedFields((ContentModelData) fieldValues, targetType, semanticSchemaField, indexedFieldXPath, mappingData));
                    }
                    break;

                case "EntityModelData":
                    if (isArray)
                    {
                        foreach (EntityModelData entityModelData in (EntityModelData[]) fieldValues)
                        {
                            mappedValues.Add(MapComponentLink(entityModelData, targetType, mappingData.Localization));
                        }
                    }
                    else
                    {
                        mappedValues.Add(MapComponentLink((EntityModelData) fieldValues, targetType, mappingData.Localization));
                    }
                    break;

                case "KeywordModelData":
                    if (isArray)
                    {
                        foreach (KeywordModelData keywordModelData in (KeywordModelData[]) fieldValues)
                        {
                            mappedValues.Add(MapKeyword(keywordModelData, targetType, mappingData.Localization));
                        }
                    }
                    else
                    {
                        mappedValues.Add(MapKeyword((KeywordModelData) fieldValues, targetType, mappingData.Localization));
                    }
                    break;


                default:
                    throw new DxaException($"Unexpected field type: '{sourceType.Name}'.");
            }

            return isListProperty ? mappedValues : ((mappedValues.Count == 0) ? null : mappedValues[0]);
        }

        private static object MapString(string stringValue, Type targetType)
        {
            if (targetType == typeof(RichText))
            {
                return new RichText(stringValue);
            }

            if (targetType == typeof(Link))
            {
                // Trying to map a text field to a Link (?); if the text is a TCM URI, it can work...
                if (!ContentManager.TcmUri.IsValid(stringValue))
                {
                    throw new DxaException($"Cannot map string to type Link: '{stringValue}'");
                }
                return new Link
                {
                    Id = stringValue.Split('-')[1],
                    Url = SiteConfiguration.LinkResolver.ResolveLink(stringValue, resolveToBinary: true)
                };
            }

            if (!string.IsNullOrEmpty(stringValue) && targetType == typeof (int) && stringValue.Contains("."))
            {
                // Simple cast from floating point to int
                return (int) (double)Convert.ChangeType(stringValue, typeof (double), CultureInfo.InvariantCulture.NumberFormat);
            }
            return Convert.ChangeType(stringValue, targetType, CultureInfo.InvariantCulture.NumberFormat);
        }

        private static object MapComponentLink(EntityModelData entityModelData, Type targetType, Localization localization)
        {
            if (targetType == typeof(Link))
            {
                return new Link
                {
                    Id = entityModelData.Id,
                    Url = GetLinkUrl(entityModelData, localization)
                };
            }

            if (targetType == typeof(string))
            {
                return GetLinkUrl(entityModelData, localization);
            }

            if (!typeof(EntityModel).IsAssignableFrom(targetType))
            {
                throw new DxaException($"Cannot map Component Link to property of type '{targetType.Name}'.");
            }

            return ModelBuilderPipeline.CreateEntityModel(entityModelData, targetType, localization);
        }

        private static object MapKeyword(KeywordModelData keywordModelData, Type targetType, Localization localization)
        {
            if (typeof(KeywordModel).IsAssignableFrom(targetType))
            {
                KeywordModel result;
                if (keywordModelData.SchemaId == null)
                {
                    result = new KeywordModel
                    {
                        ExtensionData = keywordModelData.ExtensionData
                    };
                }
                else
                {
                    MappingData keywordMappingData = new MappingData
                    {
                        SourceViewModel = keywordModelData,
                        ModelType = targetType,
                        PropertyValidation = new Validation
                        {
                            MainSchema = SemanticMapping.GetSchema(keywordModelData.SchemaId, localization),
                            InheritedSchemas = GetInheritedSemanticSchemas(keywordModelData as ViewModelData, localization)
                        },
                        MetadataFields = keywordModelData.Metadata,
                        Localization = localization
                    };

                    result = (KeywordModel) CreateViewModel(keywordMappingData);
                }

                result.Id = keywordModelData.Id;
                result.Title = keywordModelData.Title;
                result.Description = keywordModelData.Description ?? string.Empty;
                result.Key = keywordModelData.Key ?? string.Empty;
                result.TaxonomyId = keywordModelData.TaxonomyId;

                return result;
            }

            if (targetType == typeof(Tag))
            {
                return new Tag
                {
                    DisplayText = GetKeywordDisplayText(keywordModelData),
                    Key = keywordModelData.Key,
                    TagCategory = localization.GetCmUri(keywordModelData.TaxonomyId, (int) ItemType.Category)
                };
            }

            if (targetType == typeof(bool))
            {
                string key = string.IsNullOrEmpty(keywordModelData.Key) ? keywordModelData.Title : keywordModelData.Key;
                return Convert.ToBoolean(key);
            }

            return GetKeywordDisplayText(keywordModelData);
        }

        private static string GetKeywordDisplayText(KeywordModelData keywordModelData)
            => string.IsNullOrEmpty(keywordModelData.Description) ? keywordModelData.Title : keywordModelData.Description;

        private static string GetLinkUrl(EntityModelData entityModelData, Localization localization)
        {
            if (entityModelData.LinkUrl != null)
            {
                return entityModelData.LinkUrl;
            }

            Log.Debug($"Link URL for Entity Model '{entityModelData.Id}' not resolved by Model Service.");
            string componentUri = localization.GetCmUri(entityModelData.Id);
            return SiteConfiguration.LinkResolver.ResolveLink(componentUri);
        }

        private static object MapRichText(RichTextData richTextData, Type targetType, Localization localization)
        {
            IList<IRichTextFragment> fragments = new List<IRichTextFragment>();
            foreach (object fragment in richTextData.Fragments)
            {
                string htmlFragment = fragment as string;
                if (htmlFragment == null)
                {
                    // Embedded Entity Model (for Media Items)
                    MediaItem mediaItem = (MediaItem) ModelBuilderPipeline.CreateEntityModel((EntityModelData) fragment, typeof(MediaItem), localization);
                    mediaItem.IsEmbedded = true;
                    if (mediaItem.MvcData == null)
                    {
                        mediaItem.MvcData = mediaItem.GetDefaultView(localization);
                    }
                    fragments.Add(mediaItem);
                }
                else
                {
                    // HTML fragment.
                    fragments.Add(new RichTextFragment(htmlFragment));
                }
            }
            RichText richText = new RichText(fragments);

            if (targetType == typeof(RichText))
            {
                return richText;
            }

            return richText.ToString();
        }

        private static object MapEmbeddedFields(ContentModelData embeddedFields, Type targetType, SemanticSchemaField semanticSchemaField, string contextXPath, MappingData mappingData)
        {
            MappingData embeddedMappingData = new MappingData
            {
                ModelType = targetType,
                PropertyValidation = mappingData.PropertyValidation,
                EmbeddedSemanticSchemaField = semanticSchemaField,
                EmbedLevel = mappingData.EmbedLevel + 1,
                SourceViewModel = mappingData.SourceViewModel,
                Fields = embeddedFields,
                MetadataFields = embeddedFields,
                ContextXPath = contextXPath,
                Localization = mappingData.Localization
            };
            return CreateViewModel(embeddedMappingData);
        }

        private static IDictionary<string, string> GetAllFieldsAsDictionary(MappingData mappingData)
        {
            IDictionary<string, string> result = new Dictionary<string, string>();
            if (mappingData.Fields != null)
            {
                foreach (KeyValuePair<string, object> field in mappingData.Fields)
                {
                    if ((field.Key == "settings"))
                    {
                        throw new NotImplementedException("'settings' field handling"); // TODO
                    }
                    result[field.Key] = GetFieldValuesAsStrings(field.Value, mappingData,  resolveComponentLinks: false).FirstOrDefault();
                }
            }
            if (mappingData.MetadataFields != null)
            {
                foreach (KeyValuePair<string, object> field in mappingData.MetadataFields)
                {
                    result[field.Key] = GetFieldValuesAsStrings(field.Value, mappingData, resolveComponentLinks: false).FirstOrDefault();
                }
            }
            return result;
        }

        private static IEnumerable<string> GetFieldValuesAsStrings(object fieldValues, MappingData mappingData, bool resolveComponentLinks)
        {
            if (!resolveComponentLinks)
            {
                // Handle Component Links here, because standard model mapping will resolve them.
                Localization localization = mappingData.Localization;
                if (fieldValues is EntityModelData)
                {
                    return new[] { localization.GetCmUri(((EntityModelData) fieldValues).Id) };
                }
                if (fieldValues is EntityModelData[])
                {
                    return ((EntityModelData[]) fieldValues).Select(emd => localization.GetCmUri(emd.Id));
                }
            }

            // Use standard model mapping to map the field to a list of strings.
            return (IEnumerable<string>) MapField(fieldValues, typeof(List<string>), null, mappingData);
        }

        private static MvcData CreateMvcData(DataModel.MvcData data, string defaultControllerName)
        {
            if (data == null)
            {
                return null;
            }
            if (string.IsNullOrEmpty(data.ViewName))
            {
                throw new DxaException("No View Name specified in MVC Data.");
            }

            return new MvcData
            {
                ControllerName = data.ControllerName ?? defaultControllerName,
                ControllerAreaName = data.ControllerAreaName ?? SiteConfiguration.GetDefaultModuleName(),
                ActionName = data.ActionName ?? defaultControllerName,
                ViewName = data.ViewName,
                AreaName = data.AreaName ?? SiteConfiguration.GetDefaultModuleName(),
                RouteValues = data.Parameters
            };
        }

        private static RegionModel CreateRegionModel(RegionModelData regionModelData, Localization localization)
        {
            MvcData mvcData = CreateMvcData(regionModelData.MvcData, "Region");
            Type regionModelType = ModelTypeRegistry.GetViewModelType(mvcData);

            RegionModel result = (RegionModel) regionModelType.CreateInstance(regionModelData.Name);

            result.ExtensionData = regionModelData.ExtensionData;
            result.HtmlClasses = regionModelData.HtmlClasses;
            result.MvcData = mvcData;
            result.XpmMetadata = localization.IsXpmEnabled ? regionModelData.XpmMetadata : null;

            if (regionModelData.Regions != null)
            {
                IEnumerable<RegionModel> nestedRegionModels = regionModelData.Regions.Select(data => CreateRegionModel(data, localization));
                result.Regions.UnionWith(nestedRegionModels);
                result.IsVolatile |= result.Regions.Any(region => region.IsVolatile);
            }

            if (regionModelData.Entities != null)
            {
                foreach (EntityModelData entityModelData in regionModelData.Entities)
                {
                    EntityModel entityModel;
                    try
                    {
                        entityModel = ModelBuilderPipeline.CreateEntityModel(entityModelData, null, localization);
                        // indicate to region model that this region is potentially volatile if it contains a volatile entity
                        result.IsVolatile |= entityModel.IsVolatile;
                        entityModel.MvcData.RegionName = regionModelData.Name;
                    }
                    catch (Exception ex)
                    {
                        // If there is a problem mapping an Entity, we replace it with an ExceptionEntity which holds the error details and carry on.
                        Log.Error(ex);
                        entityModel = new ExceptionEntity(ex);
                    }
                    result.Entities.Add(entityModel);
                }
            }

            return result;
        }

        private static string PostProcessPageTitle(PageModelData pageModelData, Localization localization)
        {
            if (pageModelData.MvcData?.ViewName == "IncludePage") return pageModelData.Title;
            IDictionary coreResources = localization.GetResources("core");
            string title = "defaultPageTitle".Equals(pageModelData.Title)
                ? coreResources["core.defaultPageTitle"].ToString()
                : pageModelData.Title;
            string separator = coreResources["core.pageTitleSeparator"].ToString();
            string suffix = coreResources["core.pageTitlePostfix"].ToString();
            return $"{title}{separator}{suffix}";
        }      
    }
}
