// MIT License
//
// GraphController.GraphElement.cs
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
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Controllers
{
    public partial class GraphController
    {
        /// <summary>
        /// Retrieves a graph element (vertex or edge) by its identifier
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element to retrieve</param>
        /// <returns>The graph element object if found, null otherwise</returns>
        /// <response code="200">Returns the graph element object</response>
        /// <response code="204">Graph element with the specified ID was not found</response>
        [HttpGet("/graphelement/{graphElementIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(AGraphElement), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public AGraphElement GetGraphElement([FromRoute] Int32 graphElementIdentifier)
        {
            AGraphElementModel ge;
            if (_fallen8.TryGetGraphElement(out ge, graphElementIdentifier))
            {
                if (ge is VertexModel vertex)
                {
                    return new Vertex(vertex);
                }

                if (ge is EdgeModel edge)
                {
                    return new Edge(edge);
                }
            }

            return null;
        }

        /// <summary>
        /// Retrieves the complete graph data including vertices and edges
        /// </summary>
        /// <param name="maxElements">Maximum number of elements to return (default: 1000)</param>
        /// <returns>A graph object containing lists of vertices and edges</returns>
        /// <response code="200">Returns the graph data with vertices and edges</response>
        [HttpGet("/graph")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Graph), StatusCodes.Status200OK)]
        public Graph GetGraph([FromQuery] int maxElements = 1000)
        {
            // Bounded read (feature api-error-contract E6): clamp to [0, MaxPageSize] so a single
            // request cannot materialize the whole graph (the old Take(int.MaxValue) DoS), and a
            // negative maxElements yields an empty page instead of the old silent Take(negative).
            var take = Math.Clamp(maxElements, 0, MaxPageSize);

            var result = new Graph();

            var edges = _fallen8.GetAllEdges().Take(take);
            result.Edges = edges.Select(_ => new Edge(_)).ToList();

            var vertices = _fallen8.GetAllVertices().Take(take);
            result.Vertices = vertices.Select(_ => new Vertex(_)).ToList();

            return result;
        }

        /// <summary>
        /// Sets (or replaces) a named embedding on a graph element
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="embeddingName">The embedding name (letters, digits, '_', '-'; max 64 chars)</param>
        /// <param name="definition">The embedding vector</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// The element is the source of truth for its embedding (feature element-embeddings):
        /// the write is WAL-durable element state, and every vector index BOUND to this
        /// embedding name updates its projection on commit - no separate index add. Replace
        /// semantics: one current vector per name.
        ///
        /// Sample request:
        ///
        ///     PUT /graphelement/42/embedding/default
        ///     { "vector": [0.12, -0.5, 0.33] }
        /// </remarks>
        /// <response code="202">Embedding write accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Invalid embedding name, missing/empty vector, non-finite components, a dimension conflicting with a vector index bound to this name, or a zero-norm vector while a bound Cosine index exists</response>
        /// <response code="404">The graph element does not exist</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpPut("/graphelement/{graphElementIdentifier}/embedding/{embeddingName}")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SetElementEmbedding([FromRoute] int graphElementIdentifier,
            [FromRoute] string embeddingName, [FromBody] EmbeddingWriteSpecification definition,
            [FromQuery] bool waitForCompletion = false)
        {
            if (!AGraphElementModel.IsValidEmbeddingName(embeddingName))
            {
                return BadRequest(String.Format("'{0}' is not a valid embedding name.", embeddingName));
            }

            if (definition?.Vector == null || definition.Vector.Length == 0)
            {
                return BadRequest("An embedding vector is required.");
            }

            var vector = definition.Vector;
            if (vector.Length > VectorIndex.MaxDimension)
            {
                return BadRequest(String.Format("The vector exceeds the maximum dimension of {0}.", VectorIndex.MaxDimension));
            }

            if (VectorIndex.HasNonFiniteComponent(vector))
            {
                return BadRequest("The vector contains NaN or Infinity components.");
            }

            if (!_fallen8.TryGetGraphElement(out _, graphElementIdentifier))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", graphElementIdentifier));
            }

            // A write that can never project into an index BOUND to this name is rejected up
            // front (the engine-side projection would only silent-skip + log, family contract).
            foreach (var namedIndex in _fallen8.IndexFactory.GetNamedIndicesSnapshot())
            {
                if (!(namedIndex.Value is IVectorIndex vectorIndex) ||
                    !String.Equals(vectorIndex.EmbeddingName, embeddingName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (vector.Length != vectorIndex.Dimension)
                {
                    return BadRequest(String.Format(
                        "The vector has dimension {0}, but a bound vector index requires {1} for embedding '{2}'.",
                        vector.Length, vectorIndex.Dimension, embeddingName));
                }

                if (vectorIndex.Metric == VectorDistanceMetric.Cosine && VectorIndex.IsZeroNorm(vector))
                {
                    return BadRequest(String.Format(
                        "A zero-norm vector cannot rank in the Cosine vector index bound to embedding '{0}'.", embeddingName));
                }
            }

            return await AwaitAndAccept(
                _fallen8.EnqueueTransaction(
                    new SetEmbeddingsTransaction().SetEmbedding(graphElementIdentifier, embeddingName, vector)),
                waitForCompletion);
        }

        /// <summary>
        /// Removes a named embedding from a graph element
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="embeddingName">The embedding name</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Bound vector indices purge the element's projection on commit. Removing an absent
        /// embedding is a committed no-op, matching the property surface.
        /// </remarks>
        /// <response code="202">Embedding removal accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Invalid embedding name</response>
        /// <response code="404">The graph element does not exist</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}/embedding/{embeddingName}")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RemoveElementEmbedding([FromRoute] int graphElementIdentifier,
            [FromRoute] string embeddingName, [FromQuery] bool waitForCompletion = false)
        {
            if (!AGraphElementModel.IsValidEmbeddingName(embeddingName))
            {
                return BadRequest(String.Format("'{0}' is not a valid embedding name.", embeddingName));
            }

            if (!_fallen8.TryGetGraphElement(out _, graphElementIdentifier))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", graphElementIdentifier));
            }

            return await AwaitAndAccept(
                _fallen8.EnqueueTransaction(
                    new SetEmbeddingsTransaction().SetEmbedding(graphElementIdentifier, embeddingName, null)),
                waitForCompletion);
        }

        /// <summary>
        /// Gets a named embedding of a graph element
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="embeddingName">The embedding name</param>
        /// <returns>The stored vector plus the provider model stamp, when one exists</returns>
        /// <response code="200">The stored embedding</response>
        /// <response code="400">Invalid embedding name</response>
        /// <response code="404">The graph element does not exist or carries no embedding of that name</response>
        [HttpGet("/graphelement/{graphElementIdentifier}/embedding/{embeddingName}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ElementEmbeddingREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<ElementEmbeddingREST> GetElementEmbedding([FromRoute] int graphElementIdentifier,
            [FromRoute] string embeddingName)
        {
            if (!AGraphElementModel.IsValidEmbeddingName(embeddingName))
            {
                return BadRequest(String.Format("'{0}' is not a valid embedding name.", embeddingName));
            }

            if (!_fallen8.TryGetGraphElement(out var element, graphElementIdentifier))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", graphElementIdentifier));
            }

            if (!element.TryGetEmbedding(out var vector, embeddingName))
            {
                return NotFound(String.Format("Element {0} carries no embedding '{1}'.",
                    graphElementIdentifier, embeddingName));
            }

            element.TryGetEmbeddingModelStamp(out var model, embeddingName);

            return new ElementEmbeddingREST
            {
                Name = embeddingName,
                Vector = vector.ToArray(),
                Model = model
            };
        }

        /// <summary>
        /// Adds or updates a property on a graph element (vertex or edge)
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="propertyIdString">The ID/key of the property</param>
        /// <param name="definition">Property value specification</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /graphelement/123/age
        ///     {
        ///        "propertyValue": 35,
        ///        "fullQualifiedTypeName": "System.Int32"
        ///     }
        /// </remarks>
        /// <response code="202">Property addition accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request body / invalid property specification. (A non-existent graph element is NOT a 400: an out-of-range id rolls back with an internal error → 500, and an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpPut("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddProperty([FromRoute] int graphElementIdentifier, [FromRoute] string propertyIdString, [FromBody] PropertySpecification definition, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer graphElementIdentifier now fails route binding -> 400 ProblemDetails
            // (feature api-error-contract E2), instead of Convert.ToInt32 throwing FormatException -> 500.
            if (definition == null)
            {
                return BadRequest("A property specification body is required.");
            }

            var graphElementId = graphElementIdentifier;
            var propertyId = propertyIdString;

            // Guarded type resolution (E3): an unknown type name is a 400, not a thrown TypeLoadException.
            if (!TryResolveType(definition.FullQualifiedTypeName, out var targetType))
            {
                return BadRequest(String.Format("Unknown type name '{0}'.", definition.FullQualifiedTypeName));
            }

            object property;
            try
            {
                // Invariant parse of the wire value (feature property-ingestion-culture; ingest
                // home ServiceHelper.CreateObject).
                property = targetType == null
                    ? definition.PropertyValue
                    : Convert.ChangeType(definition.PropertyValue, targetType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                return BadRequest(String.Format("The property value could not be converted to '{0}': {1}",
                    definition.FullQualifiedTypeName, ex.Message));
            }

            // Definition must be constructed here: the nested-initializer form assigns into
            // the property's default value, which is null -> NullReferenceException.
            AddPropertyTransaction tx = new AddPropertyTransaction()
            {
                Definition = new PropertyAddDefinition()
                {
                    GraphElementId = graphElementId,
                    PropertyId = propertyId,
                    Property = property
                }
            };

            return await AwaitAndAccept(_fallen8.EnqueueTransaction(tx), waitForCompletion);

        }

        /// <summary>
        /// Removes a property from a graph element (vertex or edge)
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="propertyIdString">The ID/key of the property to remove</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123/age
        /// </remarks>
        /// <response code="202">Property removal accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request (e.g. a non-integer element id). (A non-existent element/property is NOT a 400: an out-of-range id → 500, an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TryRemoveProperty([FromRoute] int graphElementIdentifier, [FromRoute] string propertyIdString, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer id fails route binding -> 400 (feature api-error-contract E2).
            var graphElementId = graphElementIdentifier;
            var propertyId = propertyIdString;

            RemovePropertyTransaction tx = new RemovePropertyTransaction()
            {
                GraphElementId = graphElementId,
                PropertyId = propertyId
            };

            return await AwaitAndAccept(_fallen8.EnqueueTransaction(tx), waitForCompletion);

        }

        /// <summary>
        /// Removes a graph element (vertex or edge) from the graph
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element to remove</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123
        /// </remarks>
        /// <response code="202">Graph element removal accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request (e.g. a non-integer element id). (A non-existent graph element is NOT a 400: an out-of-range id → 500, an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TryRemoveGraphElement([FromRoute] int graphElementIdentifier, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer id fails route binding -> 400 (feature api-error-contract E2).
            var graphElementId = graphElementIdentifier;

            RemoveGraphElementTransaction tx = new RemoveGraphElementTransaction()
            {
                GraphElementId = graphElementId
            };

            return await AwaitAndAccept(_fallen8.EnqueueTransaction(tx), waitForCompletion);
        }
    }
}
