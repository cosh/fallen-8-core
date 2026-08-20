// MIT License
//
// EmbeddingBatchOrderTest.cs
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Batching an embedding run over a CAPPED backend (feature nahil-backend): a remote
    ///   remote backend enforces a per-request item cap, so a long document is embedded in several requests
    ///   instead of one - and the vector each chunk ends up with must still be the vector of THAT
    ///   chunk.
    ///
    ///   <para>Order is the property worth a test of its own because getting it wrong is invisible:
    ///   every chunk still gets a vector of the right width and the run still reports success, but
    ///   semantic search silently returns the neighbours of the wrong text. Nothing downstream can
    ///   detect it.</para>
    /// </summary>
    [TestClass]
    public class EmbeddingBatchOrderTest
    {
        private const Int32 BatchCap = 4;

        /// <summary>
        ///   A host whose chunker splits per paragraph and whose backend caps every request, so a
        ///   modest document genuinely needs several. Without the small ChunkMaxChars the whole
        ///   document merges into ONE chunk and the batching this exercises never happens - which is
        ///   how a first version of this test passed while proving nothing.
        /// </summary>
        private static Dictionary<String, String> ChunkedSmall()
        {
            return new Dictionary<String, String>
            {
                ["Fallen8:Embedding:MaxBatchSize"] = BatchCap.ToString(),
                ["Fallen8:Ingestion:ChunkMaxChars"] = "80"
            };
        }

        /// <summary>Enough distinct paragraphs to need several capped requests.</summary>
        private static String ManyParagraphs(Int32 count)
        {
            var text = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                // Distinct content per chunk: the fake generator's vector is a function of the text,
                // so identical paragraphs would make a mis-ordering undetectable.
                text.Append("Paragraph number ").Append(i).Append(" about turbine ")
                    .Append((Char)('A' + (i % 26))).Append(" and its gearbox history.\n\n");
            }

            return text.ToString();
        }

        [TestMethod]
        public async Task ALongDocument_IsEmbeddedInCappedBatches_AndEveryChunkKeepsItsOwnVector()
        {
            using var factory = new IngestionFactory(ChunkedSmall());
            using var client = factory.CreateClient();

            await IngestionTestHelper.IngestText(client, "turbines.txt", ManyParagraphs(30));

            var engine = IngestionTestHelper.EngineOf(factory);
            var chunks = engine.GetAllVertices(DocumentGraphSchema.ChunkLabel);
            Assert.IsTrue(chunks.Count > BatchCap,
                "the document must need more than one request or this proves nothing; got " + chunks.Count);

            // The cap is enforced on EVERY request, not just the first.
            var batches = factory.Embeddings.BatchSizes.ToList();
            Assert.AreEqual((Int32)Math.Ceiling(chunks.Count / (Double)BatchCap), batches.Count,
                "one request per capped batch: " + String.Join(",", batches));
            foreach (var size in batches)
            {
                Assert.IsTrue(size >= 1 && size <= BatchCap,
                    "a request of " + size + " exceeds the cap of " + BatchCap);
            }

            Assert.AreEqual(chunks.Count, batches.Sum(), "every chunk was embedded exactly once");

            // And the reassembly kept each vector with its own text. Compared against the fake's
            // pure function of the chunk's OWN text, so an off-by-one batch offset fails here.
            foreach (var chunk in chunks)
            {
                Assert.IsTrue(chunk.TryGetProperty<String>(out var text, DocumentGraphSchema.TextProperty),
                    "every chunk carries its text");
                Assert.IsTrue(chunk.TryGetEmbedding(out var stored),
                    "every chunk carries an embedding");

                var expected = FakeEmbeddingGenerator.VectorFor(text, IngestionFactory.Dim);
                CollectionAssert.AreEqual(expected, stored.ToArray(),
                    "chunk " + chunk.Id + " got the vector of a different chunk");
            }
        }

        /// <summary>
        ///   When a batch part-way through a long run fails, the report says WHERE. A remote backend
        ///   can refuse mid-run (a spent hourly token budget, a model evicted between requests), and
        ///   the provider's own message says only that the backend was unusable - which leaves an
        ///   operator unable to tell a document that failed on its first batch from one that failed
        ///   on its last.
        /// </summary>
        [TestMethod]
        public async Task ABatchThatFailsPartWayThrough_NamesTheChunksItDidNotEmbed()
        {
            using var factory = new IngestionFactory(ChunkedSmall());
            using var client = factory.CreateClient();

            // Two batches succeed, the third refuses - the shape of a spent hourly token budget
            // rather than a backend that was never reachable.
            factory.Embeddings.RefuseFromCall = 3;

            var documentId = await IngestionTestHelper.PostText(client, "turbines.txt", ManyParagraphs(30));
            var summary = await IngestionTestHelper.AwaitTerminal(client, documentId);

            Assert.AreEqual("failed", summary.GetProperty("status").GetString(), summary.ToString());
            var error = summary.GetProperty("error").GetString();

            // The provider's own reason is kept AND located: which chunks did not make it, and how
            // many already had. Without the second half, a document that died on its last batch reads
            // exactly like one that never started.
            StringAssert.Contains(error, "the hourly token budget for this key is spent", error);
            StringAssert.Contains(error, "were not embedded", error);
            StringAssert.Contains(error, "Chunks 9-", error);
            StringAssert.Contains(error, "8 already were", error);

            // The invariant holds through the failure: nothing is written until every chunk has a
            // vector, so the document is re-runnable rather than half-indexed.
            var engine = IngestionTestHelper.EngineOf(factory);
            Assert.AreEqual(0, engine.GetAllVertices(DocumentGraphSchema.ChunkLabel).Count,
                "a failed embed leaves no half-indexed document behind");
        }

        /// <summary>
        ///   A backend that returns the wrong WIDTH is a configuration fault, and it is refused rather
        ///   than stored. Pinned with its message because the number in it is the whole diagnosis: a
        ///   backend serving a different model than the one the stored vectors came from shows up
        ///   exactly here, and nowhere else.
        /// </summary>
        [TestMethod]
        public async Task AWrongWidthVector_IsRefusedWithBothDimensionsNamed()
        {
            var provider = new Fallen8EmbeddingProvider(
                Microsoft.Extensions.Options.Options.Create(new Fallen8EmbeddingOptions
                {
                    Enabled = true,
                    Backend = "Nahil",
                    ModelName = "bge-m3",
                    Dimension = 1024
                }),
                new Lazy<Microsoft.Extensions.AI.IEmbeddingGenerator<String, Microsoft.Extensions.AI.Embedding<Single>>>(
                    () => new FakeEmbeddingGenerator(768)));

            var mismatch = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, CancellationToken.None));

            StringAssert.Contains(mismatch.Message, "768", mismatch.Message);
            StringAssert.Contains(mismatch.Message, "1024", mismatch.Message);
            StringAssert.Contains(mismatch.Message, "never truncated or padded",
                "the operator must be told the value was refused, not adjusted");

            // Latched: the fault is in the configuration, so every later call fails the same way
            // instead of re-asking a backend whose answer cannot become correct.
            var again = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "y" }, CancellationToken.None));
            StringAssert.Contains(again.Message, "1024");
        }
    }
}
