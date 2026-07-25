// MIT License
//
// McpOAuth.cs
//
// Copyright (c) 2026 Henning Rauch
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

using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Hosting
{
    /// <summary>
    ///   The OAuth 2.1 resource-server wiring (spec §3.8 Phase C). The server VALIDATES tokens an
    ///   external authorization server issued — it is not an AS. Audience binding is mandatory
    ///   (the <c>aud</c> claim must equal this server's canonical resource identifier), the caller's
    ///   token is never forwarded downstream (§3.9), and a 401 points clients at the RFC 9728
    ///   protected-resource metadata so they can discover where to get a token.
    /// </summary>
    public static class McpOAuth
    {
        /// <summary>The scopes advertised in the metadata and mapped fail-closed to tiers (§3.6).</summary>
        public static readonly String[] SupportedScopes = { "f8:read", "f8:write", "f8:admin", "f8:code" };

        public const String MetadataPath = "/.well-known/oauth-protected-resource";

        public static void AddOAuth(IServiceCollection services, McpAuthOptions auth)
        {
            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    // Keep the original claim types (scope/scp) rather than remapping to long URIs.
                    options.MapInboundClaims = false;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = !String.IsNullOrEmpty(auth.Issuer),
                        ValidIssuer = auth.Issuer,
                        ValidateAudience = !String.IsNullOrEmpty(auth.Audience),
                        ValidAudience = auth.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ClockSkew = TimeSpan.FromSeconds(30),
                    };

                    if (!String.IsNullOrEmpty(auth.SigningKey))
                    {
                        // Test/lab: validate directly against a symmetric key (no discovery).
                        options.TokenValidationParameters.IssuerSigningKey =
                            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey));
                    }
                    else if (!String.IsNullOrEmpty(auth.Issuer))
                    {
                        // Production: discover the issuer's signing keys via its OIDC metadata.
                        options.Authority = auth.Issuer;
                    }

                    options.Events = new JwtBearerEvents
                    {
                        OnChallenge = context =>
                        {
                            // RFC 9728: a 401 tells the client where the protected-resource metadata is.
                            context.HandleResponse();
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            var prm = $"{context.Request.Scheme}://{context.Request.Host}{MetadataPath}";
                            context.Response.Headers["WWW-Authenticate"] = $"Bearer resource_metadata=\"{prm}\"";
                            return Task.CompletedTask;
                        },
                    };
                });

            services.AddAuthorization();
        }

        /// <summary>Serves the RFC 9728 Protected Resource Metadata (anonymously): the canonical
        /// resource identifier, the authorization server(s), and the supported scopes.</summary>
        public static void MapProtectedResourceMetadata(WebApplication app, McpAuthOptions auth)
        {
            app.MapGet(MetadataPath, (HttpContext context) =>
            {
                var resource = String.IsNullOrEmpty(auth.Audience)
                    ? $"{context.Request.Scheme}://{context.Request.Host}"
                    : auth.Audience;

                return Results.Ok(new
                {
                    resource,
                    authorization_servers = String.IsNullOrEmpty(auth.Issuer) ? Array.Empty<String>() : new[] { auth.Issuer },
                    scopes_supported = SupportedScopes,
                    bearer_methods_supported = new[] { "header" },
                });
            }).AllowAnonymous();
        }
    }
}
