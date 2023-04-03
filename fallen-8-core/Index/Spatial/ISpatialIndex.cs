// MIT License
//
// ISpatialIndex.cs
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
//
// Author:
//       Andriy Kupershmidt <kuper133@googlemail.com>

using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Index.Spatial
{
    /// <summary>
    /// Fallen8 spatial index interface.
    /// </summary>
    public interface ISpatialIndex : IIndex
    {
        #region Distance
        /// <summary>
        /// find distance between two geometric objects
        /// </summary>
        /// <param name="geometry1">
        /// geomtry 1
        /// </param>
        /// <param name="geometry2">
        /// geometry 2
        /// </param>
        /// <returns>
        /// value of distance
        /// </returns>
        float Distance(IGeometry geometry1, IGeometry geometry2);

        /// <summary>
        /// find distance between two geometric elements of graph
        /// </summary>
        /// <param name="graphElement1">
        /// element og graph 1
        /// </param>
        /// <param name="graphElement2">
        /// element of graph 2
        /// </param>
        /// <returns>
        /// value of distance
        /// </returns>
        float Distance(AGraphElementModel graphElement1, AGraphElementModel graphElement2);
        #endregion

        #region SearchRegion
        /// <summary>
        /// find all objects that have at least one point in this region
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="minimalBoundedRechtangle">
        /// region(minimal bounded rechtangle)
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean SearchRegion(out ImmutableList<AGraphElementModel> result, IMBR minimalBoundedRechtangle);
        #endregion

        #region Overlap
        /// <summary>
        /// find all objects that have at least one point in common with this geometry
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="geometry">
        /// geometric object
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Overlap(out ImmutableList<AGraphElementModel> result, IGeometry geometry);

        /// <summary>
        /// find all objects that have at least one point in common with this element of graph
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Overlap(out ImmutableList<AGraphElementModel> result, AGraphElementModel graphElement);
        #endregion

        #region Enclosure
        /// <summary>
        /// find all objects (if they exist), which this element of graph included.
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Enclosure(out ImmutableList<AGraphElementModel> result, AGraphElementModel graphElement);

        /// <summary>
        /// find all objects (if they exist), which this geometry included.
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="geometry">
        /// geomtry
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Enclosure(out ImmutableList<AGraphElementModel> result, IGeometry geometry);
        #endregion

        #region Containment

        /// <summary>
        /// find all objects, which this geometry contains. 
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="geometry">
        /// geometry
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Containment(out ImmutableList<AGraphElementModel> result, IGeometry geometry);

        /// <summary>
        /// find all objects, which this element of graph contains. 
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean Containment(out ImmutableList<AGraphElementModel> result, AGraphElementModel graphElement);
        #endregion

        #region GetAllNeighbors
        /// <summary>
        /// find all neighbors for this element of graph
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean GetAllNeighbors(out ImmutableList<AGraphElementModel> result, AGraphElementModel graphElement);

        /// <summary>
        /// find all neighbors for this geometry
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="geometry">
        /// geometry
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean GetAllNeighbors(out ImmutableList<AGraphElementModel> result, IGeometry geometry);
        #endregion

        #region GetNextNeighbors
        /// <summary>
        /// find k next neighbors for this element of graph
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <param name="countOfNextNeighbors">
        /// count of neighbors
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean GetNextNeighbors(out ImmutableList<AGraphElementModel> result, AGraphElementModel graphElement, int countOfNextNeighbors);
        /// <summary>
        /// find k next neighbors for this geometry
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="geometry">
        /// geometry
        /// </param>
        /// <param name="countOfNextNeighbors">
        /// count of neighbors
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean GetNextNeighbors(out ImmutableList<AGraphElementModel> result, IGeometry geometry, int countOfNextNeighbors);
        #endregion

        #region SearchDistance
        /// <summary>
        /// find all object which distance less or equal d from this element of graph have
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="distance">
        /// value of distance for serching
        /// </param>
        /// <param name="graphElement">
        /// element of graph
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean SearchDistance(out ImmutableList<AGraphElementModel> result,
            float distance,
            AGraphElementModel graphElement);

        /// <summary>
        /// find all object which distance less or equal d from this geometry have
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="distance">
        /// value of distance for serching
        /// </param>
        /// <param name="geometry">
        /// geometry
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean SearchDistance(out ImmutableList<AGraphElementModel> result,
            float distance,
            IGeometry geometry);
        #endregion

        #region SearchPoint
        /// <summary>
        /// find all objects which this point have
        /// </summary>
        /// <param name="result">
        /// result
        /// </param>
        /// <param name="point">
        /// point
        /// </param>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        Boolean SearchPoint(out ImmutableList<AGraphElementModel> result, IPoint point);
        #endregion
    }
}
