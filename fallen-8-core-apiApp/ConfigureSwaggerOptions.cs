// MIT License
//
// ConfigureSwaggerOptions.cs
//
// Copyright (c) 2022 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace NoSQL.GraphDB.App
{
    using System;
    using System.Linq;

    using Microsoft.AspNetCore.Mvc.ApiExplorer;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using Microsoft.OpenApi.Models;

    using Swashbuckle.AspNetCore.SwaggerGen;

    /// <summary>
    /// Configures the Swagger generation options.
    /// </summary>
    public class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
    {
        #region member vars

        private readonly IApiVersionDescriptionProvider _provider;

        #endregion

        #region constructors and destructors

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigureSwaggerOptions" /> class.
        /// </summary>
        public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider)
        {
            _provider = provider;
        }

        #endregion

        #region explicit interfaces

        public void Configure(SwaggerGenOptions options)
        {
            // add a swagger document for each discovered API version
            // note: you might choose to skip or document deprecated API versions differently
            foreach (var description in _provider.ApiVersionDescriptions)
            {
                options.SwaggerDoc(description.GroupName, CreateInfoForApiVersion(description));
            }
        }

        #endregion

        #region methods

        /// <summary>
        /// Internal implementation for building the Swagger basic config.
        /// </summary>
        /// <param name="description">The description object containing the.</param>
        /// <returns>The generated Open API info.</returns>
        private static OpenApiInfo CreateInfoForApiVersion(ApiVersionDescription description)
        {
            var info = new OpenApiInfo
            {
                Title = "Fallen-8 API",
                Version = description.ApiVersion.ToString(),
                Description = @"<p>Fallen-8 API with versioning including Swagger.</p>",
                Contact = new OpenApiContact
                {
                    Name = "Henning Rauch",
                    Email = "Henning@RauchEntwicklung.biz"
                }
            };
            if (description.IsDeprecated)
            {
                info.Description += @"<p><strong><span style=""color:white;background-color:red"">VERSION IS DEPRECATED</span></strong></p>";
            }
            return info;
        }

        #endregion
    }
}