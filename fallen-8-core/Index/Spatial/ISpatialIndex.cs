using System;
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
        float Distance(AGraphElement graphElement1, AGraphElement graphElement2);
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
        Boolean SearchRegion(out ReadOnlyCollection<AGraphElement> result, IMBR minimalBoundedRechtangle);
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
        Boolean Overlap(out ReadOnlyCollection<AGraphElement> result, IGeometry geometry);

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
        Boolean Overlap(out ReadOnlyCollection<AGraphElement> result, AGraphElement graphElement);
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
        Boolean Enclosure(out ReadOnlyCollection<AGraphElement> result, AGraphElement graphElement);

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
        Boolean Enclosure(out ReadOnlyCollection<AGraphElement> result, IGeometry geometry);
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
        Boolean Containment(out ReadOnlyCollection<AGraphElement> result, IGeometry geometry);

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
        Boolean Containment(out ReadOnlyCollection<AGraphElement> result, AGraphElement graphElement);
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
        Boolean GetAllNeighbors(out ReadOnlyCollection<AGraphElement> result, AGraphElement graphElement);

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
        Boolean GetAllNeighbors(out ReadOnlyCollection<AGraphElement> result, IGeometry geometry);
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
        Boolean GetNextNeighbors(out ReadOnlyCollection<AGraphElement> result, AGraphElement graphElement, int countOfNextNeighbors);
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
        Boolean GetNextNeighbors(out ReadOnlyCollection<AGraphElement> result, IGeometry geometry, int countOfNextNeighbors);
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
        Boolean SearchDistance(out ReadOnlyCollection<AGraphElement> result,
            float distance,
            AGraphElement graphElement);

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
        Boolean SearchDistance(out ReadOnlyCollection<AGraphElement> result,
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
        Boolean SearchPoint(out ReadOnlyCollection<AGraphElement> result, IPoint point);
        #endregion
    }
}
