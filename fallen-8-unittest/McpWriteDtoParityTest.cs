// MIT License
//
// McpWriteDtoParityTest.cs
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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Mcp.Bridge.Dto;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Field-parity guard for consolidation-audit CA-14. The REST PUT-body shapes
    ///   (<see cref="VertexSpecification"/>/<see cref="EdgeSpecification"/>/<see cref="PropertySpecification"/>)
    ///   and their MCP write-DTO mirrors (<c>VertexSpecDto</c>/<c>EdgeSpecDto</c>/<c>PropertySpecDto</c>)
    ///   are two independently maintained representations of the same wire contract - the MCP
    ///   bridge serializes its DTO and POSTs it to the REST endpoint. The existing MCP guards pin
    ///   route and method, not field shape, so a field added REST-side and missed on the MCP DTO
    ///   (as edge <c>Label</c> was during edge-type-vs-label) would silently drop that value on
    ///   every element created through <c>f8_mutate</c> while every contract test stayed green.
    ///
    ///   <para>This compares the EFFECTIVE JSON field names of each pair, computed exactly as each
    ///   side serializes at runtime: a <c>[JsonPropertyName]</c> value if present (the REST side
    ///   sets one on every field), else the camelCase of the CLR name (the MCP side has none and
    ///   serializes with <c>JsonSerializerDefaults.Web</c>). A rename on either side, or a field
    ///   added to one and not the other, breaks parity and fails here.</para>
    /// </summary>
    [TestClass]
    public class McpWriteDtoParityTest
    {
        // No field legitimately diverges between the REST body and its MCP mirror today. If a
        // future field must exist on only one side, add it here WITH a reason - so the divergence
        // is a conscious edit, not a silent drop. (name -> why it is allowed to be one-sided.)
        private static readonly IReadOnlyDictionary<string, string> AllowedRestOnly =
            new Dictionary<string, string>();
        private static readonly IReadOnlyDictionary<string, string> AllowedMcpOnly =
            new Dictionary<string, string>();

        [TestMethod]
        public void RestWriteBodies_AndTheirMcpMirrors_HaveIdenticalJsonFieldNames()
        {
            var pairs = new (string Shape, Type RestType, Type McpType)[]
            {
                ("vertex", typeof(VertexSpecification), typeof(VertexSpecDto)),
                ("edge", typeof(EdgeSpecification), typeof(EdgeSpecDto)),
                ("property", typeof(PropertySpecification), typeof(PropertySpecDto)),
            };

            var mismatches = new List<string>();

            foreach (var (shape, restType, mcpType) in pairs)
            {
                var restNames = EffectiveJsonNames(restType);
                var mcpNames = EffectiveJsonNames(mcpType);

                var restOnly = restNames.Except(mcpNames).Where(n => !AllowedRestOnly.ContainsKey(n)).OrderBy(n => n).ToList();
                var mcpOnly = mcpNames.Except(restNames).Where(n => !AllowedMcpOnly.ContainsKey(n)).OrderBy(n => n).ToList();

                if (restOnly.Count > 0 || mcpOnly.Count > 0)
                {
                    mismatches.Add(String.Format(
                        "{0}: REST-only={{{1}}} MCP-only={{{2}}}",
                        shape, String.Join(", ", restOnly), String.Join(", ", mcpOnly)));
                }
            }

            Assert.AreEqual(0, mismatches.Count,
                "REST write-body shapes and their MCP write-DTO mirrors have drifted (CA-14). "
                + "Add the missing field to the lagging side, or allow-list a deliberate divergence with a reason:\n"
                + String.Join("\n", mismatches));
        }

        // The wire name each property serializes to: an explicit [JsonPropertyName] wins; otherwise
        // the camelCase of the CLR name (JsonSerializerDefaults.Web behaviour, which the MCP bridge
        // uses and which the REST options also apply to any unattributed field).
        private static HashSet<string> EffectiveJsonNames(Type type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
                names.Add(attribute != null
                    ? attribute.Name
                    : JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            }
            return names;
        }
    }
}
