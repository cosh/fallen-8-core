// MIT License
//
// SchemaBuilder.cs
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
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   Builds tool input schemas by hand as FLAT JSON-Schema objects (spec §3.2): a plain
    ///   <c>type:object</c> with sibling properties and an optional <c>required</c> list, a
    ///   <c>mode</c>/<c>op</c> discriminator expressed as a string <c>enum</c>, and no
    ///   <c>oneOf</c>/<c>anyOf</c>/<c>$ref</c> composition (mid-2026 client tool-selection
    ///   layers under-support those). The low-level handler path needs a <see cref="JsonElement"/>
    ///   schema; this is the one place that shape is produced, so a single guard test can pin it.
    /// </summary>
    public sealed class SchemaBuilder
    {
        private readonly JsonObject _properties = new();
        private readonly JsonArray _required = new();

        public static SchemaBuilder Create() => new();

        public SchemaBuilder Str(String name, String description, Boolean required = false, IEnumerable<String>? choices = null)
        {
            var prop = new JsonObject { ["type"] = "string", ["description"] = description };
            if (choices is not null)
            {
                var arr = new JsonArray();
                foreach (var c in choices)
                {
                    arr.Add(c);
                }
                prop["enum"] = arr;
            }
            return Add(name, prop, required);
        }

        public SchemaBuilder Int(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject { ["type"] = "integer", ["description"] = description }, required);
        }

        public SchemaBuilder Bool(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject { ["type"] = "boolean", ["description"] = description }, required);
        }

        /// <summary>A JSON-native scalar of any type (string/number/bool) — no <c>type</c>
        /// constraint, so it stays a flat property, not a <c>oneOf</c>. The bridge infers the
        /// Fallen-8 type from the JSON kind.</summary>
        public SchemaBuilder Any(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject { ["description"] = description }, required);
        }

        /// <summary>A free-form object map (e.g. properties: {key: value}).</summary>
        public SchemaBuilder Obj(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["additionalProperties"] = true,
            }, required);
        }

        /// <summary>An array of free-form objects (e.g. a batch of vertex/edge specs).</summary>
        public SchemaBuilder ObjArray(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
            }, required);
        }

        /// <summary>A numeric array (e.g. an embedding/query vector).</summary>
        public SchemaBuilder NumArray(String name, String description, Boolean required = false)
        {
            return Add(name, new JsonObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JsonObject { ["type"] = "number" },
            }, required);
        }

        public SchemaBuilder StrArray(String name, String description, Boolean required = false, IEnumerable<String>? itemChoices = null)
        {
            var items = new JsonObject { ["type"] = "string" };
            if (itemChoices is not null)
            {
                var arr = new JsonArray();
                foreach (var c in itemChoices)
                {
                    arr.Add(c);
                }
                items["enum"] = arr;
            }
            return Add(name, new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = items }, required);
        }

        private SchemaBuilder Add(String name, JsonObject property, Boolean required)
        {
            _properties[name] = property;
            if (required)
            {
                _required.Add(name);
            }
            return this;
        }

        public JsonElement Build()
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = _properties,
                ["additionalProperties"] = false,
            };
            if (_required.Count > 0)
            {
                schema["required"] = _required;
            }
            return JsonSerializer.SerializeToElement(schema);
        }

        /// <summary>The empty (no-argument) object schema.</summary>
        public static JsonElement Empty()
        {
            return JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            });
        }
    }
}
