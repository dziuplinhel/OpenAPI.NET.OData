// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Microsoft.OData.Edm;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Models.References;
using Microsoft.OpenApi.OData.Common;
using Microsoft.OpenApi.OData.Edm;
using Microsoft.OpenApi.OData.Vocabulary.Core;

namespace Microsoft.OpenApi.OData.Generator
{
    /// <summary>
    /// Extension methods to create <see cref="OpenApiResponse"/> by Edm model.
    /// </summary>
    internal static class OpenApiResponseGenerator
    {
        private static IDictionary<string, OpenApiResponse> _responses =
           new Dictionary<string, OpenApiResponse>
           {
                { Constants.StatusCodeDefault,
                    new OpenApiResponseReference(Constants.Error, null)
                },

                { Constants.StatusCode204, new OpenApiResponse { Description = Constants.Success} },
                { Constants.StatusCode201, new OpenApiResponse { Description = Constants.Created} },
                { Constants.StatusCodeClass2XX, new OpenApiResponse { Description = Constants.Success} },
                { Constants.StatusCodeClass4XX, new OpenApiResponseReference(Constants.Error, null)},
                { Constants.StatusCodeClass5XX, new OpenApiResponseReference(Constants.Error, null)}
           };

        /// <summary>
        /// Get the <see cref="OpenApiResponse"/> for the build-in statusCode.
        /// </summary>
        /// <param name="statusCode">The status code.</param>
        /// <returns>The created <see cref="OpenApiResponse"/>.</returns>
        public static OpenApiResponse GetResponse(this string statusCode)
        {
            if (_responses.TryGetValue(statusCode, out OpenApiResponse response))
            {
                return response;
            }

            return null;
        }

        /// <summary>
        /// Field responses in components
        /// The value of responses is a map of Response Objects.
        /// It contains one name/value pair for the standard OData error response
        /// that is referenced from all operations of the service.
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="document">The OpenApi document to lookup references.</param>
        /// <returns>The name/value pairs for the standard OData error response.</returns>
        public static IDictionary<string, OpenApiResponse> CreateResponses(this ODataContext context, OpenApiDocument document)
        {
            Utils.CheckArgumentNull(context, nameof(context));

            var responses =  new Dictionary<string, OpenApiResponse>
            {
                { "error", context.CreateErrorResponse(document) }
            };

            if(context.Settings.EnableDollarCountPath)
            {
                responses[Constants.DollarCountSchemaName] = CreateCountResponse(document);
            }

            responses = responses.Concat(context.GetAllCollectionEntityTypes()
                                        .Select(x => new KeyValuePair<string, OpenApiResponse>(
                                                            $"{(x is IEdmEntityType eType ? eType.FullName() : x.FullTypeName())}{Constants.CollectionSchemaSuffix}",
                                                            CreateCollectionResponse(x, document)))
                                        .Where(x => !responses.ContainsKey(x.Key)))
                                .Concat(context.GetAllCollectionComplexTypes()
                                        .Select(x => new KeyValuePair<string, OpenApiResponse>(
                                                            $"{x.FullTypeName()}{Constants.CollectionSchemaSuffix}",
                                                            CreateCollectionResponse(x, document)))
                                        .Where(x => !responses.ContainsKey(x.Key)))
                            .ToDictionary(x => x.Key, x => x.Value);

            if(context.HasAnyNonContainedCollections())                                        
                responses[$"String{Constants.CollectionSchemaSuffix}"] = CreateCollectionResponse("String", document);

            foreach (IEdmOperation operation in context.Model.SchemaElements.OfType<IEdmOperation>()
                .Where(op => context.Model.OperationTargetsMultiplePaths(op)))
            {
                OpenApiResponse response = context.CreateOperationResponse(operation, document);
                if (response != null)
                    responses[$"{operation.Name}Response"] = response;
            }

            return responses;
        }

        /// <summary>
        /// Create the <see cref="OpenApiResponses"/> for a <see cref="IEdmOperationImport"/>
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="operationImport">The Edm operation import.</param>
        /// <param name="document">The OpenApi document to lookup references.</param>
        /// <returns>The created <see cref="OpenApiResponses"/>.</returns>
        public static OpenApiResponses CreateResponses(this ODataContext context, IEdmOperationImport operationImport, OpenApiDocument document)
        {
            Utils.CheckArgumentNull(context, nameof(context));
            Utils.CheckArgumentNull(operationImport, nameof(operationImport));
            Utils.CheckArgumentNull(document, nameof(document));

            return context.CreateResponses(operationImport.Operation, document);
        }

        /// <summary>
        /// Create the <see cref="OpenApiResponses"/> for a <see cref="IEdmOperation"/>
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="operation">The Edm operation.</param>
        /// <param name="document">The OpenApi document to lookup references.</param>
        /// <returns>The created <see cref="OpenApiResponses"/>.</returns>
        public static OpenApiResponses CreateResponses(this ODataContext context, IEdmOperation operation, OpenApiDocument document)
        {
            Utils.CheckArgumentNull(context, nameof(context));
            Utils.CheckArgumentNull(operation, nameof(operation));

            OpenApiResponses responses = new();
            
            if (operation.IsAction() && operation.ReturnType == null)
            {
                responses.Add(Constants.StatusCode204, Constants.StatusCode204.GetResponse());
            }
            else if (context.Model.OperationTargetsMultiplePaths(operation))
            {
                responses.Add(
                    context.Settings.UseSuccessStatusCodeRange ? Constants.StatusCodeClass2XX : Constants.StatusCode200,
                    new OpenApiResponseReference($"{operation.Name}Response", null)
                 );
            }
            else
            {
                OpenApiResponse response = context.CreateOperationResponse(operation, document);
                responses.Add(context.Settings.UseSuccessStatusCodeRange ? Constants.StatusCodeClass2XX : Constants.StatusCode200, response);
            }

            if (context.Settings.ErrorResponsesAsDefault)
            {
                responses.Add(Constants.StatusCodeDefault, Constants.StatusCodeDefault.GetResponse());
            }
            else
            {
                responses.Add(Constants.StatusCodeClass4XX, Constants.StatusCodeClass4XX.GetResponse());
                responses.Add(Constants.StatusCodeClass5XX, Constants.StatusCodeClass5XX.GetResponse());
            }

            return responses;
        }

