// MIT License
//
// EmbeddingProviderProblem.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using Microsoft.AspNetCore.Http;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   The single home for mapping an embedding-provider fault to an HTTP (status, title): an
    ///   unavailable backend (<see cref="EmbeddingProviderUnavailableException"/>) to 503, and a
    ///   contract-violating output (<see cref="EmbeddingProviderOutputException"/>) to 502 - the
    ///   contract the exception types' own XML docs declare. Every surface that catches these two
    ///   faults (the embedding endpoints, semantic traversal on <c>/path</c> and <c>/subgraph</c>,
    ///   and fused document search) resolves the pair here, so they can never drift; each caller
    ///   still wraps the pair in its own carrier (an <c>ObjectResult</c> problem+json, or a
    ///   <c>SearchOutcome</c>) with the exception message as the detail.
    /// </summary>
    internal static class EmbeddingProviderProblem
    {
        internal static (Int32 status, String title) Map(Exception ex)
        {
            return ex is EmbeddingProviderUnavailableException
                ? (StatusCodes.Status503ServiceUnavailable, "Embedding provider unavailable")
                : (StatusCodes.Status502BadGateway, "Embedding backend produced invalid output");
        }
    }
}
