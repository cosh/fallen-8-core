// MIT License
//
// IMetric.cs
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
//
// Author:
//       Andriy Kupershmidt <kuper133@googlemail.com>

using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Index.Spatial
{
    /// <summary>
    ///IMetric is the metric for n-dimensional real space.
    /// </summary>
    public interface IMetric
    {
        /// <summary>
        /// It is the function for calculation of distance between two points
        /// </summary>
        /// <param name="myPoint1">
        /// first point from real space
        /// </param>
        /// <param name="myPoint2">
        /// second point from real space
        /// </param>
        /// <returns>
        /// distance between two point-objects
        /// </returns>
        float Distance(IMBP myPoint1, IMBP myPoint2);
        /// <summary>
        /// transformation for all axis to find minimal bounded rechtangle
        /// </summary>
        /// <param name="distance">
        /// distance
        /// </param>
        /// <param name="mbr">
        /// minimal bounded rectangel
        /// </param>
        /// <returns>
        /// distance for all axis
        /// </returns>
        float[] TransformationOfDistance(float distance, IMBR mbr);

        /// <summary>
        /// Persists this metric's configuration STATE so a serialized index (e.g. the R-Tree, which
        /// records the metric by type name) can reconstruct a functionally identical metric on load -
        /// not merely a default-constructed one. The default is a no-op: a STATELESS metric (such as
        /// <see cref="Implementation.Metric.EuclidianMetric"/>) has nothing to persist and reconstructs
        /// fully from its type alone. A STATEFUL metric (such as
        /// <see cref="Implementation.Metric.GeoMetric"/>, which carries an earth radius) overrides this
        /// to write its fields, and <see cref="RestoreState"/> to read them back symmetrically.
        /// </summary>
        /// <param name="writer">The serialization writer to append the metric state to.</param>
        void SaveState(SerializationWriter writer)
        {
        }

        /// <summary>
        /// Restores the configuration state written by <see cref="SaveState"/>, in the same order and
        /// byte layout. The default is a no-op (a stateless metric writes and reads nothing). An
        /// implementation MUST read exactly what its <see cref="SaveState"/> wrote.
        /// </summary>
        /// <param name="reader">The serialization reader positioned at this metric's state.</param>
        void RestoreState(SerializationReader reader)
        {
        }
    }
}
