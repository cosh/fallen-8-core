// MIT License
//
// IDoclingConverter.cs
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
using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   Binary-to-markdown/structured conversion behind the docling-serve sidecar (feature
    ///   unstructured-ingestion). One implementation; the seam exists for the ingestion
    ///   pipeline's failure-injection tests.
    /// </summary>
    public interface IDoclingConverter
    {
        /// <summary>Whether an endpoint is configured at all (text formats never need one).</summary>
        Boolean Configured
        {
            get;
        }

        /// <summary>Converts one document. Throws <see cref="DoclingUnavailableException"/> when
        /// the sidecar is unconfigured, unreachable, times out or answers non-success.</summary>
        Task<DoclingConversionResult> ConvertAsync(Byte[] fileBytes, String fileName, CancellationToken cancellationToken);

        /// <summary>A cached, short-TTL health probe for the /status block - never a
        /// per-request conversion cost.</summary>
        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }
}