        public static OpenApiResponse CreateOperationResponse(this ODataContext context, IEdmOperation operation, OpenApiDocument document)
        {
            if (operation.ReturnType == null)
                return null;

            OpenApiSchema schema;
            if (operation.ReturnType.IsCollection())
            {
                OpenApiSchema baseSchema = new()
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        {
                            "value", context.CreateEdmTypeSchema(operation.ReturnType, document)
                        }
                    }
                };

                if (context.Settings.EnableODataAnnotationReferencesForResponses && 
                    (operation.IsDeltaFunction() || context.Settings.EnablePagination || context.Settings.EnableCount))
                {
                    schema = new OpenApiSchema
                    {
                        AllOf = new List<OpenApiSchema>
                        {
                            new OpenApiSchemaReference(operation.IsDeltaFunction() ? Constants.BaseDeltaFunctionResponse  // @odata.nextLink + @odata.deltaLink
                                        : Constants.BaseCollectionPaginationCountResponse // @odata.nextLink + @odata.count)
                                        ,document),
                            baseSchema
                        }
                    };
                }
                else if (operation.IsDeltaFunction())
                {
                    baseSchema.Properties.Add(ODataConstants.OdataNextLink);
                    baseSchema.Properties.Add(ODataConstants.OdataDeltaLink);
                    schema = baseSchema;
                }
                else
                {
                    if (context.Settings.EnablePagination)
                    {
                        baseSchema.Properties.Add(ODataConstants.OdataNextLink);
                    }
                    if (context.Settings.EnableCount)
                    {
                        baseSchema.Properties.Add(ODataConstants.OdataCount);
                    }
                    schema = baseSchema;
                }

                schema.Title = operation.ReturnType.Definition.AsElementType() is not IEdmEntityType entityType
                        ? null : $"Collection of {entityType.Name}";
                schema.Type = JsonSchemaType.Object;             
            }
            else if (operation.ReturnType.IsPrimitive())
            {
                // A property or operation response that is of a primitive type is represented as an object with a single name/value pair,
                // whose name is value and whose value is a primitive value.
                schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        {
                            "value", context.CreateEdmTypeSchema(operation.ReturnType, document)
                        }
                    }
                };
            }
            else
            {
                schema = context.CreateEdmTypeSchema(operation.ReturnType, document);
            }

            string mediaType = Constants.ApplicationJsonMediaType;
            if (operation.ReturnType.AsPrimitive()?.PrimitiveKind() == EdmPrimitiveTypeKind.Stream)
            {
                mediaType = context.Model.GetString(operation, CoreConstants.MediaType);

                if (string.IsNullOrEmpty(mediaType))
                {
                    // Use default if MediaType annotation is not specified
                    mediaType = Constants.ApplicationOctetStreamMediaType;
                }
            }

            OpenApiResponse response = new()
            {
                Description = "Success",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    {
                        mediaType,
                        new OpenApiMediaType
                        {
                            Schema = schema
                        }
                    }
                }
            };

            return response;
        }

        private static OpenApiResponse CreateCollectionResponse(IEdmStructuredType structuredType, OpenApiDocument document)
        {
            var entityType = structuredType as IEdmEntityType;
            return CreateCollectionResponse(entityType?.FullName() ?? structuredType.FullTypeName(), document);
        }
        private static OpenApiResponse CreateCollectionResponse(string typeName, OpenApiDocument document)
        {
            return new OpenApiResponse
            {
                Description = "Retrieved collection",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    {
                        Constants.ApplicationJsonMediaType,
                        new OpenApiMediaType
                        {
                            Schema = new OpenApiSchemaReference($"{typeName}{Constants.CollectionSchemaSuffix}", document) 
                        }
                    }
                }
            };
        }

        private static OpenApiResponse CreateCountResponse(OpenApiDocument document)
        {
            OpenApiSchema schema = new OpenApiSchemaReference(Constants.DollarCountSchemaName, document);
            return new OpenApiResponse
            {
                Description = "The count of the resource",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    {
                        "text/plain",
                        new OpenApiMediaType
                        {
                            Schema = schema
                        }
                    }
                }
            };
        }

        private static OpenApiResponse CreateErrorResponse(this ODataContext context, OpenApiDocument document)
        {
            var errorNamespaceName = context.GetErrorNamespaceName();
            return new OpenApiResponse
            {
                Description = "error",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    {
                        Constants.ApplicationJsonMediaType,
                        new OpenApiMediaType
                        {
                            Schema = new OpenApiSchemaReference($"{errorNamespaceName}{OpenApiErrorSchemaGenerator.ODataErrorClassName}", document)
                        }
                    }
                }
            };
        }
    }
}
