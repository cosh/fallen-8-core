// MIT License
//
// GraphController.cs
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
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Controllers.Cache;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Transaction;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [UnconditionalSuppressMessage("Trimming", "IL2096:Call to 'System.Type.GetType' can perform case insensitive lookup of the type", Justification = "Type names are provided by API users and need case-insensitive lookup. Trimming is disabled for this application.")]
    public partial class GraphController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        private readonly ILogger<GraphController> _logger;

        private readonly GeneratedCodeCache _cache;

        /// <summary>Upper bound on how many elements a single page read (<see cref="GetGraph"/>) returns,
        /// so a request cannot materialize the whole graph (feature api-error-contract E6).</summary>
        private const int MaxPageSize = 100_000;

        #endregion

        /// <summary>
        ///   Resolves a caller-supplied fully-qualified type name for value conversion. A null/empty
        ///   name means "use the raw value" (<paramref name="type"/> is <c>null</c>, returns
        ///   <c>true</c>); an ALLOW-LISTED primitive name returns its <see cref="Type"/>; any other name
        ///   returns <c>false</c> so the caller answers 400 (feature api-error-contract E3). Resolution
        ///   goes through <see cref="AllowedLiteralTypes"/>, NEVER <c>Type.GetType(userString)</c>, so an
        ///   attacker-controlled name cannot force-load an assembly or run a static ctor (feature
        ///   dynamic-code-resource-limits R3).
        /// </summary>
        private static bool TryResolveType(string fullQualifiedTypeName, out Type type)
        {
            if (string.IsNullOrEmpty(fullQualifiedTypeName))
            {
                type = null;
                return true;
            }

            return AllowedLiteralTypes.TryResolve(fullQualifiedTypeName, out type);
        }

        /// <summary>
        ///   The authorization service used for the request-shape-aware dynamic-code capability
        ///   check on <see cref="CalculateShortestPath"/> (feature stored-query-library). Null when
        ///   the controller is constructed directly (unit tests) - the hosted pipeline always
        ///   supplies it, and the pipeline-level matrix tests pin the real gate behaviour.
        /// </summary>
        private readonly IAuthorizationService _authorizationService;

        /// <summary>
        ///   The embedding provider resolving <c>semantic.queryText</c> (feature
        ///   embedding-provider). Null when constructed directly (unit tests) or before the
        ///   feature's DI registration - queryText then answers 503.
        /// </summary>
        private readonly Embedding.Fallen8EmbeddingProvider _embeddingProvider;

        public GraphController(ILogger<GraphController> logger, IFallen8 fallen8,
            IAuthorizationService authorizationService = null,
            Embedding.Fallen8EmbeddingProvider embeddingProvider = null)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _cache = new GeneratedCodeCache();

            _authorizationService = authorizationService;

            _embeddingProvider = embeddingProvider;
        }

        /// <summary>
        ///   Whether a path request INTRODUCES code: any non-blank inline filter/cost fragment.
        ///   Only such a request requires the dynamic-code capability (feature
        ///   stored-query-library); a storedQuery reference or a fragment-less request compiles no
        ///   user-supplied code.
        /// </summary>
        private static bool CarriesInlineCode(PathSpecification definition)
        {
            return !String.IsNullOrWhiteSpace(definition.Filter?.Vertex) ||
                   !String.IsNullOrWhiteSpace(definition.Filter?.Edge) ||
                   !String.IsNullOrWhiteSpace(definition.Filter?.EdgeProperty) ||
                   !String.IsNullOrWhiteSpace(definition.Cost?.Vertex) ||
                   !String.IsNullOrWhiteSpace(definition.Cost?.Edge);
        }

        /// <summary>
        ///   Shared epilogue for the write endpoints. The caller enqueues; when it asked to wait, we
        ///   await the outcome and surface a rolled-back write as its failure result rather than a
        ///   false success (the worker rolls a faulting transaction back - correctness-fixes B6),
        ///   otherwise the write is 202 Accepted. Single home for that wait-and-map logic.
        /// </summary>
        private async Task<IActionResult> AwaitAndAccept(TransactionInformation transaction, bool waitForCompletion)
        {
            if (waitForCompletion)
            {
                await transaction.Completion;

                if (transaction.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transaction.FailureReason);
                }
            }

            return Accepted();
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        #region IGraphService implementation

        /// <summary>
        ///   Converts a <see cref="LiteralSpecification"/> to an <see cref="IComparable"/> for a scan,
        ///   returning <c>false</c> with a client-facing <paramref name="error"/> for an unknown type
        ///   name or an unconvertible value (feature api-error-contract E3) instead of throwing.
        /// </summary>
        private static bool TryConvertLiteral(LiteralSpecification literal, out IComparable value, out string error)
        {
            value = null;
            error = null;

            if (!TryResolveType(literal.FullQualifiedTypeName, out var targetType))
            {
                error = String.Format("Unknown type name '{0}'.", literal.FullQualifiedTypeName);
                return false;
            }

            try
            {
                // Invariant parse of the wire literal (feature property-ingestion-culture; the
                // ingest home is ServiceHelper.CreateObject): a comma-decimal host must not read
                // "0.8" as 8.
                value = targetType == null
                    ? literal.Value
                    : (IComparable)Convert.ChangeType(literal.Value, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                error = String.Format("The literal value could not be converted to '{0}': {1}",
                    literal.FullQualifiedTypeName, ex.Message);
                return false;
            }
        }

        #endregion

        #region private helper

        /// <summary>
        ///   Builds the error result returned when a waited-on mutation transaction was rolled back,
        ///   mapping the structured <see cref="TransactionFailureReason"/> to the appropriate HTTP
        ///   status: a client-caused rollback surfaces as a 4xx, an internal fault as a 500.
        /// </summary>
        private IActionResult RolledBackResult(TransactionFailureReason reason)
        {
            switch (reason)
            {
                case TransactionFailureReason.InvalidInput:
                    return StatusCode(StatusCodes.Status400BadRequest,
                        "The transaction was rolled back: the request was invalid.");

                case TransactionFailureReason.NotFound:
                    return StatusCode(StatusCodes.Status404NotFound,
                        "The transaction was rolled back: a referenced graph element does not exist.");

                case TransactionFailureReason.QuotaExceeded:
                case TransactionFailureReason.Conflict:
                    return StatusCode(StatusCodes.Status409Conflict,
                        "The transaction was rolled back: the request conflicts with the current state or a resource quota.");

                default:
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        "The transaction was rolled back; the operation did not complete.");
            }
        }

        /// <summary>
        ///   Creats the result
        /// </summary>
        /// <param name="graphElements"> The graph elements </param>
        /// <param name="resultTypeSpecification"> The result specification </param>
        /// <returns> </returns>
        private static IEnumerable<int> CreateResult(IEnumerable<AGraphElementModel> graphElements,
                                                    ResultTypeSpecification resultTypeSpecification)
        {
            switch (resultTypeSpecification)
            {
                case ResultTypeSpecification.Vertices:
                    return graphElements.OfType<VertexModel>().Select(_ => _.Id);

                case ResultTypeSpecification.Edges:
                    return graphElements.OfType<EdgeModel>().Select(_ => _.Id);

                case ResultTypeSpecification.Both:
                    return graphElements.Select(_ => _.Id);

                default:
                    throw new ArgumentOutOfRangeException("resultTypeSpecification");
            }
        }

        #endregion

        #region not implemented

        [NonAction]
        public void Save(SerializationWriter writer)
        {
        }

        [NonAction]
        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
