// MIT License
//
// BulkController.cs
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
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Streaming bulk export/import of the graph as newline-delimited JSON
    ///   (<c>fallen8-jsonl</c> version 1, feature bulk-import-export).
    /// </summary>
    /// <remarks>
    ///   The file carries graph ELEMENTS only (vertices, edges, properties) - indices, subgraph
    ///   definitions and save-game metadata are re-created via their own endpoints. File ids are
    ///   references WITHIN the file; import always assigns fresh engine ids.
    /// </remarks>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class BulkController : ControllerBase
    {
        #region Data

        private readonly IFallen8 _fallen8;
        private readonly ILogger<BulkController> _logger;
        private readonly Fallen8BulkIOOptions _options;

        /// <summary>Lines buffered between response flushes on export.</summary>
        private const int ExportFlushEveryLines = 512;

        /// <summary>Byte threshold that also triggers an export flush - property-heavy elements
        /// must not grow the buffer unboundedly between the line-count flushes (the constant-
        /// memory claim is a byte bound, not a line bound).</summary>
        private const int ExportFlushBytes = 256 * 1024;

        #endregion

        public BulkController(ILogger<BulkController> logger, IFallen8 fallen8,
            IOptions<Fallen8BulkIOOptions> options = null)
        {
            _logger = logger;
            _fallen8 = fallen8;
            _options = options?.Value ?? new Fallen8BulkIOOptions();
        }

        #region export

        /// <summary>
        /// Streams the graph (or a label-filtered subset) as newline-delimited JSON.
        /// </summary>
        /// <param name="vertexLabel">Optional: export only vertices with exactly this label</param>
        /// <param name="edgeLabel">Optional: export only edges with exactly this label</param>
        /// <remarks>
        /// The stream is fallen8-jsonl version 1: one meta line (format version + exact counts),
        /// then vertex lines, then edge lines. Edges whose endpoints are not both in the exported
        /// vertex set are omitted, so EVERY exported file is internally consistent and importable
        /// by construction.
        ///
        /// CONSISTENCY (honest): this is data interchange, not a crash-consistent backup. Reads
        /// are lock-free; a write committed during the export may or may not appear. The
        /// guarantee is internal consistency plus "everything committed before the export began
        /// is present" (subject to the label filters). For a point-in-time backup, quiesce writes
        /// or use the save-game machinery. The same contract covers a property that an
        /// embedded/plugin writer adds DURING the stream: if it is not exportable it is omitted
        /// from its element rather than aborting the response (the REST write path cannot create
        /// such properties; the pre-stream 422 covers everything present at capture).
        /// </remarks>
        /// <response code="200">The NDJSON stream (application/x-ndjson)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="422">An element carries a property outside the exportable type
        /// allow-list (or a null value); the body names the element and property. Sent BEFORE any
        /// streaming, so a failed export is never a half-written file</response>
        [HttpGet("/bulk/export")]
        [Produces("application/x-ndjson")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> Export([FromQuery] String vertexLabel = null, [FromQuery] String edgeLabel = null)
        {
            // Two back-to-back point-in-time projections of the lock-free snapshot; the engine's
            // own read surface, not a JSON string.
            var vertices = _fallen8.GetAllVertices(vertexLabel);
            var edges = _fallen8.GetAllEdges(edgeLabel);

            var vertexIds = new HashSet<Int32>(vertices.Count);
            foreach (var vertex in vertices)
            {
                vertexIds.Add(vertex.Id);
            }

            // Pre-stream validation: every property must be exportable, BEFORE the 200 status
            // line goes out - a failed export is never a half-written file. The same pass counts
            // the endpoint-filtered edges so the meta counts are exact.
            foreach (var vertex in vertices)
            {
                var invalid = FindNonExportableProperty(vertex);
                if (invalid != null)
                {
                    return UnprocessableProperty(vertex.Id, invalid);
                }
            }

            var exportedEdgeCount = 0;
            foreach (var edge in edges)
            {
                if (!vertexIds.Contains(edge.SourceVertex.Id) || !vertexIds.Contains(edge.TargetVertex.Id))
                {
                    continue;
                }

                var invalid = FindNonExportableProperty(edge);
                if (invalid != null)
                {
                    return UnprocessableProperty(edge.Id, invalid);
                }
                exportedEdgeCount++;
            }

            // Stream: meta, vertices, endpoint-filtered edges - never a whole-graph string.
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson";

            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            var linesSinceFlush = 0;
            var cancellation = HttpContext.RequestAborted;

            JsonlGraphFormat.WriteMetaLine(buffer, DateTime.UtcNow, vertices.Count, exportedEdgeCount);

            foreach (var vertex in vertices)
            {
                JsonlGraphFormat.WriteVertexLine(buffer, vertex);
                if (++linesSinceFlush >= ExportFlushEveryLines || buffer.WrittenCount >= ExportFlushBytes)
                {
                    await FlushBuffer(buffer, cancellation);
                    linesSinceFlush = 0;
                }
            }

            foreach (var edge in edges)
            {
                if (!vertexIds.Contains(edge.SourceVertex.Id) || !vertexIds.Contains(edge.TargetVertex.Id))
                {
                    continue;
                }

                JsonlGraphFormat.WriteEdgeLine(buffer, edge);
                if (++linesSinceFlush >= ExportFlushEveryLines || buffer.WrittenCount >= ExportFlushBytes)
                {
                    await FlushBuffer(buffer, cancellation);
                    linesSinceFlush = 0;
                }
            }

            await FlushBuffer(buffer, cancellation);
            return new EmptyResult();
        }

        private async Task FlushBuffer(ArrayBufferWriter<byte> buffer, CancellationToken cancellation)
        {
            if (buffer.WrittenCount > 0)
            {
                await Response.Body.WriteAsync(buffer.WrittenMemory, cancellation);
                buffer.Clear();
            }
        }

        /// <summary>Returns the key of the first non-exportable property, or null when clean.</summary>
        private static String FindNonExportableProperty(AGraphElementModel element)
        {
            var properties = element.GetAllProperties();
            if (properties == null)
            {
                return null;
            }

            foreach (var property in properties)
            {
                if (!JsonlGraphFormat.TryFormatValue(property.Value, out _, out _))
                {
                    return property.Key;
                }
            }

            return null;
        }

        private IActionResult UnprocessableProperty(Int32 elementId, String propertyKey)
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Graph not exportable",
                Detail = String.Format(
                    "Element {0} carries property '{1}' whose value is null or of a type outside the exportable allow-list; nothing was streamed.",
                    elementId, propertyKey)
            };
            problem.Extensions["elementId"] = elementId;
            problem.Extensions["propertyKey"] = propertyKey;
            return new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" }
            };
        }

        #endregion

        #region import

        /// <summary>
        /// Imports a fallen8-jsonl stream into an EMPTY graph, assigning fresh engine ids.
        /// </summary>
        /// <remarks>
        /// The body is read as a stream and processed line by line; lines batch into large
        /// create transactions (Fallen8:BulkIO:ImportBatchSize per transaction = one WAL entry +
        /// one fsync each). File ids are remapped unconditionally: they are references within the
        /// file, resolved for edge endpoints, never preserved as engine ids. A leading meta line
        /// is optional (grep-filtered subset files stay valid); when present, its counts act as a
        /// truncation guard.
        ///
        /// FAIL-FAST (honest): the first invalid line aborts the import with its exact line
        /// number. Batches committed before the failure STAY COMMITTED (each batch is atomic and
        /// WAL-logged; the file is not one transaction) - the error body reports the committed
        /// counts, and because import requires an empty graph, recovery is always "/tabularasa,
        /// fix the line, retry".
        ///
        /// NOTE on the body cap: when Fallen8:BulkIO:MaxImportRequestBytes is configured, a real
        /// Kestrel host may enforce it at the transport layer and answer 413 before this action's
        /// own check runs - the status is the same, but the problem body with committed counts is
        /// only guaranteed when the application-level check fires first.
        /// </remarks>
        /// <response code="200">The import completed; the body carries created counts</response>
        /// <response code="400">A line was invalid (malformed JSON, unknown fields, bad property
        /// type/value, duplicate file id, unresolved edge endpoint, over-long line, meta-count
        /// mismatch) - the problem body carries lineNumber and the committed counts</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="409">The graph is not empty (import requires an empty target; use
        /// /tabularasa or a fresh instance)</response>
        /// <response code="413">The request body exceeds the configured Fallen8:BulkIO:MaxImportRequestBytes</response>
        /// <response code="500">A batch transaction faulted internally; committed counts are reported</response>
        [HttpPost("/bulk/import")]
        [Consumes("application/x-ndjson")]
        [ProducesResponseType(typeof(BulkImportResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Import()
        {
            // Explicit per-endpoint body-size carve-out (api-security-boundary): this endpoint
            // exists to carry whole-graph payloads; its memory is bounded by construction. The
            // operator may cap it; the default is unlimited.
            var bodySizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySizeFeature != null && !bodySizeFeature.IsReadOnly)
            {
                bodySizeFeature.MaxRequestBodySize = _options.MaxImportRequestBytes;
            }

            // v1 target mode: empty graph only - id remapping is unambiguous and a failed import
            // is trivially recoverable. Checked on the request thread; a racing external write is
            // accepted for v1 (an import is an operator activity on a quiet instance).
            if (_fallen8.VertexCount != 0 || _fallen8.EdgeCount != 0)
            {
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Import requires an empty graph",
                    Detail = String.Format(
                        "The graph holds {0} vertices and {1} edges. Run /tabularasa first or import into a fresh instance.",
                        _fallen8.VertexCount, _fallen8.EdgeCount)
                };
                return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
            }

            var session = new ImportSession(_fallen8, _options);
            var reader = Request.BodyReader;
            var cancellation = HttpContext.RequestAborted;
            long bytesConsumed = 0;

            while (true)
            {
                var read = await reader.ReadAsync(cancellation);
                var buffer = read.Buffer;

                // Enforce the operator's body cap at the application level too (the Kestrel
                // feature is set above, but in-memory hosts do not enforce it).
                if (_options.MaxImportRequestBytes.HasValue &&
                    bytesConsumed + buffer.Length > _options.MaxImportRequestBytes.Value)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    var problem = new ProblemDetails
                    {
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Title = "Import body too large",
                        Detail = String.Format("The request body exceeds the configured Fallen8:BulkIO:MaxImportRequestBytes ({0}).",
                            _options.MaxImportRequestBytes.Value)
                    };
                    problem.Extensions["verticesCommitted"] = session.VerticesCreated;
                    problem.Extensions["edgesCommitted"] = session.EdgesCreated;
                    return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
                }

                while (TryReadLine(ref buffer, out var line, out var lineBytesConsumed))
                {
                    bytesConsumed += lineBytesConsumed;
                    var error = await session.ProcessLineAsync(line);
                    if (error != null)
                    {
                        return ImportProblem(error, session);
                    }
                }

                if (buffer.Length > _options.MaxLineBytes)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    return ImportProblem(ImportError.LineTooLong(session.LinesRead + 1, _options.MaxLineBytes), session);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCompleted)
                {
                    // A final line without a trailing newline.
                    var trailing = await reader.ReadAsync(cancellation);
                    if (trailing.Buffer.Length > 0)
                    {
                        var error = await session.ProcessLineAsync(trailing.Buffer);
                        reader.AdvanceTo(trailing.Buffer.End);
                        if (error != null)
                        {
                            return ImportProblem(error, session);
                        }
                    }
                    else
                    {
                        reader.AdvanceTo(trailing.Buffer.End);
                    }
                    break;
                }
            }

            var finishError = await session.FinishAsync();
            if (finishError != null)
            {
                return ImportProblem(finishError, session);
            }

            return Ok(new BulkImportResultREST
            {
                VerticesCreated = session.VerticesCreated,
                EdgesCreated = session.EdgesCreated,
                LinesRead = session.LinesRead
            });
        }

        /// <summary>Slices the next '\n'-terminated line off the buffer (trailing '\r' trimmed).
        /// <paramref name="bytesConsumed"/> reports the RAW bytes taken off the wire (line +
        /// terminator, before the CR trim), so the body-cap accounting stays exact for CRLF files.</summary>
        private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line, out long bytesConsumed)
        {
            var newline = buffer.PositionOf((byte)'\n');
            if (newline == null)
            {
                line = default;
                bytesConsumed = 0;
                return false;
            }

            line = buffer.Slice(0, newline.Value);
            bytesConsumed = line.Length + 1;
            buffer = buffer.Slice(buffer.GetPosition(1, newline.Value));

            if (line.Length > 0)
            {
                var end = line.Slice(line.Length - 1).FirstSpan;
                if (end.Length == 1 && end[0] == (byte)'\r')
                {
                    line = line.Slice(0, line.Length - 1);
                }
            }

            return true;
        }

        private IActionResult ImportProblem(ImportError error, ImportSession session)
        {
            var problem = new ProblemDetails
            {
                Status = error.Status,
                Title = error.Title,
                Detail = error.Detail
            };
            if (error.LineNumber > 0)
            {
                problem.Extensions["lineNumber"] = error.LineNumber;
            }
            problem.Extensions["verticesCommitted"] = session.VerticesCreated;
            problem.Extensions["edgesCommitted"] = session.EdgesCreated;

            return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        }

        #endregion

        #region import session

        /// <summary>A structured import failure (mapped to problem+json with line context).</summary>
        private sealed class ImportError
        {
            internal int Status;
            internal String Title;
            internal String Detail;
            internal long LineNumber;

            internal static ImportError Line(long lineNumber, String detail)
            {
                return new ImportError
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid import line",
                    Detail = String.Format("Line {0}: {1}.", lineNumber, detail),
                    LineNumber = lineNumber
                };
            }

            internal static ImportError LineTooLong(long lineNumber, int maxLineBytes)
            {
                return new ImportError
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Import line too long",
                    Detail = String.Format("Line {0} exceeds the configured Fallen8:BulkIO:MaxLineBytes ({1} bytes).",
                        lineNumber, maxLineBytes),
                    LineNumber = lineNumber
                };
            }

            internal static ImportError Batch(long lineNumber, TransactionFailureReason reason, String what)
            {
                var status = reason switch
                {
                    TransactionFailureReason.InvalidInput => StatusCodes.Status400BadRequest,
                    TransactionFailureReason.NotFound => StatusCodes.Status400BadRequest,
                    TransactionFailureReason.QuotaExceeded => StatusCodes.Status409Conflict,
                    TransactionFailureReason.Conflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status500InternalServerError
                };
                return new ImportError
                {
                    Status = status,
                    Title = "Import batch rolled back",
                    Detail = String.Format(
                        "A {0} batch ending at line {1} was rolled back ({2}); batches committed before it remain committed.",
                        what, lineNumber, reason),
                    LineNumber = lineNumber
                };
            }
        }

        /// <summary>
        ///   The state of one streaming import: pending batches, the file-id → engine-id map,
        ///   committed counts, and the optional meta counts (the truncation guard).
        /// </summary>
        private sealed class ImportSession
        {
            private readonly IFallen8 _fallen8;
            private readonly Fallen8BulkIOOptions _options;

            private readonly List<VertexDefinition> _pendingVertices = new List<VertexDefinition>();
            private readonly List<Int32> _pendingVertexFileIds = new List<Int32>();
            private readonly List<EdgeDefinition> _pendingEdges = new List<EdgeDefinition>();

            /// <summary>Last file line of each pending batch, tracked separately so a rolled-back
            /// edge batch never reports a vertex line's number.</summary>
            private long _pendingVertexBatchLastLine;
            private long _pendingEdgeBatchLastLine;

            /// <summary>File id → engine id. The only structure that grows with file size.</summary>
            private readonly Dictionary<Int32, Int32> _idMap = new Dictionary<Int32, Int32>();

            /// <summary>Every file id seen (vertices AND edges) - duplicates are an error.</summary>
            private readonly HashSet<Int32> _seenFileIds = new HashSet<Int32>();

            private int _metaVertexCount = -1;
            private int _metaEdgeCount = -1;

            internal long LinesRead;
            internal int VerticesCreated;
            internal int EdgesCreated;

            internal ImportSession(IFallen8 fallen8, Fallen8BulkIOOptions options)
            {
                _fallen8 = fallen8;
                _options = options;
            }

            internal async Task<ImportError> ProcessLineAsync(ReadOnlySequence<byte> line)
            {
                LinesRead++;

                if (line.Length > _options.MaxLineBytes)
                {
                    return ImportError.LineTooLong(LinesRead, _options.MaxLineBytes);
                }

                if (IsBlank(line))
                {
                    return null; // tolerated (trailing newline, hand-edited files)
                }

                var parseError = JsonlGraphFormat.TryParseLine(line, out var parsed);
                if (parseError != null)
                {
                    return ImportError.Line(LinesRead, parseError);
                }

                switch (parsed.Type)
                {
                    case JsonlGraphFormat.LineType.Meta:
                        if (LinesRead != 1)
                        {
                            return ImportError.Line(LinesRead, "a meta line is only valid as line 1");
                        }
                        _metaVertexCount = parsed.MetaVertexCount;
                        _metaEdgeCount = parsed.MetaEdgeCount;
                        return null;

                    case JsonlGraphFormat.LineType.Vertex:
                        if (!_seenFileIds.Add(parsed.Id))
                        {
                            return ImportError.Line(LinesRead, String.Format("duplicate file id {0}", parsed.Id));
                        }

                        _pendingVertices.Add(new VertexDefinition
                        {
                            CreationDate = parsed.CreationDate,
                            Label = parsed.Label,
                            Properties = parsed.Properties
                        });
                        _pendingVertexFileIds.Add(parsed.Id);
                        _pendingVertexBatchLastLine = LinesRead;

                        if (_pendingVertices.Count >= _options.ImportBatchSize)
                        {
                            return await FlushVerticesAsync();
                        }
                        return null;

                    case JsonlGraphFormat.LineType.Edge:
                        if (!_seenFileIds.Add(parsed.Id))
                        {
                            return ImportError.Line(LinesRead, String.Format("duplicate file id {0}", parsed.Id));
                        }

                        // Map the pending vertices first, so interleaved files are legal while
                        // export's vertices-then-edges layout stays the fast path.
                        if (_pendingVertices.Count > 0)
                        {
                            var flushError = await FlushVerticesAsync();
                            if (flushError != null)
                            {
                                return flushError;
                            }
                        }

                        if (!_idMap.TryGetValue(parsed.SourceId, out var sourceEngineId))
                        {
                            return ImportError.Line(LinesRead,
                                String.Format("edge references unknown source id {0}", parsed.SourceId));
                        }
                        if (!_idMap.TryGetValue(parsed.TargetId, out var targetEngineId))
                        {
                            return ImportError.Line(LinesRead,
                                String.Format("edge references unknown target id {0}", parsed.TargetId));
                        }

                        _pendingEdges.Add(new EdgeDefinition
                        {
                            SourceVertexId = sourceEngineId,
                            TargetVertexId = targetEngineId,
                            EdgePropertyId = parsed.EdgePropertyId,
                            CreationDate = parsed.CreationDate,
                            Label = parsed.Label,
                            Properties = parsed.Properties
                        });
                        _pendingEdgeBatchLastLine = LinesRead;

                        if (_pendingEdges.Count >= _options.ImportBatchSize)
                        {
                            return await FlushEdgesAsync();
                        }
                        return null;

                    default:
                        return ImportError.Line(LinesRead, "unhandled line type");
                }
            }

            internal async Task<ImportError> FinishAsync()
            {
                if (_pendingVertices.Count > 0)
                {
                    var error = await FlushVerticesAsync();
                    if (error != null)
                    {
                        return error;
                    }
                }

                if (_pendingEdges.Count > 0)
                {
                    var error = await FlushEdgesAsync();
                    if (error != null)
                    {
                        return error;
                    }
                }

                // Truncation guard: with a meta line present, the produced counts must match.
                if (_metaVertexCount >= 0 && _metaVertexCount != VerticesCreated)
                {
                    return ImportError.Line(LinesRead, String.Format(
                        "meta declared {0} vertices but the file produced {1} (truncated or edited file; drop the meta line for hand-filtered subsets)",
                        _metaVertexCount, VerticesCreated));
                }
                if (_metaEdgeCount >= 0 && _metaEdgeCount != EdgesCreated)
                {
                    return ImportError.Line(LinesRead, String.Format(
                        "meta declared {0} edges but the file produced {1} (truncated or edited file; drop the meta line for hand-filtered subsets)",
                        _metaEdgeCount, EdgesCreated));
                }

                return null;
            }

            /// <summary>
            ///   One batch = one transaction = one WAL entry + one group-commit fsync. On commit,
            ///   construct-then-commit order (transaction-atomicity) guarantees
            ///   <c>created[i] ↔ batch[i]</c>, which is what makes the id map exact.
            /// </summary>
            private async Task<ImportError> FlushVerticesAsync()
            {
                var tx = new CreateVerticesTransaction { Vertices = _pendingVertices.GetRange(0, _pendingVertices.Count) };
                var info = _fallen8.EnqueueTransaction(tx);
                await info.Completion;

                if (info.TransactionState == TransactionState.RolledBack)
                {
                    return ImportError.Batch(_pendingVertexBatchLastLine, info.FailureReason, "vertex");
                }

                var created = tx.GetCreatedVertices();
                for (var i = 0; i < created.Count; i++)
                {
                    _idMap[_pendingVertexFileIds[i]] = created[i].Id;
                }

                VerticesCreated += created.Count;
                _pendingVertices.Clear();
                _pendingVertexFileIds.Clear();
                return null;
            }

            private async Task<ImportError> FlushEdgesAsync()
            {
                var tx = new CreateEdgesTransaction { Edges = _pendingEdges.GetRange(0, _pendingEdges.Count) };
                var info = _fallen8.EnqueueTransaction(tx);
                await info.Completion;

                if (info.TransactionState == TransactionState.RolledBack)
                {
                    return ImportError.Batch(_pendingEdgeBatchLastLine, info.FailureReason, "edge");
                }

                EdgesCreated += tx.GetCreatedEdges().Count;
                _pendingEdges.Clear();
                return null;
            }

            private static bool IsBlank(ReadOnlySequence<byte> line)
            {
                foreach (var segment in line)
                {
                    foreach (var b in segment.Span)
                    {
                        if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r')
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        #endregion
    }
}
