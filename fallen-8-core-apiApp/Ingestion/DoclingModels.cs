// MIT License
//
// DoclingModels.cs
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
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   The deliberately MINIMAL subset of docling-serve's convert response and the
    ///   DoclingDocument this feature consumes (spec unstructured-ingestion, Unverified
    ///   "DoclingDocument schema surface"): reading order (<c>body</c>/<c>groups</c> children
    ///   refs), text items with label/level/provenance, table grids, and the page map for the
    ///   page cap. Everything else in the schema is ignored on purpose; fixture documents in
    ///   the test suite pin this subset.
    /// </summary>
    public sealed class DoclingConvertResponse
    {
        [JsonPropertyName("document")]
        public DoclingConvertDocument Document
        {
            get; set;
        }

        [JsonPropertyName("status")]
        public String Status
        {
            get; set;
        }
    }

    /// <summary>The async task API's submit/poll payload (feature semantic-layer): docling-serve
    /// returns a task id and a status the poll loop watches (pending/started/success/failure).</summary>
    public sealed class DoclingTaskStatus
    {
        [JsonPropertyName("task_id")]
        public String TaskId
        {
            get; set;
        }

        [JsonPropertyName("task_status")]
        public String TaskStatus
        {
            get; set;
        }
    }

    public sealed class DoclingConvertDocument
    {
        [JsonPropertyName("md_content")]
        public String MdContent
        {
            get; set;
        }

        [JsonPropertyName("json_content")]
        public DoclingDocumentModel JsonContent
        {
            get; set;
        }
    }

    public sealed class DoclingDocumentModel
    {
        [JsonPropertyName("texts")]
        public List<DoclingTextItem> Texts
        {
            get; set;
        }

        [JsonPropertyName("tables")]
        public List<DoclingTableItem> Tables
        {
            get; set;
        }

        [JsonPropertyName("groups")]
        public List<DoclingGroupItem> Groups
        {
            get; set;
        }

        [JsonPropertyName("body")]
        public DoclingNodeItem Body
        {
            get; set;
        }

        /// <summary>Keyed by page number; only the COUNT is consumed (the page cap).</summary>
        [JsonPropertyName("pages")]
        public Dictionary<String, JsonElement> Pages
        {
            get; set;
        }
    }

    public sealed class DoclingTextItem
    {
        [JsonPropertyName("self_ref")]
        public String SelfRef
        {
            get; set;
        }

        /// <summary>docling item label, e.g. <c>title</c>, <c>section_header</c>, <c>text</c>,
        /// <c>list_item</c>, <c>code</c>.</summary>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }

        [JsonPropertyName("text")]
        public String Text
        {
            get; set;
        }

        /// <summary>Heading level (section headers only).</summary>
        [JsonPropertyName("level")]
        public Int32? Level
        {
            get; set;
        }

        [JsonPropertyName("prov")]
        public List<DoclingProvenance> Prov
        {
            get; set;
        }
    }

    public sealed class DoclingTableItem
    {
        [JsonPropertyName("self_ref")]
        public String SelfRef
        {
            get; set;
        }

        [JsonPropertyName("prov")]
        public List<DoclingProvenance> Prov
        {
            get; set;
        }

        [JsonPropertyName("data")]
        public DoclingTableData Data
        {
            get; set;
        }
    }

    public sealed class DoclingTableData
    {
        [JsonPropertyName("grid")]
        public List<List<DoclingTableCell>> Grid
        {
            get; set;
        }
    }

    public sealed class DoclingTableCell
    {
        [JsonPropertyName("text")]
        public String Text
        {
            get; set;
        }
    }

    public sealed class DoclingGroupItem
    {
        [JsonPropertyName("self_ref")]
        public String SelfRef
        {
            get; set;
        }

        [JsonPropertyName("children")]
        public List<DoclingRef> Children
        {
            get; set;
        }
    }

    public sealed class DoclingNodeItem
    {
        [JsonPropertyName("children")]
        public List<DoclingRef> Children
        {
            get; set;
        }
    }

    public sealed class DoclingRef
    {
        [JsonPropertyName("$ref")]
        public String Ref
        {
            get; set;
        }
    }

    public sealed class DoclingProvenance
    {
        [JsonPropertyName("page_no")]
        public Int32? PageNo
        {
            get; set;
        }
    }

    /// <summary>What the converter hands the pipeline: the markdown, the structured document
    /// when the sidecar returned one, and the page count when the document knows it.</summary>
    public sealed class DoclingConversionResult
    {
        public String Markdown
        {
            get; set;
        }

        public DoclingDocumentModel Document
        {
            get; set;
        }

        public Int32? PageCount
        {
            get; set;
        }
    }

    /// <summary>The sidecar is not configured, not reachable, timed out, or answered
    /// non-success - mapped to 503 at the REST boundary (binary formats only; text formats
    /// never reach the converter).</summary>
    public sealed class DoclingUnavailableException : Exception
    {
        public DoclingUnavailableException(String message) : base(message)
        {
        }

        public DoclingUnavailableException(String message, Exception inner) : base(message, inner)
        {
        }
    }
}
