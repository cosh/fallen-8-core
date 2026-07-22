// MIT License
//
// GraphController.Index.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Controllers
{
    public partial class GraphController
    {
        /// <summary>
        /// Adds (or replaces) an element's embedding vector in a vector index
        /// </summary>
        /// <param name="indexId">The ID of the vector index</param>
        /// <param name="definition">The element and its vector - explicit ("vector") or read from a float[] property ("propertyId")</param>
        /// <returns>True when the vector was indexed</returns>
        /// <remarks>
        /// One vector per element: adding again replaces. The generic PUT /index/{indexId} add
        /// path cannot express a float[] key, which is why the vector family has this typed
        /// endpoint (like fulltext and spatial have theirs).
        ///
        /// Sample request (explicit mode):
        ///
        ///     PUT /index/vector/myEmbeddings
        ///     {
        ///        "graphElementId": 42,
        ///        "vector": [0.12, -0.5, 0.33]
        ///     }
        ///
        /// Sample request (property mode - reads the element's float[] property):
        ///
        ///     PUT /index/vector/myEmbeddings
        ///     {
        ///        "graphElementId": 42,
        ///        "propertyId": "embedding"
        ///     }
        /// </remarks>
        /// <response code="200">The vector was indexed (add-again replaced the previous vector)</response>
        /// <response code="400">Not a vector index, neither/both modes supplied, wrong dimension, NaN/Infinity components, zero-norm vector under Cosine, or the named property is missing / not a float[]</response>
        /// <response code="404">The index or the graph element does not exist</response>
        [HttpPut("/index/vector/{indexId}")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult AddToVectorIndex([FromRoute] String indexId, [FromBody] VectorIndexAddSpecification definition)
        {
            if (definition == null)
            {
                return BadRequest("A vector add specification is required.");
            }

            if (!_fallen8.IndexFactory.TryGetIndex(out var index, indexId))
            {
                return NotFound(String.Format("No index named '{0}'.", indexId));
            }

            if (!(index is IVectorIndex vectorIndex))
            {
                return BadRequest(String.Format("Index '{0}' is not a vector index.", indexId));
            }

            // A BOUND index is a derived projection of the named element embedding (feature
            // element-embeddings): membership is declared at creation, the writer thread keeps
            // it, explicit adds would create a second membership authority.
            if (vectorIndex.EmbeddingName != null)
            {
                return BadRequest(String.Format(
                    "Index '{0}' is bound to embedding '{1}' and maintains itself; write the element embedding instead of adding to the index.",
                    indexId, vectorIndex.EmbeddingName));
            }

            if (!_fallen8.TryGetGraphElement(out var element, definition.GraphElementId))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", definition.GraphElementId));
            }

            var hasVector = definition.Vector != null;
            var hasProperty = !String.IsNullOrEmpty(definition.PropertyId);
            if (hasVector == hasProperty)
            {
                return BadRequest("Exactly one of 'vector' / 'propertyId' must be supplied.");
            }

            Single[] vector;
            if (hasVector)
            {
                vector = definition.Vector;
            }
            else
            {
                if (!element.TryGetProperty<Object>(out var propertyValue, definition.PropertyId))
                {
                    return BadRequest(String.Format("Element {0} carries no property '{1}'.",
                        definition.GraphElementId, definition.PropertyId));
                }

                vector = propertyValue as Single[];
                if (vector == null)
                {
                    return BadRequest(String.Format("Property '{0}' on element {1} is not a float[].",
                        definition.PropertyId, definition.GraphElementId));
                }
            }

            if (vector.Length != vectorIndex.Dimension)
            {
                return BadRequest(String.Format("The vector has dimension {0}; index '{1}' requires {2}.",
                    vector.Length, indexId, vectorIndex.Dimension));
            }

            if (VectorIndex.HasNonFiniteComponent(vector))
            {
                return BadRequest("The vector contains NaN or Infinity components; only finite values can rank.");
            }

            if (vectorIndex.Metric == VectorDistanceMetric.Cosine && VectorIndex.IsZeroNorm(vector))
            {
                return BadRequest("A zero-norm vector cannot rank under the Cosine metric.");
            }

            vectorIndex.AddOrUpdate(vector, element);
            return Ok(true);
        }

        /// <summary>
        /// Creates a new index for the graph
        /// </summary>
        /// <param name="definition">Plugin specification with index type and configuration options</param>
        /// <returns>True if the index was successfully created, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /index
        ///     {
        ///        "uniqueId": "nameIndex",
        ///        "pluginType": "DictionaryIndex",
        ///        "pluginOptions": {
        ///           "propertyId": "name",
        ///           "type": "System.String"
        ///        }
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the index was created successfully</response>
        /// <response code="400">Invalid index specification</response>
        [HttpPost("/index")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool CreateIndex([FromBody] PluginSpecification definition)
        {
            //TODO: return IIndex object representation
            IIndex result;
            return _fallen8.IndexFactory.TryCreateIndex(out result, definition.UniqueId, definition.PluginType, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        /// <summary>
        /// Adds a graph element to an existing index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="definition">Specification containing graph element ID and key information</param>
        /// <returns>True if the element was successfully added to the index, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /index/nameIndex
        ///     {
        ///        "graphElementId": 123,
        ///        "key": {
        ///          "propertyValue": "John Smith",
        ///          "fullQualifiedTypeName": "System.String"
        ///        }
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the element was successfully added to the index</response>
        /// <response code="400">Invalid specification, index not found, or graph element not found</response>
        [HttpPut("/index/{indexId}")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool AddToIndex([FromRoute] String indexId, [FromBody] IndexAddToSpecification definition)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElementModel graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
                {
                    idx.AddOrUpdate(ServiceHelper.CreateObject(definition.Key), graphElement);
                    return true;
                }

                _logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
                return false;
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Removes a key from an index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="property">The property specification representing the key to remove</param>
        /// <returns>True if the key was successfully removed, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex/propertyValue
        ///     {
        ///        "propertyValue": "John Smith",
        ///        "fullQualifiedTypeName": "System.String"
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the key was successfully removed</response>
        /// <response code="400">Invalid property specification or index not found</response>
        [HttpDelete("/index/{indexId}/propertyValue")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool RemoveKeyFromIndex([FromRoute] String indexId, [FromBody] PropertySpecification property)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                return idx.TryRemoveKey(ServiceHelper.CreateObject(property));
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Removes a graph element from an index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="graphElementId">The ID of the graph element to remove</param>
        /// <returns>True if the graph element was successfully removed from the index, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex/123
        /// </remarks>
        /// <response code="200">Returns true if the element was successfully removed from the index</response>
        /// <response code="404">Index not found or graph element not found</response>
        [HttpDelete("/index/{indexId}/{graphElementId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public bool RemoveGraphElementFromIndex([FromRoute] String indexId, [FromRoute] Int32 graphElementId)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElementModel graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, graphElementId))
                {
                    idx.RemoveValue(graphElement);
                    return true;
                }

                _logger.LogError(String.Format("Could not find graph element {0}.", graphElementId));
                return false;
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Deletes an index from the system
        /// </summary>
        /// <param name="indexId">The ID of the index to delete</param>
        /// <returns>True if the index was successfully deleted, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex
        /// </remarks>
        /// <response code="200">Returns true if the index was successfully deleted</response>
        /// <response code="404">Index not found</response>
        [HttpDelete("/index/{indexId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public bool DeleteIndex([FromRoute] String indexId)
        {
            return _fallen8.IndexFactory.TryDeleteIndex(indexId);
        }
    }
}
