// MIT License
//
// McpStatusDtoParityTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Mcp.Bridge.Dto;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The READ-side mirror of <see cref="McpWriteDtoParityTest"/> (feature mcp-plugin-gaps, spec
    ///   section 4). The bridge's <c>StatusDto</c> family is a hand-written projection of the apiApp's
    ///   <c>GET /status</c> body, and a field the DTO declares under a name the REST side never sends
    ///   binds to nothing, silently: <c>EmbeddingStateDto.Model</c>/<c>Dimensions</c> never bound
    ///   (the wire says <c>modelName</c>/<c>dimension</c>) and nothing noticed because nothing read
    ///   them. One direction only: the MCP side may be a SUBSET of the REST shape (it deliberately
    ///   does not model every field), but every name it declares must exist on the REST side.
    /// </summary>
    [TestClass]
    public class McpStatusDtoParityTest
    {
        [TestMethod]
        public void EveryStatusDtoField_ExistsOnItsRestShape_UnderTheSameJsonName()
        {
            var pairs = new (String Shape, Type RestType, Type McpType)[]
            {
                ("status", typeof(StatusREST), typeof(StatusDto)),
                ("index", typeof(IndexDescriptionREST), typeof(IndexDto)),
                ("embedding", typeof(EmbeddingProviderStatsREST), typeof(EmbeddingStateDto)),
                ("chat", typeof(ChatProviderStatsREST), typeof(ChatStateDto)),
                ("ingestion", typeof(IngestionStatsREST), typeof(IngestionStateDto)),
                ("docling", typeof(DoclingStatsREST), typeof(DoclingStateDto)),
                ("nlp", typeof(NlpStatsREST), typeof(NlpStateDto)),
            };

            var phantom = new List<String>();
            foreach (var (shape, restType, mcpType) in pairs)
            {
                var restNames = McpTestSupport.EffectiveJsonNames(restType);
                var mcpOnly = McpTestSupport.EffectiveJsonNames(mcpType).Except(restNames).OrderBy(n => n).ToList();
                if (mcpOnly.Count > 0)
                {
                    phantom.Add($"{shape}: {{{String.Join(", ", mcpOnly)}}}");
                }
            }

            Assert.AreEqual(0, phantom.Count,
                "the MCP status DTOs declare fields the REST /status body never sends, so they can never bind:\n"
                + String.Join("\n", phantom));
        }
    }
}
