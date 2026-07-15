// MIT License
//
// WalTransactionCodec.cs
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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   The single source of truth for the write-ahead-log entry payload format: it classifies a
    ///   committed transaction into a <see cref="WalEntryType" />, serializes its definition, and
    ///   reconstructs an equivalent transaction on replay. The reconstructed transaction is
    ///   re-executed against the loaded snapshot, so replay reuses the exact committed transaction
    ///   logic rather than reimplementing element construction.
    ///
    ///   <para>The definition encoding deliberately mirrors the snapshot payload encoding
    ///   (var-int ids/counts, tokenized string keys/labels/edge-property-ids, <c>WriteObject</c> for
    ///   polymorphic property values), so a property value round-trips through the log exactly as it
    ///   does through a snapshot (a complex value comes back as a JSON element on both paths).</para>
    /// </summary>
    internal static class WalTransactionCodec
    {
        /// <summary>
        ///   Classifies <paramref name="tx" /> as a loggable operation. Data-mutating transactions,
        ///   the id-space lifecycle transactions (Trim, TabulaRasa), and the subgraph transactions
        ///   are loggable; Save/Load are lifecycle operations that reset/replace the log rather than
        ///   being logged. A <see cref="CreateSubGraphTransaction" /> is loggable ONLY when its
        ///   committed result carries a recipe (<see cref="Algorithms.SubGraph.SubGraphResult.Recipe" />):
        ///   a delegate-only subgraph has no serializable recipe and is not persisted by a snapshot
        ///   either, so logging one would produce an entry that could never be replayed.
        /// </summary>
        internal static bool TryGetEntryType(ATransaction tx, out WalEntryType type)
        {
            switch (tx)
            {
                case CreateVertexTransaction _: type = WalEntryType.CreateVertex; return true;
                case CreateVerticesTransaction _: type = WalEntryType.CreateVertices; return true;
                case CreateEdgeTransaction _: type = WalEntryType.CreateEdge; return true;
                case CreateEdgesTransaction _: type = WalEntryType.CreateEdges; return true;
                case AddPropertyTransaction _: type = WalEntryType.AddProperty; return true;
                case AddPropertiesTransaction _: type = WalEntryType.AddProperties; return true;
                case RemovePropertyTransaction _: type = WalEntryType.RemoveProperty; return true;
                case RemoveGraphElementTransaction _: type = WalEntryType.RemoveGraphElement; return true;
                case RemoveGraphElementsTransaction _: type = WalEntryType.RemoveGraphElements; return true;
                case TrimTransaction _: type = WalEntryType.Trim; return true;
                case TabulaRasaTransaction _: type = WalEntryType.TabulaRasa; return true;

                case CreateSubGraphTransaction create:
                    // Only a recipe-bearing subgraph is replayable (and persistable). A delegate-only
                    // create is classified not-loggable, exactly like Save/Load.
                    if (create.SubGraphCreated?.Recipe == null)
                    {
                        type = default;
                        return false;
                    }
                    type = WalEntryType.CreateSubGraph;
                    return true;

                case RemoveSubGraphTransaction _: type = WalEntryType.RemoveSubGraph; return true;

                case RegisterStoredQueryTransaction _: type = WalEntryType.RegisterStoredQuery; return true;
                case RemoveStoredQueryTransaction _: type = WalEntryType.RemoveStoredQuery; return true;

                default: type = default; return false;
            }
        }

        /// <summary>
        ///   Serializes one entry payload: the <paramref name="type" /> byte followed by the
        ///   transaction's definition (nothing more for the Trim/TabulaRasa markers, for which
        ///   <paramref name="tx" /> may be null).
        /// </summary>
        internal static byte[] SerializeEntry(WalEntryType type, ATransaction tx)
        {
            using (var mem = new MemoryStream())
            {
                var writer = new SerializationWriter(mem, true);
                writer.Write((byte)type);

                switch (type)
                {
                    case WalEntryType.CreateVertex:
                        WriteVertexDefinition(writer, ((CreateVertexTransaction)tx).Definition);
                        break;

                    case WalEntryType.CreateVertices:
                        {
                            var list = ((CreateVerticesTransaction)tx).Vertices ?? new List<VertexDefinition>();
                            writer.WriteVarInt32(list.Count);
                            foreach (var def in list)
                            {
                                WriteVertexDefinition(writer, def);
                            }
                            break;
                        }

                    case WalEntryType.CreateEdge:
                        WriteEdgeDefinition(writer, ((CreateEdgeTransaction)tx).Definition);
                        break;

                    case WalEntryType.CreateEdges:
                        {
                            var list = ((CreateEdgesTransaction)tx).Edges ?? new List<EdgeDefinition>();
                            writer.WriteVarInt32(list.Count);
                            foreach (var def in list)
                            {
                                WriteEdgeDefinition(writer, def);
                            }
                            break;
                        }

                    case WalEntryType.AddProperty:
                        WritePropertyAddDefinition(writer, ((AddPropertyTransaction)tx).Definition);
                        break;

                    case WalEntryType.AddProperties:
                        {
                            var list = ((AddPropertiesTransaction)tx).Properties ?? new List<PropertyAddDefinition>();
                            writer.WriteVarInt32(list.Count);
                            foreach (var def in list)
                            {
                                WritePropertyAddDefinition(writer, def);
                            }
                            break;
                        }

                    case WalEntryType.RemoveProperty:
                        {
                            var t = (RemovePropertyTransaction)tx;
                            writer.WriteVarInt32(t.GraphElementId);
                            writer.WriteOptimized(t.PropertyId);
                            break;
                        }

                    case WalEntryType.RemoveGraphElement:
                        writer.WriteVarInt32(((RemoveGraphElementTransaction)tx).GraphElementId);
                        break;

                    case WalEntryType.RemoveGraphElements:
                        {
                            var ids = ((RemoveGraphElementsTransaction)tx).GraphElementIds ?? new List<int>();
                            writer.WriteVarInt32(ids.Count);
                            foreach (var id in ids)
                            {
                                writer.WriteVarInt32(id);
                            }
                            break;
                        }

                    case WalEntryType.CreateSubGraph:
                        {
                            var create = (CreateSubGraphTransaction)tx;
                            // The recipe round-trips through the log as JSON via the SAME source-gen
                            // serialization the snapshot recipe-manifest uses. The source subgraph
                            // name (empty for a root subgraph) lets replay resolve a nested
                            // subgraph's source by its stable name, in commit order.
                            var json = JsonSerializer.Serialize(
                                create.SubGraphCreated.Recipe, CoreJsonContext.Default.SubGraphRecipe);
                            writer.WriteOptimized(json);
                            writer.WriteOptimized(create.SourceSubGraphName ?? String.Empty);
                            break;
                        }

                    case WalEntryType.RemoveSubGraph:
                        writer.WriteOptimized(((RemoveSubGraphTransaction)tx).SubGraphName ?? String.Empty);
                        break;

                    case WalEntryType.RegisterStoredQuery:
                        {
                            // The whole definition (name, kind, source, metadata) round-trips as
                            // JSON via the SAME source-gen serialization the snapshot manifest
                            // uses, so Save and WAL agree byte-for-byte on what a stored query is.
                            var register = (RegisterStoredQueryTransaction)tx;
                            var json = JsonSerializer.Serialize(
                                register.Entry.Definition, CoreJsonContext.Default.StoredQueryDefinition);
                            writer.WriteOptimized(json);
                            break;
                        }

                    case WalEntryType.RemoveStoredQuery:
                        writer.WriteOptimized(((RemoveStoredQueryTransaction)tx).Name ?? String.Empty);
                        break;

                    case WalEntryType.Trim:
                    case WalEntryType.TabulaRasa:
                        // Markers: the type byte is the whole payload.
                        break;

                    default:
                        throw new InvalidOperationException("Unknown WAL entry type " + type);
                }

                writer.UpdateHeader();
                writer.Flush();
                return mem.ToArray();
            }
        }

        /// <summary>
        ///   Reconstructs the transaction from an entry payload. Returns the transaction to
        ///   re-execute for data entries, or <c>null</c> for the Trim/TabulaRasa markers (whose
        ///   <paramref name="type" /> the caller acts on directly).
        /// </summary>
        internal static ATransaction Deserialize(byte[] payload, out WalEntryType type)
        {
            var reader = new SerializationReader(new MemoryStream(payload, writable: false));
            type = (WalEntryType)reader.ReadByte();

            switch (type)
            {
                case WalEntryType.CreateVertex:
                    return new CreateVertexTransaction { Definition = ReadVertexDefinition(reader) };

                case WalEntryType.CreateVertices:
                    {
                        var tx = new CreateVerticesTransaction();
                        var count = reader.ReadOptimizedInt32Checked("wal vertex definitions");
                        var list = new List<VertexDefinition>(count);
                        for (var i = 0; i < count; i++)
                        {
                            list.Add(ReadVertexDefinition(reader));
                        }
                        tx.Vertices = list;
                        return tx;
                    }

                case WalEntryType.CreateEdge:
                    return new CreateEdgeTransaction { Definition = ReadEdgeDefinition(reader) };

                case WalEntryType.CreateEdges:
                    {
                        var tx = new CreateEdgesTransaction();
                        var count = reader.ReadOptimizedInt32Checked("wal edge definitions");
                        var list = new List<EdgeDefinition>(count);
                        for (var i = 0; i < count; i++)
                        {
                            list.Add(ReadEdgeDefinition(reader));
                        }
                        tx.Edges = list;
                        return tx;
                    }

                case WalEntryType.AddProperty:
                    return new AddPropertyTransaction { Definition = ReadPropertyAddDefinition(reader) };

                case WalEntryType.AddProperties:
                    {
                        var tx = new AddPropertiesTransaction();
                        var count = reader.ReadOptimizedInt32Checked("wal property definitions");
                        var list = new List<PropertyAddDefinition>(count);
                        for (var i = 0; i < count; i++)
                        {
                            list.Add(ReadPropertyAddDefinition(reader));
                        }
                        tx.Properties = list;
                        return tx;
                    }

                case WalEntryType.RemoveProperty:
                    {
                        var id = reader.ReadOptimizedInt32();
                        var propertyId = reader.ReadOptimizedString();
                        return new RemovePropertyTransaction { GraphElementId = id, PropertyId = propertyId };
                    }

                case WalEntryType.RemoveGraphElement:
                    return new RemoveGraphElementTransaction { GraphElementId = reader.ReadOptimizedInt32() };

                case WalEntryType.RemoveGraphElements:
                    {
                        var count = reader.ReadOptimizedInt32Checked("wal removal ids");
                        var ids = new List<int>(count);
                        for (var i = 0; i < count; i++)
                        {
                            ids.Add(reader.ReadOptimizedInt32());
                        }
                        return new RemoveGraphElementsTransaction { GraphElementIds = ids };
                    }

                case WalEntryType.RemoveSubGraph:
                    return new RemoveSubGraphTransaction { SubGraphName = reader.ReadOptimizedString() };

                case WalEntryType.RemoveStoredQuery:
                    return new RemoveStoredQueryTransaction { Name = reader.ReadOptimizedString() };

                case WalEntryType.Trim:
                case WalEntryType.TabulaRasa:
                case WalEntryType.CreateSubGraph:
                case WalEntryType.RegisterStoredQuery:
                    // Trim/TabulaRasa carry no payload; CreateSubGraph/RegisterStoredQuery need the
                    // (engine-external) compilers, so the replay loop decodes them via
                    // TryDecodeSubGraphCreate/TryDecodeStoredQueryRegister and drives the compile +
                    // re-execute itself rather than re-executing a ready transaction.
                    return null;

                default:
                    throw new InvalidDataException("Unknown WAL entry type " + (byte)type);
            }
        }

        /// <summary>
        ///   Decodes a <see cref="WalEntryType.RegisterStoredQuery" /> entry into the persisted
        ///   definition. Returns false (never throws) when the payload is not a stored-query
        ///   registration entry or its definition JSON cannot be parsed, so a single
        ///   unusable-but-CRC-valid entry is skipped on replay rather than halting recovery - each
        ///   entry has its own payload so skipping one cannot corrupt the next.
        /// </summary>
        internal static bool TryDecodeStoredQueryRegister(byte[] payload, out StoredQueryDefinition definition)
        {
            definition = null;

            try
            {
                var reader = new SerializationReader(new MemoryStream(payload, writable: false));
                if ((WalEntryType)reader.ReadByte() != WalEntryType.RegisterStoredQuery)
                {
                    return false;
                }

                var json = reader.ReadOptimizedString();
                if (string.IsNullOrEmpty(json))
                {
                    return false;
                }

                definition = JsonSerializer.Deserialize(json, CoreJsonContext.Default.StoredQueryDefinition);
                return definition != null;
            }
            catch
            {
                definition = null;
                return false;
            }
        }

        /// <summary>
        ///   Decodes a <see cref="WalEntryType.CreateSubGraph" /> entry into the persisted recipe and
        ///   the source subgraph name (empty for a root subgraph). Returns false (never throws) when
        ///   the payload is not a create-subgraph entry or its recipe JSON cannot be parsed, so a
        ///   single unusable-but-CRC-valid subgraph entry is skipped on replay rather than halting
        ///   recovery - subgraphs are rebuildable derived state, and each entry has its own payload so
        ///   skipping one cannot corrupt the next.
        /// </summary>
        internal static bool TryDecodeSubGraphCreate(byte[] payload, out SubGraphRecipe recipe, out string sourceSubGraphName)
        {
            recipe = null;
            sourceSubGraphName = null;

            try
            {
                var reader = new SerializationReader(new MemoryStream(payload, writable: false));
                if ((WalEntryType)reader.ReadByte() != WalEntryType.CreateSubGraph)
                {
                    return false;
                }

                var json = reader.ReadOptimizedString();
                sourceSubGraphName = reader.ReadOptimizedString();

                if (string.IsNullOrEmpty(json))
                {
                    return false;
                }

                recipe = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SubGraphRecipe);
                return recipe != null;
            }
            catch
            {
                recipe = null;
                sourceSubGraphName = null;
                return false;
            }
        }

        #region definition (de)serialization

        private static void WriteVertexDefinition(SerializationWriter writer, VertexDefinition def)
        {
            writer.Write(def.CreationDate);
            writer.WriteOptimized(def.Label);
            WriteProperties(writer, def.Properties);
        }

        private static VertexDefinition ReadVertexDefinition(SerializationReader reader)
        {
            var creationDate = reader.ReadUInt32();
            var label = reader.ReadOptimizedString();
            var properties = ReadProperties(reader);
            return new VertexDefinition { CreationDate = creationDate, Label = label, Properties = properties };
        }

        private static void WriteEdgeDefinition(SerializationWriter writer, EdgeDefinition def)
        {
            writer.WriteVarInt32(def.SourceVertexId);
            writer.WriteVarInt32(def.TargetVertexId);
            writer.WriteOptimized(def.EdgePropertyId);
            writer.Write(def.CreationDate);
            writer.WriteOptimized(def.Label);
            WriteProperties(writer, def.Properties);
        }

        private static EdgeDefinition ReadEdgeDefinition(SerializationReader reader)
        {
            var sourceVertexId = reader.ReadOptimizedInt32();
            var targetVertexId = reader.ReadOptimizedInt32();
            var edgePropertyId = reader.ReadOptimizedString();
            var creationDate = reader.ReadUInt32();
            var label = reader.ReadOptimizedString();
            var properties = ReadProperties(reader);
            return new EdgeDefinition
            {
                SourceVertexId = sourceVertexId,
                TargetVertexId = targetVertexId,
                EdgePropertyId = edgePropertyId,
                CreationDate = creationDate,
                Label = label,
                Properties = properties
            };
        }

        private static void WritePropertyAddDefinition(SerializationWriter writer, PropertyAddDefinition def)
        {
            writer.WriteVarInt32(def.GraphElementId);
            writer.WriteOptimized(def.PropertyId);
            writer.WriteObject(def.Property);
        }

        private static PropertyAddDefinition ReadPropertyAddDefinition(SerializationReader reader)
        {
            var id = reader.ReadOptimizedInt32();
            var propertyId = reader.ReadOptimizedString();
            var property = reader.ReadObject();
            return new PropertyAddDefinition { GraphElementId = id, PropertyId = propertyId, Property = property };
        }

        /// <summary>
        ///   Writes a property map (key count as var-int, then tokenized key + <c>WriteObject</c>
        ///   value pairs). A null or empty map is written as a zero count; both read back as null,
        ///   which is indistinguishable to element creation (an empty map allocates no store).
        /// </summary>
        private static void WriteProperties(SerializationWriter writer, Dictionary<String, Object> properties)
        {
            var count = properties == null ? 0 : properties.Count;
            writer.WriteVarInt32(count);
            if (count == 0)
            {
                return;
            }
            foreach (var kv in properties)
            {
                writer.WriteOptimized(kv.Key);
                writer.WriteObject(kv.Value);
            }
        }

        private static Dictionary<String, Object> ReadProperties(SerializationReader reader)
        {
            var count = reader.ReadOptimizedInt32Checked("wal properties");
            if (count == 0)
            {
                return null;
            }
            var properties = new Dictionary<String, Object>(count);
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadOptimizedString();
                var value = reader.ReadObject();
                properties[key] = value;
            }
            return properties;
        }

        #endregion
    }
}
