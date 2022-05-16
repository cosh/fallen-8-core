// MIT License
//
// VertexModel.cs
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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using NoSQL.GraphDB.Core.Error;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   Vertex model.
    /// </summary>
    public sealed class VertexModel : AGraphElement
    {
        #region Data

        /// <summary>
        ///   The out edges.
        /// </summary>
        internal ImmutableDictionary<String, ImmutableList<EdgeModel>> _outEdges;

        /// <summary>
        ///   The in edges.
        /// </summary>
        internal ImmutableDictionary<String, ImmutableList<EdgeModel>> _inEdges;

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the <see cref="VertexModel" /> class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        public VertexModel(Int32 id, UInt32 creationDate, String label = null, Dictionary<String, Object> properties = null)
            : base(id, creationDate, label, properties)
        {
        }

        /// <summary>
        ///   Initializes a new instance of the VertexModel class. For internal usage only
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='modificationDate'> Modification date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        /// <param name='outEdges'> Out edges. </param>
        /// <param name='incEdges'> Inc edges. </param>
        internal VertexModel(Int32 id, UInt32 creationDate, UInt32 modificationDate, String label = null,
                             Dictionary<String, Object> properties = null, Dictionary<String, List<EdgeModel>> outEdges = null, Dictionary<String, List<EdgeModel>> incEdges = null)
            : base(id, creationDate, label, properties)
        {
            if (outEdges != null)
            {
                _outEdges = outEdges.ToImmutableDictionary(_ => _.Key, __ => __.Value.ToImmutableList());
            }

            if (incEdges != null)
            {
                _inEdges = incEdges.ToImmutableDictionary(_ => _.Key, __ => __.Value.ToImmutableList());
            }

            ModificationDate = modificationDate;
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Adds the out edge.
        /// </summary>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        /// <param name='outEdge'> Out edge. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void AddOutEdge(String edgePropertyId, EdgeModel outEdge)
        {
            if (_outEdges == null)
            {
                _outEdges = new Dictionary<String, ImmutableList<EdgeModel>> () { { edgePropertyId, ImmutableList.Create<EdgeModel>( outEdge ) } }.ToImmutableDictionary();

                return;
            }

            ImmutableList<EdgeModel> edgePropertyFound;
            if (_outEdges.TryGetValue(edgePropertyId, out edgePropertyFound))
            {
                edgePropertyFound = edgePropertyFound.Add(outEdge);
            }
            else
            {
                //not yet found
                edgePropertyFound = new List<EdgeModel> () { outEdge }.ToImmutableList();
            }
            
            _outEdges = _outEdges.SetItem(edgePropertyId, edgePropertyFound);
        }

        /// <summary>
        ///   Adds the out edges.
        /// </summary>
        /// <param name='outEdges'> Out edges. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void SetOutEdges(Dictionary<String, List<EdgeModel>> outEdges)
        {
            _outEdges = outEdges.ToImmutableDictionary(_ => _.Key, __ => __.Value.ToImmutableList());
        }

        /// <summary>
        ///   Adds the incoming edge.
        /// </summary>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        /// <param name='incomingEdge'> Incoming edge. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void AddIncomingEdge(String edgePropertyId, EdgeModel incomingEdge)
        {
            if (_inEdges == null)
            {
                _inEdges = new Dictionary<String, ImmutableList<EdgeModel>>() { { edgePropertyId, ImmutableList.Create<EdgeModel>(incomingEdge) } }.ToImmutableDictionary();

                return;
            }

            ImmutableList<EdgeModel> edgePropertyFound;
            if (_inEdges.TryGetValue(edgePropertyId, out edgePropertyFound))
            {
                edgePropertyFound = edgePropertyFound.Add(incomingEdge);
            }
            else
            {
                //not yet found
                edgePropertyFound = new List<EdgeModel>() { incomingEdge }.ToImmutableList();
            }

            _inEdges = _inEdges.SetItem(edgePropertyId, edgePropertyFound);
        }

        /// <summary>
        ///   Gets the incoming edges.
        /// </summary>
        /// <returns> The incoming edges. </returns>
        internal ImmutableDictionary<String, ImmutableList<EdgeModel>> GetIncomingEdges()
        {
            return _inEdges;
        }

        /// <summary>
        ///   Gets the outgoing edges.
        /// </summary>
        /// <returns> The outgoing edges. </returns>
        internal ImmutableDictionary<String, ImmutableList<EdgeModel>> GetOutgoingEdges()
        {
            return _outEdges;
        }

        /// <summary>
        ///   Removes an incoming edge
        /// </summary>
        /// <param name="edgePropertyId"> Edge property identifier. </param>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        internal void RemoveIncomingEdge(String edgePropertyId, EdgeModel toBeRemovedEdge)
        {
            if (_inEdges == null)
            {
                return;
            }

            ImmutableList<EdgeModel> edgePropertyFound;
            if (_inEdges.TryGetValue(edgePropertyId, out edgePropertyFound))
            {
                edgePropertyFound = edgePropertyFound.RemoveAll(_ => _.Id == toBeRemovedEdge.Id);
                _inEdges = _inEdges.SetItem(edgePropertyId, edgePropertyFound);
            }
        }

        /// <summary>
        ///   Removes an incoming edge
        /// </summary>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        /// <returns> The edge property identifier where the edge was deleted </returns>
        internal List<String> RemoveIncomingEdge(EdgeModel toBeRemovedEdge)
        {
            var result = new List<String>();

            if (_inEdges == null)
            {
                return result;
            }

            var tempDict = new Dictionary<string, ImmutableList<EdgeModel>>();

            foreach (var aEdgeProperty in _inEdges)
            {
                if (aEdgeProperty.Value.Contains(toBeRemovedEdge))
                {
                    tempDict.Add(aEdgeProperty.Key, aEdgeProperty.Value.RemoveAll(_ => _.Id == toBeRemovedEdge.Id));
                }
            }

            _inEdges = _inEdges.SetItems(tempDict);

            return tempDict.Select(_ => _.Key).ToList();
        }

        /// <summary>
        ///   Remove outgoing edge
        /// </summary>
        /// <param name="edgePropertyId"> The edge property identifier. </param>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        internal void RemoveOutGoingEdge(String edgePropertyId, EdgeModel toBeRemovedEdge)
        {
            if (_outEdges == null)
            {
                return;
            }

            ImmutableList<EdgeModel> edgePropertyFound;
            if (_outEdges.TryGetValue(edgePropertyId, out edgePropertyFound))
            {
                edgePropertyFound = edgePropertyFound.RemoveAll(_ => _.Id == toBeRemovedEdge.Id);
                _outEdges = _outEdges.SetItem(edgePropertyId, edgePropertyFound);
            }
        }

        /// <summary>
        ///   Removes an outgoing edge
        /// </summary>
        /// <param name="toBeRemovedEdge"> The to be removed edge </param>
        /// <returns> The edge property identifier where the edge was deleted </returns>
        internal List<String> RemoveOutGoingEdge(EdgeModel toBeRemovedEdge)
        {
            var result = new List<String>();

            if (_outEdges == null)
            {
                return result;
            }

            var tempDict = new Dictionary<string, ImmutableList<EdgeModel>>();

            foreach (var aEdgeProperty in _outEdges)
            {
                if (aEdgeProperty.Value.Contains(toBeRemovedEdge))
                {
                    tempDict.Add(aEdgeProperty.Key, aEdgeProperty.Value.RemoveAll(_ => _.Id == toBeRemovedEdge.Id));
                }
            }

            _outEdges = _outEdges.SetItems(tempDict);

            return tempDict.Select(_ => _.Key).ToList();
        }

        #endregion

        #region IVertexModel implementation

        public uint GetInDegree()
        {
            UInt32 degree = 0;

            if (_inEdges != null)
            {
                degree = Convert.ToUInt32(_inEdges.Sum(_ => _.Value.Count));
            }
            return degree;
        }

        public uint GetOutDegree()
        {
            UInt32 degree = 0;

            if (_outEdges != null)
            {
                degree = Convert.ToUInt32(_outEdges.Sum(_ => _.Value.Count));
            }
            return degree;
        }

        /// <summary>
        ///   Gets all neighbors.
        /// </summary>
        /// <returns> The neighbors. </returns>
        public List<VertexModel> GetAllNeighbors()
        {
            var neighbors = new List<VertexModel>();

            if (_outEdges != null)
            {
                neighbors.AddRange(_outEdges.SelectMany(_ => _.Value).Select(TargetVertexExtractor));
            }

            if (_inEdges != null)
            {
                neighbors.AddRange(_inEdges.SelectMany(_ => _.Value).Select(TargetVertexExtractor));
            }

            return neighbors;
        }

        /// <summary>
        ///   Gets the incoming edge identifiers.
        /// </summary>
        /// <returns> The incoming edge identifiers. </returns>
        public List<String> GetIncomingEdgeIds()
        {
            var inEdges = new List<String>();

            if (_inEdges != null)
            {
                inEdges.AddRange(_inEdges.Select(_ => _.Key));
            }
            return inEdges;
        }

        /// <summary>
        ///   Gets the outgoing edge identifiers.
        /// </summary>
        /// <returns> The outgoing edge identifiers. </returns>
        public List<String> GetOutgoingEdgeIds()
        {

            var outEdges = new List<String>();

            if (_outEdges != null)
            {
                outEdges.AddRange(_outEdges.Select(_ => _.Key));
            }
            return outEdges;
        }

        /// <summary>
        ///   Tries to get an out edge.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        public Boolean TryGetOutEdge(out ImmutableList<EdgeModel> result, String edgePropertyId)
        {
            result = null;

            if (_outEdges != null)
            {
                return _outEdges.TryGetValue(edgePropertyId, out result);
            }

            return false;
        }

        /// <summary>
        ///   Tries to get in edges.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='edgePropertyId'> Edge property identifier. </param>
        public Boolean TryGetInEdge(out ImmutableList<EdgeModel> result, String edgePropertyId)
        {
            result = null;

            if (_inEdges != null)
            {
                return _inEdges.TryGetValue(edgePropertyId, out result);
            }

            return false;
        }

        #endregion

        #region AGraphElement

        /// <summary>
        ///   The overide of the trim method
        /// </summary>
        internal override void Trim()
        {
            //NOP
        }

        #endregion

        #region Equals Overrides

        public override Boolean Equals(Object obj)
        {
            // If parameter is null return false.
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to PathElement return false.
            var p = obj as VertexModel;

            return p != null && Equals(p);
        }

        public Boolean Equals(VertexModel p)
        {
            // If parameter is null return false:
            return (object)p != null && ReferenceEquals(this, p);
        }

        public static Boolean operator ==(VertexModel a, VertexModel b)
        {
            // If both are null, or both are same instance, return true.
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            // Return true if the fields match:
            return a.Equals(b);
        }

        public static Boolean operator !=(VertexModel a, VertexModel b)
        {
            return !(a == b);
        }

        public override int GetHashCode()
        {
            return Id;
        }

        #endregion

        #region misc overrides

        public override string ToString()
        {
            return Id.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region private helper

        /// <summary>
        /// Target vertex extractor.
        /// </summary>
        /// <returns>
        /// The target vertex.
        /// </returns>
        /// <param name='edge'>
        /// Edge.
        /// </param>
        private static VertexModel TargetVertexExtractor(EdgeModel edge)
        {
            return edge.TargetVertex;
        }

        /// <summary>
        /// Source vertex extractor.
        /// </summary>
        /// <returns>
        /// The source vertex.
        /// </returns>
        /// <param name='edge'>
        /// Edge.
        /// </param>
        private static VertexModel SourceVertexExtractor(EdgeModel edge)
        {
            return edge.SourceVertex;
        }

        #endregion
    }
}
