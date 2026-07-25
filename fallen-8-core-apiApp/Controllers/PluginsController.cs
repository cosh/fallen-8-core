// MIT License
//
// PluginsController.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Manages the per-namespace plugin registry: typed, source-based registration of runtime
    ///   plugins that replaces the removed DLL-upload path (feature plugin-registration).
    /// </summary>
    /// <remarks>
    ///   The operating model: author a WHOLE plugin type as C# source against a category scaffold,
    ///   POST it to the category's typed endpoint (the server compiles with Roslyn and validates it
    ///   against the category contract), and it registers into the ADDRESSED namespace's registry.
    ///   Registered algorithm plugins are then invoked transparently by name through the existing
    ///   path/subgraph/analytics endpoints; graph functions are invoked here by name.
    ///
    ///   HONESTY NOTE: a registered plugin runs IN-PROCESS WITH FULL TRUST when invoked - exactly as
    ///   dangerous as the uploaded DLL it replaces. The win is that the source is visible,
    ///   contract-validated, gated (registration requires the dynamic-plugin capability), logged, and
    ///   per-namespace. It is NOT a sandbox.
    /// </remarks>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class PluginsController : ControllerBase
    {
        #region Data

        private readonly IFallen8 _fallen8;

        private readonly ILogger<PluginsController> _logger;

        /// <summary>
        ///   The compile bridge used to validate-and-compile at registration. Stateless; the same
        ///   implementation is registered on each engine for load-time rehydration.
        /// </summary>
        private static readonly PluginCompiler _compiler = new PluginCompiler();

        #endregion

        public PluginsController(ILogger<PluginsController> logger, IFallen8 fallen8)
        {
            _logger = logger;
            _fallen8 = fallen8;
        }

        #region registration

        /// <summary>
        /// Registers an algorithm plugin (Path / SubGraph / Analytics) from whole-type C# source.
        /// </summary>
        /// <param name="registration">The registration (name, contract, description, sourceCode)</param>
        /// <returns>A summary of the registered plugin</returns>
        /// <remarks>
        /// The source is compiled ONCE here and validated against the contract (exactly one public
        /// class implementing the contract's interface, with a public parameterless constructor and a
        /// PluginName equal to the registration name). A registered algorithm is then invoked by name
        /// through the existing path/subgraph/analytics endpoints. Entries are immutable: to change
        /// one, delete and re-register.
        ///
        /// SECURITY: registration compiles C# that later executes IN-PROCESS WITH FULL TRUST. It
        /// requires the dynamic-plugin capability (Fallen8:Security:EnableDynamicPluginLoading) - a
        /// provisioning window - plus authentication; invoking an already-registered plugin does not.
        /// </remarks>
        /// <response code="201">The plugin was compiled, validated and registered</response>
        /// <response code="400">The registration was malformed, or the source failed to compile / satisfy the contract (diagnostics in the body)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Dynamic plugin registration is disabled on this server</response>
        /// <response code="409">A plugin with the same name already exists, the name collides with a built-in, or the per-namespace quota was reached</response>
        /// <response code="413">The request body exceeds the code-endpoint size limit</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">The registration transaction faulted with an internal error</response>
        [HttpPost("/plugins/algorithm")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicPluginPolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PluginSummaryREST), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RegisterAlgorithm([FromBody] AlgorithmPluginRegistration registration)
        {
            if (registration == null)
            {
                return BadRequest("An algorithm plugin registration is required.");
            }

            if (!TryParseAlgorithmContract(registration.Contract, out var contract))
            {
                return BadRequest(String.Format(
                    "'{0}' is not a valid algorithm contract. Expected 'Path', 'SubGraph' or 'Analytics'.",
                    registration.Contract));
            }

            var definition = new PluginDefinition
            {
                Name = registration.Name,
                Category = PluginCategory.Algorithm,
                Contract = contract,
                SourceCode = registration.SourceCode,
                Description = registration.Description,
                CreatedAt = DateTime.UtcNow
            };

            return await RegisterAsync(definition);
        }

        /// <summary>
        /// Registers a graph function (a stored graph procedure) from whole-type C# source.
        /// </summary>
        /// <param name="registration">The registration (name, description, sourceCode)</param>
        /// <returns>A summary of the registered plugin</returns>
        /// <remarks>
        /// A graph function reads the whole graph (full scan or index query) and returns a view of
        /// existing vertices/edges. It is invoked by name via POST /plugins/function/{name}/invoke.
        /// Read-only in v1. Same compile/validate/gate contract as algorithm registration.
        /// </remarks>
        /// <response code="201">The function was compiled, validated and registered</response>
        /// <response code="400">The registration was malformed, or the source failed to compile / satisfy the contract</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Dynamic plugin registration is disabled on this server</response>
        /// <response code="409">A plugin with the same name already exists, or the per-namespace quota was reached</response>
        /// <response code="413">The request body exceeds the code-endpoint size limit</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">The registration transaction faulted with an internal error</response>
        [HttpPost("/plugins/function")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicPluginPolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PluginSummaryREST), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RegisterFunction([FromBody] FunctionPluginRegistration registration)
        {
            if (registration == null)
            {
                return BadRequest("A function plugin registration is required.");
            }

            var definition = new PluginDefinition
            {
                Name = registration.Name,
                Category = PluginCategory.Function,
                Contract = PluginContract.GraphFunction,
                SourceCode = registration.SourceCode,
                Description = registration.Description,
                CreatedAt = DateTime.UtcNow
            };

            return await RegisterAsync(definition);
        }

        #endregion

        #region validation (side-effect-free compile check for the editor)

        /// <summary>
        /// Compile-checks an algorithm plugin source WITHOUT registering it (for the authoring editor).
        /// </summary>
        /// <response code="200">The compile check ran; the body reports validity + diagnostics</response>
        /// <response code="400">The request was malformed (invalid contract)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Dynamic plugin registration is disabled on this server</response>
        [HttpPost("/plugins/algorithm/validate")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicPluginPolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PluginValidationREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult ValidateAlgorithm([FromBody] PluginValidationSpecification specification)
        {
            if (specification == null)
            {
                return BadRequest("A validation specification is required.");
            }

            if (!TryParseAlgorithmContract(specification.Contract, out var contract))
            {
                return BadRequest(String.Format(
                    "'{0}' is not a valid algorithm contract. Expected 'Path', 'SubGraph' or 'Analytics'.",
                    specification.Contract));
            }

            return Ok(Validate(specification, PluginCategory.Algorithm, contract));
        }

        /// <summary>
        /// Compile-checks a graph function source WITHOUT registering it (for the authoring editor).
        /// </summary>
        /// <response code="200">The compile check ran; the body reports validity + diagnostics</response>
        /// <response code="400">The request was malformed</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Dynamic plugin registration is disabled on this server</response>
        [HttpPost("/plugins/function/validate")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicPluginPolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PluginValidationREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult ValidateFunction([FromBody] PluginValidationSpecification specification)
        {
            if (specification == null)
            {
                return BadRequest("A validation specification is required.");
            }

            return Ok(Validate(specification, PluginCategory.Function, PluginContract.GraphFunction));
        }

        #endregion

        #region invocation

        /// <summary>
        /// Invokes a registered graph function by name against the addressed namespace's graph.
        /// </summary>
        /// <param name="name">The registered function name</param>
        /// <param name="invocation">The call-time parameters (string-valued in v1)</param>
        /// <returns>The selected vertices and edges</returns>
        /// <remarks>
        /// Invoking a registered function does NOT require the dynamic-plugin capability (only
        /// registration does) - it carries the standard authentication like any read. The function is
        /// activated fresh and runs read-only; the result references existing graph elements.
        /// </remarks>
        /// <response code="200">The function ran; returns the selected vertices/edges</response>
        /// <response code="400">The function reported an expected failure (e.g. a missing/invalid parameter)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No graph function with the given name is registered in this namespace</response>
        /// <response code="409">The function exists but is not in a runnable (Compiled) state</response>
        /// <response code="500">The function threw while running</response>
        [HttpPost("/plugins/function/{name}/invoke")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(GraphFunctionResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult InvokeFunction([FromRoute] String name, [FromBody] GraphFunctionInvocation invocation)
        {
            if (!_fallen8.Plugins.TryGet(out var entry, name) || entry.Definition.Category != PluginCategory.Function)
            {
                return NotFound(String.Format("No graph function named '{0}' is registered in this namespace.", name));
            }

            if (entry.CompileState != PluginCompileState.Compiled)
            {
                return Conflict(String.Format(
                    "Graph function '{0}' is not runnable (compile state: {1}).", name, entry.CompileState));
            }

            var parameters = invocation?.Parameters?.ToDictionary(kv => kv.Key, kv => (Object)kv.Value);

            try
            {
                if (!_fallen8.TryInvokeGraphFunction(out var result, name, parameters))
                {
                    // The function was resolvable and Compiled, so a false return is a function-side
                    // expected failure (per the Try* contract), not a "not found".
                    return BadRequest(String.Format(
                        "Graph function '{0}' reported a failure (check the parameters).", name));
                }

                return Ok(GraphFunctionResultREST.FromResult(result));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Graph function '{0}' threw while running.", name);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    String.Format("Graph function '{0}' threw while running.", name));
            }
        }

        #endregion

        #region list / get / delete

        /// <summary>
        /// Lists all registered plugins in the addressed namespace.
        /// </summary>
        /// <response code="200">Returns the plugin summaries</response>
        /// <response code="401">No valid credential was supplied</response>
        [HttpGet("/plugins")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<PluginSummaryREST>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult GetAllPlugins()
        {
            var summaries = _fallen8.Plugins.GetAll()
                .Select(PluginSummaryREST.FromEntry)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

            return Ok(summaries);
        }

        /// <summary>
        /// Gets the full definition of a registered plugin, including its source.
        /// </summary>
        /// <param name="name">The plugin name</param>
        /// <remarks>
        /// The response includes the stored source (which also covers manual migration between
        /// instances) and - for a Failed entry - the recompile diagnostics.
        /// </remarks>
        /// <response code="200">Returns the plugin detail</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No plugin with the given name exists in this namespace</response>
        [HttpGet("/plugins/{name}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(PluginDetailREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetPlugin([FromRoute] String name)
        {
            if (!_fallen8.Plugins.TryGet(out var entry, name))
            {
                return NotFound(String.Format("No plugin named '{0}'.", name));
            }

            return Ok(PluginDetailREST.FromEntryDetail(entry));
        }

        /// <summary>
        /// Deletes (deregisters) a plugin.
        /// </summary>
        /// <param name="name">The plugin name</param>
        /// <remarks>
        /// Deletion drops the pinned compiled type so its collectible load context can unload once
        /// in-flight invocations finish. Removal compiles nothing; it carries only the standard
        /// authentication (not the dynamic-plugin capability).
        /// </remarks>
        /// <response code="204">The plugin was deleted</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No plugin with the given name exists in this namespace</response>
        /// <response code="500">The removal transaction was rolled back and did not complete</response>
        [HttpDelete("/plugins/{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeletePlugin([FromRoute] String name)
        {
            if (!_fallen8.Plugins.TryGet(out _, name))
            {
                return NotFound(String.Format("No plugin named '{0}'.", name));
            }

            var tx = new RemovePluginTransaction { Name = name };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            await txInfo.Completion;

            if (txInfo.TransactionState == TransactionState.RolledBack)
            {
                if (txInfo.FailureReason == TransactionFailureReason.NotFound)
                {
                    return NotFound(String.Format("No plugin named '{0}'.", name));
                }

                return StatusCode(StatusCodes.Status500InternalServerError,
                    String.Format("The removal of plugin '{0}' was rolled back; the operation did not complete.", name));
            }

            return NoContent();
        }

        #endregion

        #region private helpers

        private async Task<IActionResult> RegisterAsync(PluginDefinition definition)
        {
            if (!PluginRegistry.IsValidName(definition.Name))
            {
                return BadRequest(String.Format(
                    "'{0}' is not a valid plugin name. Names must match ^[A-Za-z0-9_-]{{1,{1}}}$.",
                    definition.Name, PluginRegistry.MaxNameLength));
            }

            if (String.IsNullOrWhiteSpace(definition.SourceCode))
            {
                return BadRequest("Plugin source code is required.");
            }

            try
            {
                // Fail fast on the request thread (the transaction re-checks on the writer thread, so
                // a TOCTOU race still resolves correctly).
                if (_fallen8.Plugins.TryGet(out _, definition.Name))
                {
                    return Conflict(String.Format("A plugin named '{0}' already exists.", definition.Name));
                }

                if (CollidesWithBuiltIn(definition.Contract, definition.Name))
                {
                    return Conflict(String.Format(
                        "'{0}' is the name of a built-in {1} plugin; choose a different name.",
                        definition.Name, definition.Contract));
                }

                if (_fallen8.Plugins.Count >= _fallen8.Plugins.MaxCount)
                {
                    return Conflict(String.Format(
                        "The maximum number of plugins ({0}) has been reached.", _fallen8.Plugins.MaxCount));
                }

                // Compile + contract-validate BEFORE enqueueing: validation must fail fast with
                // diagnostics, and Roslyn must never occupy the single writer thread.
                if (!_compiler.TryCompile(definition, out var artifact, out var compileError))
                {
                    return BadRequest(compileError);
                }

                var entry = new PluginEntry(definition, PluginCompileState.Compiled, artifact);
                var tx = new RegisterPluginTransaction { Entry = entry };
                var txInfo = _fallen8.EnqueueTransaction(tx);
                await txInfo.Completion;

                if (txInfo.TransactionState == TransactionState.RolledBack)
                {
                    return MapFailedRegistration(txInfo, definition.Name);
                }

                return Created("/plugins/" + Uri.EscapeDataString(definition.Name), PluginSummaryREST.FromEntry(entry));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error registering plugin '{0}'", definition.Name);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    String.Format("An unexpected error occurred while registering plugin '{0}'.", definition.Name));
            }
        }

        private PluginValidationREST Validate(PluginValidationSpecification specification, PluginCategory category,
            PluginContract contract)
        {
            var definition = new PluginDefinition
            {
                Name = specification.Name,
                Category = category,
                Contract = contract,
                SourceCode = specification.SourceCode,
                CreatedAt = DateTime.UtcNow
            };

            // TryValidate (not TryCompile) so the validate path unloads its collectible load context
            // instead of orphaning one per compile-as-you-type check.
            var ok = _compiler.TryValidate(definition, out var error);
            return new PluginValidationREST { Valid = ok, Error = ok ? null : error };
        }

        private static bool TryParseAlgorithmContract(String value, out PluginContract contract)
        {
            // Strict literal names only (Enum.TryParse would also accept numeric strings, and
            // GraphFunction is not an algorithm contract).
            switch (value)
            {
                case "Path": contract = PluginContract.Path; return true;
                case "SubGraph": contract = PluginContract.SubGraph; return true;
                case "Analytics": contract = PluginContract.Analytics; return true;
                default: contract = default; return false;
            }
        }

        /// <summary>
        ///   Whether a name collides with a built-in plugin of the same category (§8 sub-decision):
        ///   rejected so resolution-order shadowing never surprises anyone. Graph functions have no
        ///   built-ins.
        /// </summary>
        private static bool CollidesWithBuiltIn(PluginContract contract, String name)
        {
            IEnumerable<String> names = null;
            switch (contract)
            {
                case PluginContract.Path:
                    PluginFactory.TryGetAvailablePlugins<IShortestPathAlgorithm>(out names);
                    break;
                case PluginContract.SubGraph:
                    PluginFactory.TryGetAvailablePlugins<ISubGraphAlgorithm>(out names);
                    break;
                case PluginContract.Analytics:
                    PluginFactory.TryGetAvailablePlugins<IGraphAnalyticsAlgorithm>(out names);
                    break;
                default:
                    return false;
            }

            return names != null && names.Contains(name, StringComparer.Ordinal);
        }

        private IActionResult MapFailedRegistration(TransactionInformation txInfo, String name)
        {
            switch (txInfo.FailureReason)
            {
                case TransactionFailureReason.InvalidInput:
                    return BadRequest(String.Format("The plugin '{0}' was structurally invalid.", name));

                case TransactionFailureReason.Conflict:
                    return Conflict(String.Format("A plugin named '{0}' already exists.", name));

                case TransactionFailureReason.QuotaExceeded:
                    return Conflict(String.Format(
                        "Registration of plugin '{0}' was rejected because the per-namespace quota was reached.", name));

                default:
                    if (txInfo.Error != null)
                    {
                        _logger?.LogError(txInfo.Error, "Registration of plugin '{0}' faulted and was rolled back.", name);
                    }
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        String.Format("Registration of plugin '{0}' failed due to an internal error.", name));
            }
        }

        #endregion
    }
}
