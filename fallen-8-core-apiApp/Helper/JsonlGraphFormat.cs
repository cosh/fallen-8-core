// MIT License
//
// JsonlGraphFormat.cs
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
using System.Globalization;
using System.Text.Json;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The <c>fallen8-jsonl</c> version-1 line schema (feature bulk-import-export): one JSON
    ///   object per line - a leading <c>meta</c> line (format version + exact counts), then
    ///   <c>vertex</c> lines, then <c>edge</c> lines. Property values travel as
    ///   <c>{"type": &lt;allow-listed name&gt;, "value": &lt;invariant string&gt;}</c> pairs using
    ///   pinned round-trip formats (<c>"R"</c> for Single/Double, <c>"O"</c> for
    ///   DateTime/DateTimeOffset, <c>"c"</c> for TimeSpan, <c>"D"</c> for Guid, invariant
    ///   <c>ToString</c> otherwise), so every <see cref="AllowedLiteralTypes"/> type round-trips
    ///   value-exactly INCLUDING its CLR type. Types resolve ONLY through
    ///   <see cref="AllowedLiteralTypes"/> (never <c>Type.GetType</c> on file input, preserving
    ///   dynamic-code-resource-limits R3); value PARSING is this class's own per-type
    ///   invariant-culture code because interchange must not inherit the server culture (and
    ///   TimeSpan/Guid/DateTimeOffset are not IConvertible at all).
    ///
    ///   <para>Strict v1: unknown top-level fields are rejected; the <c>version</c> field is the
    ///   only evolution mechanism.</para>
    /// </summary>
    public static class JsonlGraphFormat
    {
        public const String FormatName = "fallen8-jsonl";
        public const Int32 FormatVersion = 1;

        #region export - line writing

        private static readonly JsonWriterOptions _writerOptions = new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        };

        private static readonly byte[] _newline = { (byte)'\n' };

        /// <summary>Writes the leading meta line.</summary>
        public static void WriteMetaLine(IBufferWriter<byte> output, DateTime exportedAtUtc, int vertexCount, int edgeCount)
        {
            using (var json = new Utf8JsonWriter(output, _writerOptions))
            {
                json.WriteStartObject();
                json.WriteString("type", "meta");
                json.WriteString("format", FormatName);
                json.WriteNumber("version", FormatVersion);
                json.WriteString("exportedAtUtc", exportedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                json.WriteNumber("vertexCount", vertexCount);
                json.WriteNumber("edgeCount", edgeCount);
                json.WriteEndObject();
            }
            output.Write(_newline);
        }

        /// <summary>Writes one vertex line (the caller pre-validated every property type).</summary>
        public static void WriteVertexLine(IBufferWriter<byte> output, VertexModel vertex)
        {
            using (var json = new Utf8JsonWriter(output, _writerOptions))
            {
                json.WriteStartObject();
                json.WriteString("type", "vertex");
                json.WriteNumber("id", vertex.Id);
                WriteElementCommon(json, vertex);
                json.WriteEndObject();
            }
            output.Write(_newline);
        }

        /// <summary>Writes one edge line (the caller pre-validated every property type).</summary>
        public static void WriteEdgeLine(IBufferWriter<byte> output, EdgeModel edge)
        {
            using (var json = new Utf8JsonWriter(output, _writerOptions))
            {
                json.WriteStartObject();
                json.WriteString("type", "edge");
                json.WriteNumber("id", edge.Id);
                json.WriteString("edgePropertyId", edge.EdgePropertyId);
                json.WriteNumber("source", edge.SourceVertex.Id);
                json.WriteNumber("target", edge.TargetVertex.Id);
                WriteElementCommon(json, edge);
                json.WriteEndObject();
            }
            output.Write(_newline);
        }

        private static void WriteElementCommon(Utf8JsonWriter json, AGraphElementModel element)
        {
            if (element.Label != null)
            {
                json.WriteString("label", element.Label);
            }

            json.WriteNumber("creationDate", element.CreationDate);

            var properties = element.GetAllProperties();
            if (properties != null && properties.Count > 0)
            {
                json.WriteStartObject("properties");
                foreach (var property in properties)
                {
                    // The caller's pre-stream validation guarantees this succeeds.
                    if (!TryFormatValue(property.Value, out var typeName, out var formatted))
                    {
                        throw new InvalidOperationException(String.Format(
                            "Property '{0}' on element {1} escaped export pre-validation.", property.Key, element.Id));
                    }

                    json.WriteStartObject(property.Key);
                    json.WriteString("type", typeName);
                    json.WriteString("value", formatted);
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
        }

        /// <summary>
        ///   Formats a property value as its pinned invariant round-trip string. Returns false
        ///   for a null value or a runtime type outside the allow-list - the export rejects those
        ///   up front (422) rather than degrading silently.
        /// </summary>
        public static bool TryFormatValue(Object value, out String typeName, out String formatted)
        {
            typeName = null;
            formatted = null;

            if (value == null)
            {
                return false;
            }

            var type = value.GetType();
            if (!AllowedLiteralTypes.TryResolve(type.FullName, out var resolved) || resolved != type)
            {
                return false;
            }

            typeName = type.FullName;
            formatted = value switch
            {
                String s => s,
                Boolean b => b ? "true" : "false",
                Single f => f.ToString("R", CultureInfo.InvariantCulture),
                Double d => d.ToString("R", CultureInfo.InvariantCulture),
                Decimal m => m.ToString(CultureInfo.InvariantCulture),
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
                Guid g => g.ToString("D", CultureInfo.InvariantCulture),
                Char c => c.ToString(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
            return true;
        }

        #endregion

        #region import - line parsing

        public enum LineType
        {
            Meta,
            Vertex,
            Edge
        }

        /// <summary>One parsed line. Field presence depends on <see cref="Type"/>.</summary>
        public sealed class ParsedLine
        {
            public LineType Type;

            // meta
            public Int32 MetaVertexCount;
            public Int32 MetaEdgeCount;

            // vertex + edge
            public Int32 Id;
            public String Label;
            public UInt32 CreationDate;
            public Dictionary<String, Object> Properties;

            // edge only
            public String EdgePropertyId;
            public Int32 SourceId;
            public Int32 TargetId;
        }

        /// <summary>
        ///   Parses one line strictly. Returns null and a <paramref name="parsed"/> result on
        ///   success, otherwise a human-readable error (the caller adds the line number).
        /// </summary>
        public static String TryParseLine(ReadOnlySequence<byte> line, out ParsedLine parsed)
        {
            parsed = null;

            JsonDocument document;
            try
            {
                var jsonReader = new Utf8JsonReader(line);
                document = JsonDocument.ParseValue(ref jsonReader);
            }
            catch (JsonException ex)
            {
                return "malformed JSON: " + ex.Message;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return "the line is not a JSON object";
                }

                if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    return "missing required string field 'type'";
                }

                switch (typeElement.GetString())
                {
                    case "meta":
                        return ParseMeta(root, out parsed);
                    case "vertex":
                        return ParseElement(root, LineType.Vertex, out parsed);
                    case "edge":
                        return ParseElement(root, LineType.Edge, out parsed);
                    default:
                        return String.Format("unknown line type '{0}' (expected meta, vertex or edge)", typeElement.GetString());
                }
            }
        }

        private static readonly HashSet<String> _metaFields = new HashSet<String>(StringComparer.Ordinal)
        {
            "type", "format", "version", "exportedAtUtc", "vertexCount", "edgeCount"
        };

        private static readonly HashSet<String> _vertexFields = new HashSet<String>(StringComparer.Ordinal)
        {
            "type", "id", "label", "creationDate", "properties"
        };

        private static readonly HashSet<String> _edgeFields = new HashSet<String>(StringComparer.Ordinal)
        {
            "type", "id", "label", "creationDate", "properties", "edgePropertyId", "source", "target"
        };

        private static String ParseMeta(JsonElement root, out ParsedLine parsed)
        {
            parsed = null;

            foreach (var field in root.EnumerateObject())
            {
                if (!_metaFields.Contains(field.Name))
                {
                    return String.Format("unknown field '{0}' on a meta line (strict v{1})", field.Name, FormatVersion);
                }
            }

            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                !String.Equals(format.GetString(), FormatName, StringComparison.Ordinal))
            {
                return String.Format("meta 'format' must be '{0}'", FormatName);
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionValue))
            {
                return "meta 'version' must be an integer";
            }

            if (versionValue != FormatVersion)
            {
                return String.Format("unsupported format version {0} (this build reads version {1})", versionValue, FormatVersion);
            }

            var result = new ParsedLine { Type = LineType.Meta, MetaVertexCount = -1, MetaEdgeCount = -1 };

            if (root.TryGetProperty("vertexCount", out var vertexCount))
            {
                if (vertexCount.ValueKind != JsonValueKind.Number || !vertexCount.TryGetInt32(out result.MetaVertexCount) || result.MetaVertexCount < 0)
                {
                    return "meta 'vertexCount' must be a non-negative integer";
                }
            }

            if (root.TryGetProperty("edgeCount", out var edgeCount))
            {
                if (edgeCount.ValueKind != JsonValueKind.Number || !edgeCount.TryGetInt32(out result.MetaEdgeCount) || result.MetaEdgeCount < 0)
                {
                    return "meta 'edgeCount' must be a non-negative integer";
                }
            }

            parsed = result;
            return null;
        }

        private static String ParseElement(JsonElement root, LineType lineType, out ParsedLine parsed)
        {
            parsed = null;
            var allowed = lineType == LineType.Vertex ? _vertexFields : _edgeFields;

            foreach (var field in root.EnumerateObject())
            {
                if (!allowed.Contains(field.Name))
                {
                    return String.Format("unknown field '{0}' on a {1} line (strict v{2})",
                        field.Name, lineType == LineType.Vertex ? "vertex" : "edge", FormatVersion);
                }
            }

            var result = new ParsedLine { Type = lineType };

            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out result.Id))
            {
                return "missing or non-integer required field 'id'";
            }

            if (root.TryGetProperty("label", out var label))
            {
                if (label.ValueKind == JsonValueKind.String)
                {
                    result.Label = label.GetString();
                }
                else if (label.ValueKind != JsonValueKind.Null)
                {
                    return "'label' must be a string or null";
                }
            }

            if (!root.TryGetProperty("creationDate", out var creationDate) ||
                creationDate.ValueKind != JsonValueKind.Number || !creationDate.TryGetUInt32(out result.CreationDate))
            {
                return "missing or invalid required field 'creationDate' (a UInt32 Unix timestamp)";
            }

            if (lineType == LineType.Edge)
            {
                if (!root.TryGetProperty("edgePropertyId", out var edgePropertyId) ||
                    edgePropertyId.ValueKind != JsonValueKind.String || String.IsNullOrEmpty(edgePropertyId.GetString()))
                {
                    return "missing required string field 'edgePropertyId'";
                }
                result.EdgePropertyId = edgePropertyId.GetString();

                if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Number || !source.TryGetInt32(out result.SourceId))
                {
                    return "missing or non-integer required field 'source'";
                }

                if (!root.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetInt32(out result.TargetId))
                {
                    return "missing or non-integer required field 'target'";
                }
            }

            if (root.TryGetProperty("properties", out var properties))
            {
                if (properties.ValueKind != JsonValueKind.Object)
                {
                    return "'properties' must be an object";
                }

                foreach (var property in properties.EnumerateObject())
                {
                    var error = ParsePropertyPair(property.Name, property.Value, out var value);
                    if (error != null)
                    {
                        return error;
                    }

                    (result.Properties ??= new Dictionary<String, Object>()).Add(property.Name, value);
                }
            }

            parsed = result;
            return null;
        }

        private static String ParsePropertyPair(String key, JsonElement pair, out Object value)
        {
            value = null;

            if (pair.ValueKind != JsonValueKind.Object)
            {
                return String.Format("property '{0}' must be a {{type, value}} object", key);
            }

            String typeName = null, raw = null;
            foreach (var field in pair.EnumerateObject())
            {
                switch (field.Name)
                {
                    case "type":
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            return String.Format("property '{0}': 'type' must be a string", key);
                        }
                        typeName = field.Value.GetString();
                        break;
                    case "value":
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            return String.Format("property '{0}': 'value' must be a string", key);
                        }
                        raw = field.Value.GetString();
                        break;
                    default:
                        return String.Format("property '{0}': unknown field '{1}'", key, field.Name);
                }
            }

            if (typeName == null || raw == null)
            {
                return String.Format("property '{0}' must carry both 'type' and 'value'", key);
            }

            var parseError = TryParseValue(typeName, raw, out value);
            return parseError == null ? null : String.Format("property '{0}': {1}", key, parseError);
        }

        /// <summary>
        ///   Converts a typed string pair back into its CLR value. The type resolves ONLY through
        ///   <see cref="AllowedLiteralTypes"/>; parsing is invariant-culture with the pinned
        ///   round-trip formats.
        /// </summary>
        public static String TryParseValue(String typeName, String raw, out Object value)
        {
            value = null;

            if (!AllowedLiteralTypes.TryResolve(typeName, out var type))
            {
                return String.Format("'{0}' is not an allow-listed property type", typeName);
            }

            try
            {
                var invariant = CultureInfo.InvariantCulture;
                if (type == typeof(String))
                {
                    value = raw;
                }
                else if (type == typeof(Boolean))
                {
                    value = Boolean.Parse(raw);
                }
                else if (type == typeof(Byte))
                {
                    value = Byte.Parse(raw, invariant);
                }
                else if (type == typeof(SByte))
                {
                    value = SByte.Parse(raw, invariant);
                }
                else if (type == typeof(Int16))
                {
                    value = Int16.Parse(raw, invariant);
                }
                else if (type == typeof(UInt16))
                {
                    value = UInt16.Parse(raw, invariant);
                }
                else if (type == typeof(Int32))
                {
                    value = Int32.Parse(raw, invariant);
                }
                else if (type == typeof(UInt32))
                {
                    value = UInt32.Parse(raw, invariant);
                }
                else if (type == typeof(Int64))
                {
                    value = Int64.Parse(raw, invariant);
                }
                else if (type == typeof(UInt64))
                {
                    value = UInt64.Parse(raw, invariant);
                }
                else if (type == typeof(Single))
                {
                    value = Single.Parse(raw, NumberStyles.Float, invariant);
                }
                else if (type == typeof(Double))
                {
                    value = Double.Parse(raw, NumberStyles.Float, invariant);
                }
                else if (type == typeof(Decimal))
                {
                    value = Decimal.Parse(raw, NumberStyles.Number, invariant);
                }
                else if (type == typeof(Char))
                {
                    if (raw.Length != 1)
                    {
                        return "a Char value must be exactly one character";
                    }
                    value = raw[0];
                }
                else if (type == typeof(DateTime))
                {
                    value = DateTime.ParseExact(raw, "O", invariant, DateTimeStyles.RoundtripKind);
                }
                else if (type == typeof(DateTimeOffset))
                {
                    value = DateTimeOffset.ParseExact(raw, "O", invariant);
                }
                else if (type == typeof(TimeSpan))
                {
                    value = TimeSpan.ParseExact(raw, "c", invariant);
                }
                else if (type == typeof(Guid))
                {
                    value = Guid.ParseExact(raw, "D");
                }
                else
                {
                    return String.Format("'{0}' has no pinned parse format", typeName);
                }
            }
            catch (FormatException ex)
            {
                return String.Format("value '{0}' is not a valid {1}: {2}", raw, type.Name, ex.Message);
            }
            catch (OverflowException)
            {
                return String.Format("value '{0}' overflows {1}", raw, type.Name);
            }

            return null;
        }

        #endregion
    }
}
