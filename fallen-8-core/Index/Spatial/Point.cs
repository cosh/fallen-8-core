// 
// Point.cs
//  
// Author:
//       Henning Rauch <Henning@RauchEntwicklung.biz>
// 
// Copyright (c) 2011-2015 Henning Rauch
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#region Usings

using System.Collections.Generic;
using NNoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.SpatialContainer;

#endregion

namespace NoSQL.GraphDB.Core.Index.Spatial
{
    /// <summary>
    /// Point.
    /// </summary>
    public sealed class Point : IPoint
    {

        #region data

        /// <summary>
		/// Gets or sets the longitude.
		/// </summary>
		/// <value>
		/// The longitude.
		/// </value>
        public float Longitude { get; private set; }

        /// <summary>
        /// Gets or sets the latitude.
        /// </summary>
        /// <value>
        /// The latitude.
        /// </value>
        public float Latitude { get; private set; }

        #endregion

        #region constructor

        public Point(float longitude, float latitude)
        {
            Longitude = longitude;
            Latitude = latitude;
        }

        #endregion

        public IMBR GeometryToMBR()
        {
            var lower = new[] { Longitude, Latitude };
            var upper = new[] { Longitude, Latitude };

            return new MBR(lower, upper);
        }

        public List<IDimension> Dimensions
        {
            get { return new List<IDimension> { new RealDimension(), new RealDimension() }; }
        }

        public IEnumerable<object> Coordinates
        {
            get { return new[] { new[] { Longitude, Latitude } }; }
        }

        public float[] PointToSpaceR()
        {
            return new[] { Longitude, Latitude };
        }
    }
}

