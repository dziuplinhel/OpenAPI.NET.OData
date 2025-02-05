// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.OData.Edm;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Models.Interfaces;
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
        /// <summary>
        /// Get the <see cref="IOpenApiResponse"/> for the build-in statusCode.
        /// </summary>
        /// <param name="statusCode">The status code.</param>
        /// <returns>The created <see cref="IOpenApiResponse"/>.</returns>
        public static IOpenApiResponse GetResponse(this string statusCode)
        {
            return statusCode switch {
                Constants.StatusCodeDefault => new OpenApiResponseReference(Constants.Error),
                Constants.StatusCode204 => new OpenApiResponse { Description = Constants.Success},
                Constants.StatusCode201 => new OpenApiResponse { Description = Constants.Created},
                Constants.StatusCodeClass2XX => new OpenApiResponse { Description = Constants.Success},
                Constants.StatusCodeClass4XX => new OpenApiResponseReference(Constants.Error),
                Constants.StatusCodeClass5XX => new OpenApiResponseReference(Constants.Error),
                _ => null,
            };
        }

        /// <summary>
        /// Field responses in components
        /// The value of responses is a map of Response Objects.
        /// It contains one name/value pair for the standard OData error response
        /// that is referenced from all operations of the service.
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="document">The OpenApi document to lookup references.</param>
        public static void AddResponsesToDocument(this ODataContext context, OpenApiDocument document)
        {
            Utils.CheckArgumentNull(context, nameof(context));
            Utils.CheckArgumentNull(document, nameof(document));

            var responses =  new Dictionary<string, OpenApiResponse>
            {
                { "error", context.CreateErrorResponse() }
            };

            if(context.Settings.EnableDollarCountPath)
            {
                responses[Constants.DollarCountSchemaName] = CreateCountResponse();
            }

            responses = responses.Concat(context.GetAllCollectionEntityTypes()
                                        .Select(x => new KeyValuePair<string, OpenApiResponse>(
                                                            $"{(x is IEdmEntityType eType ? eType.FullName() : x.FullTypeName())}{Constants.CollectionSchemaSuffix}",
                                                            CreateCollectionResponse(x)))
                                        .Where(x => !responses.ContainsKey(x.Key)))
                                .Concat(context.GetAllCollectionComplexTypes()
                                        .Select(x => new KeyValuePair<string, OpenApiResponse>(
                                                            $"{x.FullTypeName()}{Constants.CollectionSchemaSuffix}",
                                                            CreateCollectionResponse(x)))
                                        .Where(x => !responses.ContainsKey(x.Key)))
                            .ToDictionary(x => x.Key, x => x.Value);

            if(context.HasAnyNonContainedCollections())                                        
                responses[$"String{Constants.CollectionSchemaSuffix}"] = CreateCollectionResponse("String");

            foreach (IEdmOperation operation in context.Model.SchemaElements.OfType<IEdmOperation>()
                .Where(op => context.Model.OperationTargetsMultiplePaths(op)))
            {
                OpenApiResponse response = context.CreateOperationResponse(operation);
                if (response != null)
                    responses[$"{operation.Name}Response"] = response;
            }

            foreach (var response in responses)
            {
                document.AddComponent(response.Key, response.Value);
            }
        }

        /// <summary>
        /// Create the <see cref="OpenApiResponses"/> for a <see cref="IEdmOperationImport"/>
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="operationImport">The Edm operation import.</param>
        /// <returns>The created <see cref="OpenApiResponses"/>.</returns>
        public static OpenApiResponses CreateResponses(this ODataContext context, IEdmOperationImport operationImport)
        {
            Utils.CheckArgumentNull(context, nameof(context));
            Utils.CheckArgumentNull(operationImport, nameof(operationImport));

            return context.CreateResponses(operationImport.Operation);
        }

        /// <summary>
        /// Create the <see cref="OpenApiResponses"/> for a <see cref="IEdmOperation"/>
        /// </summary>
        /// <param name="context">The OData context.</param>
        /// <param name="operation">The Edm operation.</param>
        /// <returns>The created <see cref="OpenApiResponses"/>.</returns>
        public static OpenApiResponses CreateResponses(this ODataContext context, IEdmOperation operation)
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
                    new OpenApiResponseReference($"{operation.Name}Response")
                 );
            }
            else
            {
                OpenApiResponse response = context.CreateOperationResponse(operation);
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

        public static OpenApiResponse CreateOperationResponse(this ODataContext context, IEdmOperation operation)
        {
            if (operation.ReturnType == null)
                return null;

            IOpenApiSchema schema;
            if (operation.ReturnType.IsCollection())
            {
                OpenApiSchema baseSchema = new()
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        {
                            "value", context.CreateEdmTypeSchema(operation.ReturnType)
                        }
                    }
                };

                if (context.Settings.EnableODataAnnotationReferencesForResponses && 
                    (operation.IsDeltaFunction() || context.Settings.EnablePagination || context.Settings.EnableCount))
                {
                    schema = new OpenApiSchema
                    {
                        AllOf =
                        [
                            new OpenApiSchemaReference(operation.IsDeltaFunction() ? Constants.BaseDeltaFunctionResponse  // @odata.nextLink + @odata.deltaLink
                                        : Constants.BaseCollectionPaginationCountResponse // @odata.nextLink + @odata.count)
                                        ),
                            baseSchema
                        ]
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

                if (schema is OpenApiSchema openApiSchema)
                {
                    openApiSchema.Title = operation.ReturnType.Definition.AsElementType() is not IEdmEntityType entityType
                            ? null : $"Collection of {entityType.Name}";
                    openApiSchema.Type = JsonSchemaType.Object;
                }
            }
            else if (operation.ReturnType.IsPrimitive())
            {
                // A property or operation response that is of a primitive type is represented as an object with a single name/value pair,
                // whose name is value and whose value is a primitive value.
                schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        {
                            "value", context.CreateEdmTypeSchema(operation.ReturnType)
                        }
                    }
                };
            }
            else
            {
                schema = context.CreateEdmTypeSchema(operation.ReturnType);
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

        private static OpenApiResponse CreateCollectionResponse(IEdmStructuredType structuredType)
        {
            var entityType = structuredType as IEdmEntityType;
            return CreateCollectionResponse(entityType?.FullName() ?? structuredType.FullTypeName());
        }
        private static OpenApiResponse CreateCollectionResponse(string typeName)
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
                            Schema = new OpenApiSchemaReference($"{typeName}{Constants.CollectionSchemaSuffix}") 
                        }
                    }
                }
            };
        }

        private static OpenApiResponse CreateCountResponse()
        {
            var schema = new OpenApiSchemaReference(Constants.DollarCountSchemaName);
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

        private static OpenApiResponse CreateErrorResponse(this ODataContext context)
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
                            Schema = new OpenApiSchemaReference($"{errorNamespaceName}{OpenApiErrorSchemaGenerator.ODataErrorClassName}")
                        }
                    }
                }
            };
        }
    }
}
