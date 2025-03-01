﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OpenApi.Models;
using Smartstore.Web.Api.Security;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Smartstore.Web.Api.Swagger
{
    /// <summary>
    /// Adds information to <see cref="OpenApiOperation"/> like describing <see cref="OpenApiResponse"/> objects
    /// for repeating methods in OData controllers (like Get, Post, Put, Patch, Delete etc.).
    /// Only takes into account OData controllers that inherit from SmartODataController.
    /// </summary>
    internal class SwaggerOperationFilter : IOperationFilter
    {
        private static readonly string[] _knownMethodNames = new[] { "Get", "Post", "Put", "Patch", "Delete" };
        private static readonly Type[] _parameterTypesToRemove = new[] { typeof(ODataQueryOptions) };

        private static readonly AllowedQueryOptions[] _supportedQueryOptions = new[]
        {
            AllowedQueryOptions.Top,
            AllowedQueryOptions.Skip,
            AllowedQueryOptions.OrderBy,
            AllowedQueryOptions.Count,
            AllowedQueryOptions.Select,
            AllowedQueryOptions.Expand,
            AllowedQueryOptions.Filter,
            AllowedQueryOptions.Search,
            AllowedQueryOptions.Compute
        };

        // Perf: avoids multiple creation of the same query parameters.
        private readonly Dictionary<string, OpenApiParameter> _queryParameters = new();

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            try
            {
                // Skip what is not inherited from our SmartODataController.
                if (context.MethodInfo.DeclaringType.BaseType.IsClosedGenericTypeOf(typeof(SmartODataController<>)))
                {
                    var helper = new SwaggerOperationHelper(operation, context);

                    AddDocumentation(helper);
                    AddQueryParameters(helper);
                    RemoveParameters(helper);
                }
            }
            catch (Exception ex)
            {
                ex.Dump();
            }
        }

        protected virtual void RemoveParameters(SwaggerOperationHelper helper)
        {
            helper.Op.Parameters.Remove(param =>
            {
                var refId = param?.Schema?.Reference?.Id;

                return refId != null && _parameterTypesToRemove.Any(type => refId.StartsWithNoCase(type.FullName));
            });
        }

        /// <summary>
        /// Adds documentation for known OData controller methods.
        /// </summary>
        protected virtual void AddDocumentation(SwaggerOperationHelper helper)
        {
            var mi = helper.Context.MethodInfo;
            var isSingleResult = mi.ReturnType.IsClosedGenericTypeOf(typeof(SingleResult<>));
            var isQueryResult = mi.ReturnType.IsClosedGenericTypeOf(typeof(IQueryable<>));
            var isNavigationProperty = false;
            var canProcess = _knownMethodNames.Contains(mi.Name);

            if (!canProcess && helper.ActionName.StartsWith("Get") && helper.HttpMethod.EqualsNoCase("Get"))
            {
                isNavigationProperty = isSingleResult || isQueryResult;
                canProcess = isNavigationProperty;
            }

            if (canProcess)
            {
                helper.Op.Responses.Clear();
            }
            else
            {
                // Unknown methods like OData actions and functions.
                // These must be documented via code comment above the related action method.
                helper.Op.Parameters
                    .Where(x => x.Name.EqualsNoCase("key") && x.Description.IsEmpty())
                    .Each(x => x.Description = $"The {helper.EntityType.Name} identifier.");
            }

            if (mi.DeclaringType.HasAttribute<AuthorizeAttribute>(true) || mi.HasAttribute<AuthorizeAttribute>(true))
            {
                helper.Op.Responses[Status401Unauthorized.ToString()] = CreateUnauthorizedResponse();
            }

            if (!canProcess)
            {
                return;
            }

            helper.Op.Responses[Status400BadRequest.ToString()] = CreateBadRequestResponse();

            var entityName = PrefixArticle(helper.EntityType.Name);

            //if (helper.EntityType.Name == "FileItemInfo")
            //{
            //    helper.EntityType.Name.Dump();
            //    helper.Op.Parameters.Where(x => x.Schema)
            //}

            switch (helper.ActionName)
            {
                case "Get":
                    if (helper.HasKeyParameter)
                    {
                        helper.Op.Summary ??= $"Gets {entityName} by identifier.";
                        helper.Op.Responses[Status200OK.ToString()] = helper.CreateSucccessResponse(true);
                        helper.Op.Responses[Status404NotFound.ToString()] = CreateNotFoundResponse();
                        helper.AddKeyParameter();
                    }
                    else
                    {
                        helper.Op.Summary ??= $"Gets {entityName} list.";
                        helper.Op.Responses[Status200OK.ToString()] = helper.CreateSucccessResponse(false);
                    }
                    break;

                //case "GetProperty":
                //    helper.Op.Summary ??= $"Gets a property value of a {helper.EntityType.Name}.";
                //    helper.Op.Description ??= "A property value can alternatively be obtained using the **$select** query string parameter.";
                //    helper.Op.Responses[Status200OK.ToString()] = helper.CreateSucccessResponse(null);
                //    helper.Op.Responses[Status404NotFound.ToString()] = CreateNotFoundResponse();
                //    helper.AddKeyParameter();
                //    helper.AddPropertyParameter();
                //    break;

                case "Post":
                    helper.Op.Summary ??= $"Creates {entityName}.";
                    helper.Op.RequestBody = helper.CreateRequestBody();
                    helper.Op.Responses[Status201Created.ToString()] = helper.CreateSucccessResponse(true);
                    break;

                case "Put":
                case "Patch":
                    helper.Op.Summary ??= (helper.ActionName == "Patch"
                        ? $"Partially updates {entityName}."
                        : $"Updates {entityName}.");

                    helper.Op.RequestBody = helper.CreateRequestBody();
                    helper.Op.Responses[Status200OK.ToString()] = helper.CreateSucccessResponse(true);
                    helper.Op.Responses[Status204NoContent.ToString()] = CreateNoContentResponse();
                    helper.Op.Responses[Status404NotFound.ToString()] = CreateNotFoundResponse();
                    helper.Op.Responses[Status409Conflict.ToString()] = CreateConflictResponse();
                    helper.Op.Responses[Status422UnprocessableEntity.ToString()] = CreateUnprocessableEntityResponse();
                    helper.AddKeyParameter();
                    break;

                case "Delete":
                    helper.Op.Summary ??= $"Deletes {entityName}.";
                    helper.Op.Responses[Status204NoContent.ToString()] = CreateNoContentResponse();
                    helper.Op.Responses[Status404NotFound.ToString()] = CreateNotFoundResponse();
                    helper.AddKeyParameter();
                    break;

                default:
                    if (isNavigationProperty)
                    {
                        var navPropType = mi.ReturnType.GenericTypeArguments[0];                        

                        helper.Op.Summary ??= isQueryResult
                            ? $"Gets {navPropType.Name.NaIfEmpty()} entities assigned to {entityName}."
                            : $"Gets the {navPropType.Name.NaIfEmpty()} assigned to {entityName}.";

                        helper.Op.Responses[Status200OK.ToString()] = helper.CreateSucccessResponse(isSingleResult, navPropType);

                        if (isSingleResult)
                        {
                            helper.Op.Responses[Status404NotFound.ToString()] = CreateNotFoundResponse();
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Adds input fields for query parameters to Swagger documentation.
        /// </summary>
        protected virtual void AddQueryParameters(SwaggerOperationHelper helper)
        {
            if (!helper.HttpMethod.EqualsNoCase("Get"))
            {
                return;
            }

            var attribute = helper.ActionDescriptor?.FilterDescriptors
                .Where(x => x.Filter is EnableQueryAttribute)
                .Select(x => x.Filter as EnableQueryAttribute)
                .FirstOrDefault();

            if (attribute == null)
            {
                return;
            }

            var allowedOptions = _supportedQueryOptions.Where(x => attribute.AllowedQueryOptions.HasFlag(x));
            if (helper.HasKeyParameter)
            {
                allowedOptions = allowedOptions.Where(x => x == AllowedQueryOptions.Select || x == AllowedQueryOptions.Compute);
            }

            foreach (var option in allowedOptions)
            {
                var key = helper.EntityType.FullName + option.ToString();

                if (!_queryParameters.TryGetValue(key, out var p))
                {
                    p = new OpenApiParameter
                    {
                        Required = false,
                        In = ParameterLocation.Query
                    };

                    // INFO: OpenApiParameter.Example is not suitable for optional inputs. It remains in the input field when Try-It-Out is clicked.
                    // The user would have to delete or update it for all filters before execution.
                    var example = helper.BuildQueryExample(option).EmptyNull();

                    switch (option)
                    {
                        case AllowedQueryOptions.Filter:
                            p.Name = "$filter";
                            p.Description = "Expression(s) to limit the results to be returned. " + example;
                            break;
                        case AllowedQueryOptions.Expand:
                            p.Name = "$expand";
                            p.Description = "A comma-separated list of related entities to be included inline in the response. " + example;
                            break;
                        case AllowedQueryOptions.Select:
                            p.Name = "$select";
                            p.Description = "A comma-separated list of properties to be included in the response. " + example;
                            break;
                        case AllowedQueryOptions.OrderBy:
                            p.Name = "$orderby";
                            p.Description = "A comma-separated list of expression(s) to order the returned entities. " + example;
                            break;
                        case AllowedQueryOptions.Top:
                            p.Name = "$top";
                            p.Description = "The number of returned entities. " + example;
                            p.Schema ??= helper.GenerateSchema(typeof(int));
                            break;
                        case AllowedQueryOptions.Skip:
                            p.Name = "$skip";
                            p.Description = "The number of skipped and not included entities. " + example;
                            p.Schema ??= helper.GenerateSchema(typeof(int));
                            break;
                        case AllowedQueryOptions.Count:
                            p.Name = "$count";
                            p.Description = "Requests the total number of matching entities included in the response. " + example;
                            p.Schema ??= helper.GenerateSchema(typeof(bool));
                            break;
                        case AllowedQueryOptions.Compute:
                            p.Name = "$compute";
                            p.Description = "Defines a computed property that can be used in a $select, $filter or $orderby expression.<br>" + example;
                            break;
                        case AllowedQueryOptions.Search:
                            // TODO (?): $search can be empowered by own matching logic by implementing ISearchBinder.
                            // https://devblogs.microsoft.com/odata/compute-and-search-in-asp-net-core-odata-8/
                            p.Name = "$search";
                            p.Description = "Expression to limit the results matching a free-text search. The definition of what *matching* means is up to the service. " + example;
                            break;
                    }

                    p.Schema ??= helper.GenerateSchema(typeof(string));
                    _queryParameters[key] = p;
                }

                helper.Op.Parameters.Add(p);
            }
        }

        #region Utilities

        private static OpenApiResponse CreateUnauthorizedResponse()
            => new() { Description = $"Unauthorized API request. The exact reason is provided by the **{BasicAuthenticationHandler.ResultDescriptionHeader}** response header." };

        private static OpenApiResponse CreateNotFoundResponse()
            => new() { Description = "The requested resource was not found." };

        private static OpenApiResponse CreateBadRequestResponse()
            => new() { Description = "Bad request. Occurs, for example, when the data sent is incorrect." };

        private static OpenApiResponse CreateNoContentResponse()
            => new() { Description = "The request has succeeded. There is no content provided." };

        private static OpenApiResponse CreateConflictResponse()
            => new() { Description = "The request failed due to a conflict. The most common cause of this failure is a concurrency violation at the related entity." };

        private static OpenApiResponse CreateUnprocessableEntityResponse()
            => new() { Description = "The processing of the associated entity failed. Details about the reason can be found in the response message." };

        private static string PrefixArticle(string str)
        {
            if (str.HasValue())
            {
                return ("aeiouAEIOU".Contains(str[0]) ? "an " : "a ") + str;
            }

            return str;
        }

        #endregion
    }
}
