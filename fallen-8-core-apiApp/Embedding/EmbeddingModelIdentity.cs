// MIT License
//
// EmbeddingModelIdentity.cs
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
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   The model identity of the active embedding provider (feature embedding-provider,
    ///   FR-8): name, optional version, output dimension, intended metric. Its
    ///   <see cref="Stamp" /> string is what gets written next to provider-generated element
    ///   embeddings, declared on vector indices via the <c>model</c> option, and compared at
    ///   query time - a mismatch anywhere is a hard error, never silent coercion.
    /// </summary>
    public sealed class EmbeddingModelIdentity
    {
        public String Name
        {
            get;
        }

        /// <summary>Optional free-form version/quantization/revision; empty when unspecified.</summary>
        public String Version
        {
            get;
        }

        public Int32 Dimension
        {
            get;
        }

        public VectorDistanceMetric IntendedMetric
        {
            get;
        }

        /// <summary>The canonical identity string: <c>name[@version]#dimension#metric</c>.</summary>
        public String Stamp
        {
            get;
        }

        public EmbeddingModelIdentity(String name, String version, Int32 dimension, VectorDistanceMetric intendedMetric)
        {
            Name = name;
            Version = version ?? String.Empty;
            Dimension = dimension;
            IntendedMetric = intendedMetric;
            Stamp = String.IsNullOrEmpty(Version)
                ? String.Format("{0}#{1}#{2}", Name, Dimension, IntendedMetric)
                : String.Format("{0}@{1}#{2}#{3}", Name, Version, Dimension, IntendedMetric);
        }
    }

    /// <summary>The provider (or its backend) is not usable right now - configuration error,
    /// model load failure, or an unreachable sidecar. Maps to 503.</summary>
    public sealed class EmbeddingProviderUnavailableException : Exception
    {
        public EmbeddingProviderUnavailableException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>The backend produced output violating the index add contract (wrong dimension,
    /// non-finite components, zero-norm under Cosine). Maps to 502 - an upstream fault, never
    /// coerced.</summary>
    public sealed class EmbeddingProviderOutputException : Exception
    {
        public EmbeddingProviderOutputException(String message)
            : base(message)
        {
        }
    }
}
