// MIT License
//
// IngestionEntityTest.cs
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
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Ingestion;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature semantic-layer FR-6: NLP enrichment folds into the entity graph - deduplicated
    ///   Entity vertices, mentions edges chunk -&gt; entity (capped), key-term chunk properties,
    ///   the enriched flag, and the additive contract (NLP off / failing never fails the ingest).
    /// </summary>
    [TestClass]
    public class IngestionEntityTest
    {
        private static Dictionary<String, String> NlpOn() => new Dictionary<String, String>
        {
            { "Fallen8:Nlp:Enabled", "true" }
        };

        private static NlpEntity Entity(String text, String label) =>
            new NlpEntity { Text = text, Label = label, Start = 0, End = text.Length };

        private static List<VertexModel> Entities(Fallen8 engine) =>
            engine.GetAllVertices(DocumentGraphSchema.EntityLabel).ToList();

        [TestMethod]
        public async Task Enrich_CreatesEntityVertices_MentionsEdges_AndKeyTerms()
        {
            using var factory = new IngestionFactory(NlpOn());
            factory.Nlp.OnEnrich = _ => (
                new List<NlpEntity> { Entity("Muster GmbH", "ORG"), Entity("München", "LOC") },
                new List<String> { "checkout service", "payment gateway" });
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            await IngestionTestHelper.IngestText(client, "notes.md", "# H\n\nThe Muster GmbH in München.");

            var entities = Entities(engine);
            Assert.AreEqual(2, entities.Count, "one Entity vertex per distinct (type, normalized)");
            var byText = entities.ToDictionary(
                e => { e.TryGetProperty<String>(out var t, "text"); return t; });
            Assert.IsTrue(byText.ContainsKey("Muster GmbH"));
            Assert.IsTrue(byText["München"].TryGetProperty<String>(out var type, "type") && type == "LOC");

            // Each chunk mentions both entities.
            var chunk = engine.GetAllVertices(DocumentGraphSchema.ChunkLabel)[0];
            Assert.IsTrue(chunk.TryGetOutEdge(out var mentions, DocumentGraphSchema.MentionsEdge));
            Assert.AreEqual(2, mentions.Count);

            // Key terms landed on the chunk (newline-joined).
            Assert.IsTrue(chunk.TryGetProperty<String>(out var keyTerms, DocumentGraphSchema.KeyTermsProperty));
            StringAssert.Contains(keyTerms, "checkout service");
            StringAssert.Contains(keyTerms, "payment gateway");

            // The document records that enrichment ran.
            var document = engine.GetAllVertices(DocumentGraphSchema.DocumentLabel)[0];
            Assert.IsTrue(document.TryGetProperty<Boolean>(out var enriched, DocumentGraphSchema.EnrichedProperty) && enriched);
        }

        [TestMethod]
        public async Task Entities_AreDeduplicatedAcrossChunksAndDocuments()
        {
            using var factory = new IngestionFactory(NlpOn());
            // Every chunk yields the same entity (different surface case), so dedup must collapse
            // them to ONE vertex across chunks and across the two documents.
            factory.Nlp.OnEnrich = _ => (new List<NlpEntity> { Entity("Muster GmbH", "ORG") }, new List<String>());
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            await IngestionTestHelper.IngestText(client, "a.md", "# A\n\nfirst\n\n# B\n\nsecond");
            await IngestionTestHelper.IngestText(client, "b.md", "# C\n\nthird");

            Assert.AreEqual(1, Entities(engine).Count, "the same entity is one vertex per namespace");

            // Every chunk mentions it (2 + 1 chunks -> 3 mentions edges into the one entity).
            var entity = Entities(engine)[0];
            Assert.IsTrue(entity.TryGetInEdge(out var mentions, DocumentGraphSchema.MentionsEdge));
            Assert.AreEqual(3, mentions.Count);
        }

        [TestMethod]
        public async Task CaseInsensitiveDedup_ButKeepsFirstSurfaceForm()
        {
            using var factory = new IngestionFactory(NlpOn());
            var calls = 0;
            factory.Nlp.OnEnrich = _ =>
            {
                calls++;
                var surface = calls == 1 ? "Muster GmbH" : "muster gmbh";  // same entity, different case
                return (new List<NlpEntity> { Entity(surface, "ORG") }, new List<String>());
            };
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            await IngestionTestHelper.IngestText(client, "a.md", "one");
            await IngestionTestHelper.IngestText(client, "b.md", "two");

            var entities = Entities(engine);
            Assert.AreEqual(1, entities.Count, "case-insensitive dedup");
            Assert.IsTrue(entities[0].TryGetProperty<String>(out var text, "text"));
            Assert.AreEqual("Muster GmbH", text, "the first-seen surface form is kept");
        }

        [TestMethod]
        public async Task MentionsPerChunk_AreCapped()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Nlp:Enabled", "true" },
                { "Fallen8:Nlp:MaxEntitiesPerChunk", "2" }
            });
            factory.Nlp.OnEnrich = _ => (new List<NlpEntity>
            {
                Entity("Alpha", "ORG"), Entity("Beta", "ORG"), Entity("Gamma", "ORG"), Entity("Delta", "ORG")
            }, new List<String>());
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            await IngestionTestHelper.IngestText(client, "a.md", "one chunk");

            var chunk = engine.GetAllVertices(DocumentGraphSchema.ChunkLabel)[0];
            Assert.IsTrue(chunk.TryGetOutEdge(out var mentions, DocumentGraphSchema.MentionsEdge));
            Assert.AreEqual(2, mentions.Count, "mentions per chunk are capped");
        }

        [TestMethod]
        public async Task NlpDisabled_LeavesNoEntities_AndEnrichedFalse()
        {
            using var factory = new IngestionFactory();  // NLP off (default)
            factory.Nlp.OnEnrich = _ => (new List<NlpEntity> { Entity("Ignored", "ORG") }, new List<String>());
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            await IngestionTestHelper.IngestText(client, "a.md", "content");

            Assert.AreEqual(0, Entities(engine).Count, "NLP off: no entity network");
            var document = engine.GetAllVertices(DocumentGraphSchema.DocumentLabel)[0];
            Assert.IsTrue(document.TryGetProperty<Boolean>(out var enriched, DocumentGraphSchema.EnrichedProperty));
            Assert.IsFalse(enriched);
        }

        [TestMethod]
        public async Task ListEntities_RanksByMentionCount_AndFilters()
        {
            using var factory = new IngestionFactory(NlpOn());
            // Alpha is mentioned by both chunks, Beta by one: Alpha outranks Beta.
            var call = 0;
            factory.Nlp.OnEnrich = _ =>
            {
                call++;
                var entities = call == 1
                    ? new List<NlpEntity> { Entity("Alpha", "ORG"), Entity("Beta", "LOC") }
                    : new List<NlpEntity> { Entity("Alpha", "ORG") };
                return (entities, new List<String>());
            };
            using var client = factory.CreateClient();
            await IngestionTestHelper.EnsureBinding(client);

            await IngestionTestHelper.IngestText(client, "a.md", "# A\n\nfirst\n\n# B\n\nsecond");

            using var all = await client.GetAsync("/document/entities");
            Assert.AreEqual(HttpStatusCode.OK, all.StatusCode);
            var body = await IngestionTestHelper.ReadJson(all);
            Assert.AreEqual(2, body.GetProperty("total").GetInt32());
            var entities = body.GetProperty("entities");
            Assert.AreEqual("Alpha", entities[0].GetProperty("text").GetString(), "most-mentioned first");
            Assert.IsTrue(entities[0].GetProperty("mentionCount").GetInt32() >= entities[1].GetProperty("mentionCount").GetInt32());

            // Type filter.
            using var orgs = await client.GetAsync("/document/entities?type=ORG");
            var orgBody = await IngestionTestHelper.ReadJson(orgs);
            Assert.AreEqual(1, orgBody.GetProperty("total").GetInt32());
            Assert.AreEqual("Alpha", orgBody.GetProperty("entities")[0].GetProperty("text").GetString());

            // Substring filter (case-insensitive).
            using var beta = await client.GetAsync("/document/entities?contains=bet");
            var betaBody = await IngestionTestHelper.ReadJson(beta);
            Assert.AreEqual(1, betaBody.GetProperty("total").GetInt32());
            Assert.AreEqual("Beta", betaBody.GetProperty("entities")[0].GetProperty("text").GetString());
        }

        [TestMethod]
        public async Task NlpFailure_DoesNotFailTheIngest()
        {
            using var factory = new IngestionFactory(NlpOn());
            factory.Nlp.ThrowUnavailable = true;  // sidecar down mid-ingest
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            var summary = await IngestionTestHelper.IngestText(client, "a.md", "# H\n\ncontent here");

            Assert.AreEqual("indexed", summary.GetProperty("status").GetString(),
                "enrichment is additive: an NLP failure still indexes the document");
            Assert.AreEqual(0, Entities(engine).Count, "no entities when NLP failed");
            var document = engine.GetAllVertices(DocumentGraphSchema.DocumentLabel)[0];
            Assert.IsTrue(document.TryGetProperty<Boolean>(out var enriched, DocumentGraphSchema.EnrichedProperty));
            Assert.IsFalse(enriched, "enriched:false records that NLP did not run");
        }
    }
}
