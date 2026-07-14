// MIT License
//
// Fallen8SecurityOptions.cs
//
// Copyright (c) 2025 Henning Rauch
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Security configuration for the hosted API, bound from the <c>Fallen8:Security</c> section
    ///   (feature api-security-boundary). Establishes an authentication trust boundary and gates the
    ///   in-process remote-code-execution surface (Roslyn filter compilation + plugin DLL loading).
    ///
    ///   <para>HONEST LIMIT: in-process Roslyn compilation cannot be sandboxed - a compiled delegate
    ///   runs with the server process's full authority. This is a trust boundary (who may reach the
    ///   code endpoints) plus an operator kill switch, NOT a sandbox. Anyone allowed to post a filter
    ///   is trusted as the process.</para>
    /// </summary>
    public sealed class Fallen8SecurityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Security";

        /// <summary>Authorization policy name gating the Roslyn compile endpoints (POST /path, PUT /subgraph).</summary>
        public const String DynamicCodePolicy = "Fallen8.DynamicCodeExecution";

        /// <summary>Authorization policy name gating the plugin-DLL load endpoint (PUT /plugin).</summary>
        public const String DynamicPluginPolicy = "Fallen8.DynamicPluginLoading";

        /// <summary>The authentication scheme name for the built-in API-key handler.</summary>
        public const String ApiKeyScheme = "Fallen8ApiKey";

        /// <summary>Rate-limiter policy name applied to the expensive/dangerous code + plugin endpoints.</summary>
        public const String SensitiveRateLimitPolicy = "Fallen8.SensitiveEndpoints";

        /// <summary>
        ///   The API key required in the <see cref="ApiKeyHeader"/>. Supply from user-secrets or
        ///   environment - NEVER a checked-in default. When null/blank the API-key scheme authenticates
        ///   nobody: the server logs a prominent warning and runs UNAUTHENTICATED (mitigated by the
        ///   loopback-by-default bind and the code/plugin endpoints being off by default).
        /// </summary>
        public String ApiKey { get; set; }

        /// <summary>Header carrying the API key. Defaults to <c>X-Api-Key</c>.</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>
        ///   Master switch for the Roslyn compile endpoints (POST /path, PUT /subgraph). Default false:
        ///   a disabled server returns 403 before any compilation. Turning it on means an authenticated
        ///   caller can run arbitrary in-process code (see the honest-limit note).
        /// </summary>
        public Boolean EnableDynamicCodeExecution { get; set; } = false;

        /// <summary>
        ///   Master switch for uploading + loading plugin DLLs (PUT /plugin). Default false: a disabled
        ///   server returns 403 and writes nothing.
        /// </summary>
        public Boolean EnableDynamicPluginLoading { get; set; } = false;

        /// <summary>
        ///   Directory uploaded plugin DLLs are written to and discovered from. Defaults to a
        ///   <c>plugins</c> subdirectory of <see cref="AppContext.BaseDirectory"/> - never the app's own
        ///   binary directory, so an upload cannot plant a DLL next to the server binaries.
        /// </summary>
        public String PluginDirectory { get; set; }

        /// <summary>
        ///   Origins allowed by the CORS policy. Empty (default) means deny all cross-origin requests.
        ///   No wildcard-with-credentials is ever configured.
        /// </summary>
        public String[] AllowedCorsOrigins { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   Maximum request body size (bytes) for the code + plugin endpoints. Defaults to 1 MiB - a
        ///   filter fragment or a plugin DLL over this is rejected with 413 before it is buffered.
        /// </summary>
        public Int64 MaxSensitiveRequestBodyBytes { get; set; } = 1L * 1024 * 1024;

        /// <summary>
        ///   Permitted request count per fixed window for the sensitive (code/plugin) endpoints. A
        ///   breach returns 429. Defaults to 30 per <see cref="RateLimitWindowSeconds"/>.
        /// </summary>
        public Int32 SensitiveRateLimitPermitPerWindow { get; set; } = 30;

        /// <summary>Fixed-window length (seconds) for the sensitive-endpoint rate limiter. Default 10.</summary>
        public Int32 RateLimitWindowSeconds { get; set; } = 10;

        /// <summary>
        ///   When false (default) the app binds to loopback only, so the anonymous-by-default surface is
        ///   not reachable off-box unless the operator opts in. When true the app honours the configured
        ///   Kestrel/Urls binding.
        /// </summary>
        public Boolean AllowRemoteAccess { get; set; } = false;

        /// <summary>Resolves <see cref="PluginDirectory"/>, defaulting to <c>&lt;base&gt;/plugins</c>.</summary>
        public String ResolvePluginDirectory()
        {
            return String.IsNullOrWhiteSpace(PluginDirectory)
                ? System.IO.Path.Combine(AppContext.BaseDirectory, "plugins")
                : PluginDirectory;
        }
    }
}
