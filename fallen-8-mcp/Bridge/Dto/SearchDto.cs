// MIT License
//
// SearchDto.cs
//
// Copyright (c) 2026 Henning Rauch
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

namespace NoSQL.GraphDB.Mcp.Bridge.Dto
{
    /// <summary>The literal a scan compares against (Fallen-8's typed-value wire pair).</summary>
    public sealed class LiteralDto
    {
        public String Value { get; set; } = String.Empty;

        public String FullQualifiedTypeName { get; set; } = "System.String";
    }

    /// <summary>Request body for <c>POST /scan/index/all</c>. <c>Operator</c> is the numeric
    /// <c>BinaryOperator</c> (Equals=0…NotEquals=5); <c>ResultType</c> is the string enum.</summary>
    public sealed class IndexScanRequest
    {
        public String IndexId { get; set; } = String.Empty;

        public Int32 Operator { get; set; }

        public LiteralDto Literal { get; set; } = new();

        public String ResultType { get; set; } = "Both";
    }

    /// <summary>Request body for <c>POST /scan/graph/property/{propertyId}</c> (un-indexed).</summary>
    public sealed class PropertyScanRequest
    {
        public Int32 Operator { get; set; }

        public LiteralDto Literal { get; set; } = new();

        public String ResultType { get; set; } = "Both";
    }

    /// <summary>Request body for <c>POST /scan/index/fulltext</c>.</summary>
    public sealed class FulltextScanRequest
    {
        public String IndexId { get; set; } = String.Empty;

        public String RequestString { get; set; } = String.Empty;
    }

    /// <summary>Request body for <c>POST /scan/index/vector</c> (kNN over a query vector).</summary>
    public sealed class VectorScanRequest
    {
        public String IndexId { get; set; } = String.Empty;

        public Single[] Query { get; set; } = Array.Empty<Single>();

        public Int32 K { get; set; }

        public String? Kind { get; set; }

        public String? Label { get; set; }
    }

    /// <summary>Request body for <c>POST /embedding/search</c> (text-in semantic kNN).</summary>
    public sealed class SemanticScanRequest
    {
        public String IndexId { get; set; } = String.Empty;

        public String Text { get; set; } = String.Empty;

        public Int32 K { get; set; }

        public String? Kind { get; set; }

        public String? Label { get; set; }
    }

    /// <summary>Response of <c>POST /scan/index/fulltext</c>.</summary>
    public sealed class FulltextResultDto
    {
        public Double MaximumScore { get; set; }

        public List<FulltextHitDto> Elements { get; set; } = new();
    }

    public sealed class FulltextHitDto
    {
        public Int32 GraphElementId { get; set; }

        public Double Score { get; set; }

        public List<String> Highlights { get; set; } = new();
    }

    /// <summary>Response of <c>POST /scan/index/vector</c> and <c>POST /embedding/search</c>.</summary>
    public sealed class VectorResultDto
    {
        public String Metric { get; set; } = String.Empty;

        public Boolean HigherIsBetter { get; set; }

        public List<VectorHitDto> Results { get; set; } = new();
    }

    public sealed class VectorHitDto
    {
        public Int32 GraphElementId { get; set; }

        public Single Score { get; set; }
    }
}
