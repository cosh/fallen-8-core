// MIT License
//
// IFallen8Admin.cs
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

using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;
using System;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   Fallen8 administrative interface for managing factories.
    /// </summary>
    public interface IFallen8Admin : IFallen8Write
    {
        /// <summary>
        ///   Gets the index factory.
        /// </summary>
        IndexFactory IndexFactory
        {
            get;
        }

        /// <summary>
        ///   Gets the service factory.
        /// </summary>
        ServiceFactory ServiceFactory
        {
            get;
        }

        /// <summary>
        ///   Gets the subgraph factory.
        /// </summary>
        SubGraphFactory SubGraphFactory
        {
            get;
        }

        /// <summary>
        ///   Gets or sets the compiler used to rebuild persisted subgraphs on load. When null,
        ///   persisted subgraphs are skipped. Set by the hosting layer that understands the
        ///   subgraph specification format (for example the REST API).
        /// </summary>
        ISubGraphRecipeCompiler SubGraphRecipeCompiler
        {
            get; set;
        }

        /// <summary>
        ///   Gets the stored query library (feature stored-query-library).
        /// </summary>
        StoredQueryLibrary StoredQueries
        {
            get;
        }

        /// <summary>
        ///   Gets the change feed, or null when the engine was constructed without one
        ///   (feature change-feed).
        /// </summary>
        ChangeFeed.ChangeFeedDispatcher ChangeFeed
        {
            get;
        }

        /// <summary>
        ///   Gets or sets the compiler used to (re)build stored query artifacts from their
        ///   persisted source. When null, rehydrated stored queries load as source-only. Set by
        ///   the hosting layer that understands the specification format (for example the REST
        ///   API) - the same bridge pattern as <see cref="SubGraphRecipeCompiler"/>.
        /// </summary>
        IStoredQueryCompiler StoredQueryCompiler
        {
            get; set;
        }

        ILoggerFactory LoggerFactory
        {
            get;
        }

        void SetId(Guid id);

        /// <summary>
        ///   Enables or disables the automatic post-removal tombstone reclamation and sets the
        ///   tombstone threshold that triggers it (feature trim-reader-safety). Auto-trim frees a
        ///   removed element's heavy body (properties + adjacency) WITHOUT reassigning any element id,
        ///   so element ids stay stable REST handles; it is OFF by default. Id renumbering / slot
        ///   compaction remains the job of the explicit <c>TrimTransaction</c>.
        /// </summary>
        void ConfigureAutoTrim(bool enabled, int tombstoneThreshold);
    }
}
