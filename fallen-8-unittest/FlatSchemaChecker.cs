// MIT License
//
// FlatSchemaChecker.cs
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
using System.Linq;
using System.Text.Json;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Validates a tool-call argument object against the FLAT JSON-Schema subset
    ///   <c>SchemaBuilder</c> can produce: an object with typed sibling properties, a
    ///   <c>required</c> list, <c>additionalProperties:false</c>, string <c>enum</c>s, array
    ///   <c>items.type</c>, and the untyped scalar (<c>Any</c>). Deliberately not a JSON Schema
    ///   implementation: the repo controls the producer, and
    ///   <c>McpToolSurfaceTest.Overview_Schema_IsFlatEnumDiscriminated_NoComposition</c> pins that
    ///   no composition keyword ever appears, so a validator for this subset is a validator for every
    ///   schema the server advertises. Returns the violations rather than a boolean, so a failing test
    ///   says what was wrong.
    /// </summary>
    internal static class FlatSchemaChecker
    {
        internal static IReadOnlyList<String> Violations(JsonElement schema, JsonElement arguments)
        {
            var violations = new List<String>();
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                violations.Add("arguments must be an object");
                return violations;
            }

            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            var additionalAllowed = !schema.TryGetProperty("additionalProperties", out var ap) || ap.ValueKind != JsonValueKind.False;

            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    if (!arguments.TryGetProperty(name, out _))
                    {
                        violations.Add($"required argument '{name}' is missing");
                    }
                }
            }

            foreach (var argument in arguments.EnumerateObject())
            {
                if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(argument.Name, out var declared))
                {
                    if (!additionalAllowed)
                    {
                        violations.Add($"argument '{argument.Name}' is not declared and additionalProperties is false");
                    }
                    continue;
                }
                CheckValue(argument.Name, declared, argument.Value, violations);
            }

            return violations;
        }

        private static void CheckValue(String path, JsonElement declared, JsonElement value, List<String> violations)
        {
            if (!declared.TryGetProperty("type", out var typeElement))
            {
                return; // the untyped scalar: any JSON kind is acceptable
            }
            var type = typeElement.GetString();
            var ok = type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                _ => false,
            };
            if (!ok)
            {
                violations.Add($"'{path}' must be {type} but is {value.ValueKind}");
                return;
            }

            if (declared.TryGetProperty("enum", out var choices))
            {
                var allowed = choices.EnumerateArray().Select(c => c.GetString()).ToHashSet(StringComparer.Ordinal);
                if (!allowed.Contains(value.GetString()))
                {
                    violations.Add($"'{path}' is '{value.GetString()}', not one of [{String.Join(", ", allowed)}]");
                }
            }

            if (type == "array" && declared.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    CheckValue($"{path}[{index}]", items, item, violations);
                    index++;
                }
            }
        }
    }
}
