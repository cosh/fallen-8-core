// MIT License
//
// GraphController.Scan.cs
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
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Controllers
{
    public partial class GraphController
    {
        /// <summary>
        /// Scans the graph for elements with a specific property value
        /// </summary>
        /// <param name="propertyId">The property ID to scan for</param>
        /// <param name="definition">Scan specification with comparison operator and value</param>
        /// <returns>A collection of graph element IDs matching the criteria</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/graph/property/name
        ///     {
        ///        "operator": "Equal",
        ///        "literal": {
        ///          "value": "John Doe",
        ///          "fullQualifiedTypeName": "System.String"
        ///        },
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs</response>
        /// <response code="400">Invalid scan specification</response>
        [HttpPost("/scan/graph/property/{propertyId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<IEnumerable<int>> GraphScan([FromRoute] String propertyId, [FromBody] ScanSpecification definition)
        {
            // A malformed scan spec (missing body/literal, unknown type name, or an unconvertible value)
            // is a client error -> 400, not a thrown exception -> 500 (feature api-error-contract E3).
            if (definition == null || definition.Literal == null)
            {
                return BadRequest("A scan specification with a literal is required.");
            }

            if (!TryConvertLiteral(definition.Literal, out var value, out var error))
            {
                return BadRequest(error);
            }

            List<AGraphElementModel> graphElements;
            return _fallen8.GraphScan(out graphElements, propertyId, value, definition.Operator)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
        }

        /// <summary>
        /// Performs a scan operation on an index with a specific value and operator
        /// </summary>
        /// <param name="definition">Index scan specification with index ID, operator and value</param>
        /// <returns>A collection of graph element IDs matching the criteria</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/all
        ///     {
        ///        "indexId": "userNameIndex",
        ///        "operator": "Equal",
        ///        "literal": {
        ///          "value": "Jane",
        ///          "fullQualifiedTypeName": "System.String"
        ///        },
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs</response>
        /// <response code="400">Invalid scan specification or index not found</response>
        [HttpPost("/scan/index/all")]
        [Produces("application/json")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<IEnumerable<int>> IndexScan([FromBody] IndexScanSpecification definition)
        {
            if (definition == null || definition.Literal == null)
            {
                return BadRequest("An index scan specification with a literal is required.");
            }

            if (!TryConvertLiteral(definition.Literal, out var value, out var error))
            {
                return BadRequest(error);
            }

            IReadOnlyList<AGraphElementModel> graphElements;
            return _fallen8.IndexScan(out graphElements, definition.IndexId, value, definition.Operator)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
        }

        /// <summary>
        /// Performs a range-based scan on an index between two values
        /// </summary>
        /// <param name="definition">Range scan specification with index ID, limits and include/exclude options</param>
        /// <returns>A collection of graph element IDs within the specified range</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/range
        ///     {
        ///        "indexId": "ageIndex",
        ///        "leftLimit": 18,
        ///        "rightLimit": 30,
        ///        "includeLeft": true,
        ///        "includeRight": false,
        ///        "fullQualifiedTypeName": "System.Int32",
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs within the range</response>
        /// <response code="400">Invalid range specification or index not found</response>
        [HttpPost("/scan/index/range")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<IEnumerable<int>> RangeIndexScan([FromBody] RangeIndexScanSpecification definition)
        {
            if (definition == null)
            {
                return BadRequest("A range scan specification is required.");
            }

            // Guarded type resolution + conversion of both limits (feature api-error-contract E3).
            if (!TryResolveType(definition.FullQualifiedTypeName, out var limitType))
            {
                return BadRequest(String.Format("Unknown type name '{0}'.", definition.FullQualifiedTypeName));
            }

            IComparable left, right;
            try
            {
                // Invariant parse of the wire limits (feature property-ingestion-culture; ingest
                // home ServiceHelper.CreateObject).
                left = (IComparable)Convert.ChangeType(definition.LeftLimit, limitType ?? typeof(string), CultureInfo.InvariantCulture);
                right = (IComparable)Convert.ChangeType(definition.RightLimit, limitType ?? typeof(string), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                return BadRequest(String.Format("A range limit could not be converted to '{0}': {1}",
                    definition.FullQualifiedTypeName, ex.Message));
            }

            IReadOnlyList<AGraphElementModel> graphElements;
            return _fallen8.RangeIndexScan(out graphElements, definition.IndexId, left, right, definition.IncludeLeft,
                                           definition.IncludeRight)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
        }

        /// <summary>
        /// Performs a fulltext search on an indexed property
        /// </summary>
        /// <param name="definition">Fulltext search specification with index ID and search terms</param>
        /// <returns>A result object containing matched elements and highlighting information</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/fulltext
        ///     {
        ///        "indexId": "documentIndex",
        ///        "requestString": "graph database nosql"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the search results with highlighting</response>
        /// <response code="400">Invalid search specification or index not found</response>
        /// <response code="404">Index not found or is not a fulltext index</response>
        [HttpPost("/scan/index/fulltext")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(FulltextSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public FulltextSearchResultREST FulltextIndexScan([FromBody] FulltextIndexScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            FulltextSearchResult result;
            return _fallen8.FulltextIndexScan(out result, definition.IndexId, definition.RequestString)
                       ? new FulltextSearchResultREST(result)
                       : null;
        }

        /// <summary>
        /// Finds the k nearest neighbours of a query vector in a vector index
        /// </summary>
        /// <param name="definition">The kNN query: index, query vector, k, optional kind/label constraints</param>
        /// <returns>The hits best-first with raw scores, plus the metric and its direction</returns>
        /// <remarks>
        /// Exact brute-force kNN (SIMD): deterministic ordering - best score first, ties broken
        /// by ascending element id. Constraints are applied BEFORE scoring, so the returned k are
        /// k MATCHING elements. Removed elements never appear. The GraphRAG recipe: feed the
        /// returned element ids into the existing traversal surface (POST /path, PUT /subgraph,
        /// property reads) - similarity search lands ON the graph.
        ///
        /// Sample request:
        ///
        ///     POST /scan/index/vector
        ///     {
        ///        "indexId": "myEmbeddings",
        ///        "query": [0.1, 0.2, 0.3],
        ///        "k": 10,
        ///        "kind": "vertex",
        ///        "label": "person"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the k best-scoring matching elements (fewer when the corpus is smaller)</response>
        /// <response code="400">Not a vector index, wrong query dimension, NaN/Infinity components, k outside [1, 1024], zero-norm query under Cosine, or an unknown kind value</response>
        /// <response code="404">The index does not exist</response>
        [HttpPost("/scan/index/vector")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(VectorSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult VectorIndexScan([FromBody] VectorIndexScanSpecification definition)
        {
            if (definition == null || definition.Query == null)
            {
                return BadRequest("A vector scan specification with a query vector is required.");
            }

            if (!_fallen8.IndexFactory.TryGetIndex(out var index, definition.IndexId))
            {
                return NotFound(String.Format("No index named '{0}'.", definition.IndexId));
            }

            if (!(index is IVectorIndex vectorIndex))
            {
                return BadRequest(String.Format("Index '{0}' is not a vector index.", definition.IndexId));
            }

            if (!NoSQL.GraphDB.App.Helper.VectorSearchConstraintBuilder.TryBuild(
                    definition.Kind, definition.Label, out var constraint, out var constraintError))
            {
                return BadRequest(constraintError);
            }

            if (!vectorIndex.TryNearestNeighbors(out var result, definition.Query, definition.K, constraint))
            {
                return BadRequest(String.Format(
                    "Invalid kNN query: the query must have dimension {0} with finite components, k must be within [1, {1}], and a Cosine query must not be zero-norm.",
                    vectorIndex.Dimension, VectorIndex.MaxK));
            }

            var results = new List<VectorScoredElementREST>(result.Entries.Count);
            foreach (var entry in result.Entries)
            {
                results.Add(new VectorScoredElementREST { GraphElementId = entry.Element.Id, Score = entry.Score });
            }

            return Ok(new VectorSearchResultREST
            {
                Metric = result.Metric.ToString(),
                HigherIsBetter = result.HigherIsBetter,
                Results = results
            });
        }

        /// <summary>
        /// Performs a spatial distance search using a spatial index
        /// </summary>
        /// <param name="definition">Spatial search specification with index ID, reference element and distance</param>
        /// <returns>A collection of graph element IDs within the specified distance</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/spatial
        ///     {
        ///        "indexId": "locationIndex",
        ///        "graphElementId": 123,
        ///        "distance": 5.0
        ///     }
        /// </remarks>
        /// <response code="200">Returns the element IDs within the specified distance</response>
        /// <response code="400">Invalid search specification</response>
        /// <response code="404">Index not found, is not a spatial index, or reference element not found</response>
        [HttpPost("/scan/index/spatial")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IEnumerable<int> SpatialIndexScanSearchDistance([FromBody] SearchDistanceSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            AGraphElementModel graphElement;
            if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
            {
                IIndex idx;
                if (_fallen8.IndexFactory.TryGetIndex(out idx, definition.IndexId))
                {
                    var spatialIndex = idx as ISpatialIndex;
                    if (spatialIndex != null)
                    {
                        ImmutableList<AGraphElementModel> result;
                        return spatialIndex.SearchDistance(out result, definition.Distance, graphElement)
                            ? result.Select(_ => _.Id)
                            : null;
                    }
                    _logger.LogError(String.Format("The index with id {0} is no spatial index.", definition.IndexId));
                    return null;
                }
                _logger.LogError(String.Format("Could not find index {0}.", definition.IndexId));
                return null;
            }
            _logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
            return null;
        }
    }
}
