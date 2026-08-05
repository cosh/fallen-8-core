// MIT License
//
// AuditDefectMessagesTest.cs
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
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit defects B13 and B33: two refusals that named the wrong cause. The bulk export's 422
    ///   blamed the type allow-list for every rejected property, including an allow-listed
    ///   String/Char carrying an unpaired surrogate; subscribing to a DISPOSED change feed was
    ///   reported as "subscriber limit reached" although no limit was hit. These tests pin that each
    ///   cause is now told apart from the others, so an operator is never sent after the wrong knob.
    /// </summary>
    [TestClass]
    public class AuditDefectMessagesTest
    {
        #region B13: the export 422 names the actual rejection cause

        private sealed class ExportFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        /// <summary>The pieces of the 422 problem body a caller diagnoses from.</summary>
        private sealed class ExportRefusal
        {
            public HttpStatusCode Status;
            public String MediaType;
            public String PropertyKey;
            public String Detail;
        }

        /// <summary>
        ///   Seeds ONE vertex carrying property "p" with <paramref name="propertyValue"/> straight
        ///   on the engine (the REST write path cannot create these values at all), then exports.
        /// </summary>
        private static async Task<ExportRefusal> ExportWithProperty(Object propertyValue)
        {
            using var factory = new ExportFactory();
            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;

            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new Dictionary<String, Object> { { "p", propertyValue } });
            engine.EnqueueTransaction(vtx).WaitUntilFinished();

            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/bulk/export");
            var body = await response.Content.ReadAsStringAsync();

            var refusal = new ExportRefusal
            {
                Status = response.StatusCode,
                MediaType = response.Content.Headers.ContentType?.MediaType,
                Detail = body
            };

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                using var problem = JsonDocument.Parse(body);
                refusal.PropertyKey = problem.RootElement.GetProperty("propertyKey").GetString();
                refusal.Detail = problem.RootElement.GetProperty("detail").GetString();
            }

            return refusal;
        }

        private static void AssertRefused(ExportRefusal refusal)
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refusal.Status, refusal.Detail);
            Assert.AreEqual("application/problem+json", refusal.MediaType,
                "the failure is a problem body, never a half-written NDJSON file");
            Assert.AreEqual("p", refusal.PropertyKey, "the offending property is still pinpointed");
        }

        [TestMethod]
        public async Task Export_422_NullValue_IsAttributedToTheNullValue()
        {
            var refusal = await ExportWithProperty(null);

            AssertRefused(refusal);
            StringAssert.Contains(refusal.Detail, "its value is null", refusal.Detail);
            Assert.IsFalse(refusal.Detail.Contains("allow-list"),
                "a null value has no runtime type, so the allow-list must not be blamed");
        }

        [TestMethod]
        public async Task Export_422_NonAllowListedType_NamesTheTypeAndTheAllowList()
        {
            var refusal = await ExportWithProperty(new Int32[] { 1, 2, 3 });

            AssertRefused(refusal);
            StringAssert.Contains(refusal.Detail, "System.Int32[]", refusal.Detail);
            StringAssert.Contains(refusal.Detail, "outside the exportable allow-list", refusal.Detail);
            Assert.IsFalse(refusal.Detail.Contains("null"),
                "the value is present, so nullness must not be offered as the cause");
        }

        [TestMethod]
        public async Task Export_422_UnpairedSurrogateInAString_IsNotBlamedOnTheAllowList()
        {
            // System.String IS allow-listed: the refusal happens AFTER that check passes, so the
            // old wording ("a type outside the exportable allow-list") was simply false here.
            var refusal = await ExportWithProperty("bad\ud800tail");

            AssertRefused(refusal);
            StringAssert.Contains(refusal.Detail, "unpaired surrogate", refusal.Detail);
            StringAssert.Contains(refusal.Detail, "invalid UTF-16", refusal.Detail);
            StringAssert.Contains(refusal.Detail, "String", refusal.Detail);
            Assert.IsFalse(refusal.Detail.Contains("allow-list"),
                "System.String is allow-listed; blaming the allow-list sends the operator hunting an exotic type");
            Assert.IsFalse(refusal.Detail.Contains("null"), refusal.Detail);
        }

        [TestMethod]
        public async Task Export_422_UnpairedSurrogateChar_IsNotBlamedOnTheAllowList()
        {
            var refusal = await ExportWithProperty('\ud800');

            AssertRefused(refusal);
            StringAssert.Contains(refusal.Detail, "unpaired surrogate", refusal.Detail);
            StringAssert.Contains(refusal.Detail, "Char", refusal.Detail);
            Assert.IsFalse(refusal.Detail.Contains("allow-list"),
                "System.Char is allow-listed too");
        }

        [TestMethod]
        public void TryFormatValue_ClassifiesEachRejection_AndSaysNothingOnSuccess()
        {
            // The single home of the classification: one distinct reason per false, null on true.
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(null, out _, out _, out var nullReason));
            StringAssert.Contains(nullReason, "null", nullReason);

            Assert.IsFalse(JsonlGraphFormat.TryFormatValue(new Object(), out _, out _, out var typeReason));
            StringAssert.Contains(typeReason, "System.Object", typeReason);
            StringAssert.Contains(typeReason, "allow-list", typeReason);

            Assert.IsFalse(JsonlGraphFormat.TryFormatValue('\ud800', out _, out _, out var charReason));
            StringAssert.Contains(charReason, "unpaired surrogate", charReason);
            Assert.IsFalse(charReason.Contains("allow-list"), charReason);

            Assert.IsFalse(JsonlGraphFormat.TryFormatValue("\udc00head", out _, out _, out var lowReason));
            StringAssert.Contains(lowReason, "unpaired surrogate", lowReason);
            Assert.IsFalse(lowReason.Contains("allow-list"), lowReason);

            // Every cause reads differently: a caller can act on the message.
            CollectionAssert.AllItemsAreUnique(new[] { nullReason, typeReason, charReason });

            // Success says nothing at all. A WELL-FORMED surrogate pair (U+1F600, written as
            // escapes so this source file stays ASCII) must pass: the check refuses unpaired
            // surrogates, never astral characters. Same for the one non-scalar type.
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue("ok \ud83d\ude00 pair", out _, out _, out var okReason));
            Assert.IsNull(okReason);
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue(new Single[] { 1f, 2f }, out _, out _, out var vectorReason));
            Assert.IsNull(vectorReason);

            // The three-argument overload keeps behaving exactly as before (the streaming hot path).
            Assert.IsFalse(JsonlGraphFormat.TryFormatValue("bad\ud800tail", out _, out _));
            Assert.IsTrue(JsonlGraphFormat.TryFormatValue("plain", out var typeName, out var formatted));
            Assert.AreEqual("System.String", typeName);
            Assert.AreEqual("plain", formatted);
        }

        #endregion

        #region B33: a disposed change feed is not a subscriber-limit refusal

        [TestMethod]
        public void TrySubscribe_AfterFeedDispose_FailsForDisposal_NotTheSubscriberLimit()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var engine = new Fallen8(loggerFactory, new ChangeFeedOptions { MaxSubscribers = 32 });

            // The in-flight request's captured reference: the engine nulls its own property on
            // dispose, the dispatcher instance the request already holds stays reachable.
            var feed = engine.ChangeFeed;
            Assert.IsFalse(feed.IsDisposed, "a live feed is not disposed");

            engine.Dispose();

            Assert.IsTrue(feed.IsDisposed, "the dropped/shut-down engine disposed its feed");
            Assert.AreEqual(0, feed.SubscriberCount,
                "no subscriber is registered, so the limit demonstrably is NOT the cause");
            Assert.IsFalse(feed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription),
                "a disposed feed accepts nobody");
            Assert.IsNull(subscription);
        }

        [TestMethod]
        public void TrySubscribe_AtTheSubscriberLimit_IsNotReportedAsDisposal()
        {
            var loggerFactory = TestLoggerFactory.Create();
            using var engine = new Fallen8(loggerFactory, new ChangeFeedOptions { MaxSubscribers = 1 });
            var feed = engine.ChangeFeed;

            Assert.IsTrue(feed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var first));

            Assert.IsFalse(feed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out _),
                "the second subscriber exceeds MaxSubscribers");
            Assert.IsFalse(feed.IsDisposed, "the limit refusal must not read as a shut-down feed");
            Assert.AreEqual(feed.Options.MaxSubscribers, feed.SubscriberCount,
                "the limit is genuinely reached: that IS the cause here");

            // Freeing the slot lifts the refusal, and the feed was never disposed.
            first.Dispose();
            Assert.IsTrue(feed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var second));
            Assert.IsFalse(feed.IsDisposed);
            second.Dispose();
        }

        [TestMethod]
        public void FeedIsDisposed_IsIdempotentAcrossRepeatedDisposal()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var feed = new ChangeFeedDispatcher(new ChangeFeedOptions(),
                loggerFactory.CreateLogger<ChangeFeedDispatcher>());

            Assert.IsFalse(feed.IsDisposed);

            feed.Dispose();
            Assert.IsTrue(feed.IsDisposed);

            // Dispose is documented as idempotent; the flag must not flip back.
            feed.Dispose();
            Assert.IsTrue(feed.IsDisposed);
        }

        #endregion
    }
}
