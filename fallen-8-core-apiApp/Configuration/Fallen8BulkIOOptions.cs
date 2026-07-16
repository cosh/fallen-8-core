// MIT License
//
// Fallen8BulkIOOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Bulk import/export configuration, bound from the <c>Fallen8:BulkIO</c> section
    ///   (feature bulk-import-export). Non-positive values reset to the defaults.
    /// </summary>
    public sealed class Fallen8BulkIOOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:BulkIO";

        private Int32 _importBatchSize = 10_000;
        private Int32 _maxLineBytes = 1024 * 1024;

        /// <summary>
        ///   Elements per import batch = one CreateVertices/CreateEdges transaction = one WAL
        ///   entry + one group-commit fsync. Bounds import memory (batch definitions + created
        ///   models) independent of file size. Default 10 000.
        /// </summary>
        public Int32 ImportBatchSize
        {
            get { return _importBatchSize; }
            set { _importBatchSize = value > 0 ? value : 10_000; }
        }

        /// <summary>
        ///   Maximum bytes of one JSONL line (import). A longer line is a 400 with its line
        ///   number - one element carrying more than a MiB of properties is a modelling smell,
        ///   and the cap bounds per-line parse memory. Default 1 MiB.
        /// </summary>
        public Int32 MaxLineBytes
        {
            get { return _maxLineBytes; }
            set { _maxLineBytes = value > 0 ? value : 1024 * 1024; }
        }

        /// <summary>
        ///   Optional cap on the whole import request body (bytes); null = unlimited - the
        ///   endpoint exists precisely to carry whole-graph payloads and its memory is bounded
        ///   by construction (MaxLineBytes x parse + ImportBatchSize x definitions), so the body
        ///   cap is a disk/graph-size control, not a memory control. This is an EXPLICIT
        ///   per-endpoint carve-out from the api-security-boundary body limits; the sensitive
        ///   (code/plugin) 1 MiB limit is untouched.
        /// </summary>
        public Int64? MaxImportRequestBytes { get; set; }
    }
}
