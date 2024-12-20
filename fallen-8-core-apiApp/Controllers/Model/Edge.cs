﻿// MIT License
//
// EdgeSpecification.cs
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

using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The edge
    /// </summary>
    public class Edge : AGraphElement
    {
        /// <summary>
        ///   The target vertex.
        /// </summary>
        [Required]
        public int TargetVertex
        {
            get; set;
        }

        /// <summary>
        ///   The source vertex.
        /// </summary>
        [Required]
        public int SourceVertex
        {
            get; set;
        }

        public Edge(EdgeModel edge) : base(edge.Id, edge.CreationDate, edge.ModificationDate, edge.Label, edge.GetAllProperties())
        {
            TargetVertex = edge.TargetVertex.Id;
            
            SourceVertex = edge.SourceVertex.Id;
        }
    }
}