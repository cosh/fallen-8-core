// MIT License
//
// NlAssistFeedbackSignalTest.cs
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
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// FL-1 of the nl-assist-feedback-loop feature: the /delegates/validate endpoint emits an
    /// aggregate, content-free compile signal (fallen8.delegate.validate{kind,result}) so an
    /// operator can watch first-pass compile rate per kind. The invariant under test is that it
    /// carries ONLY the canonical kind and valid/invalid - never the fragment or the NL intent.
    /// </summary>
    [TestClass]
    public class NlAssistFeedbackSignalTest
    {
        private const String Metric = "fallen8.delegate.validate";

        private sealed class Collector : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener();
            private readonly List<(String Instrument, Int64 Value, Dictionary<String, Object> Tags)> _m = new();
            private readonly Object _lock = new();

            public Collector()
            {
                _listener.InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "NoSQL.GraphDB.App")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<Int64>((instrument, value, tags, state) =>
                {
                    var map = new Dictionary<String, Object>();
                    foreach (var tag in tags)
                    {
                        map[tag.Key] = tag.Value;
                    }
                    lock (_lock)
                    {
                        _m.Add((instrument.Name, value, map));
                    }
                });
                _listener.Start();
            }

            public List<(String Instrument, Int64 Value, Dictionary<String, Object> Tags)> Of(String name)
            {
                lock (_lock)
                {
                    return _m.Where(x => x.Instrument == name).ToList();
                }
            }

            public void Dispose() => _listener.Dispose();
        }

        private static ActionResult<DelegateValidationREST> Validate(String kind, String fragment) =>
            new DelegatesController().ValidateDelegate(
                new ValidateDelegateSpecification { DelegateKind = kind, Fragment = fragment });

        [TestMethod]
        public void Validate_RecordsSignal_ByKindAndResult()
        {
            using var collector = new Collector();

            Validate("VertexFilter", "return (v) => v.Label == \"person\";");            // valid
            Validate("VertexFilter", "return (v) => v.ThisMemberDoesNotExist;");         // invalid (CS1061)
            Validate("EdgeFilter", "return (e) => e.Label == \"knows\";");               // valid

            var signal = collector.Of(Metric);
            Assert.AreEqual(3, signal.Count, "one measurement per known-kind validation");
            Assert.IsTrue(signal.All(x => x.Value == 1L), "each is a single increment");
            Assert.AreEqual(1, signal.Count(x => (String)x.Tags["kind"] == "VertexFilter" && (String)x.Tags["result"] == "valid"));
            Assert.AreEqual(1, signal.Count(x => (String)x.Tags["kind"] == "VertexFilter" && (String)x.Tags["result"] == "invalid"));
            Assert.AreEqual(1, signal.Count(x => (String)x.Tags["kind"] == "EdgeFilter" && (String)x.Tags["result"] == "valid"));
        }

        [TestMethod]
        public void Validate_TagIsCanonicalKind_NotCallerCasing()
        {
            using var collector = new Collector();

            Validate("vertexfilter", "return (v) => v.Label == \"person\";"); // lower-cased kind

            var only = collector.Of(Metric).Single();
            Assert.AreEqual("VertexFilter", (String)only.Tags["kind"],
                "the tag is the canonical kind, so casing variants don't inflate cardinality");
        }

        [TestMethod]
        public void Validate_UnknownKind_RecordsNothing()
        {
            using var collector = new Collector();

            var result = Validate("NotARealKind", "return (v) => true;");

            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult), "unknown kind is a 400");
            Assert.AreEqual(0, collector.Of(Metric).Count,
                "a malformed request is not a compile outcome and must not be counted");
        }

        [TestMethod]
        public void Signal_CarriesOnlyKindAndResult_NeverContent()
        {
            using var collector = new Collector();

            // A fragment whose text (property id "age", label "person") would be sensitive if leaked.
            Validate("GraphElementFilter",
                "return (ge) => ge.Label == \"person\" && ge.TryGetProperty(out int age, \"age\") && age > 30;");

            foreach (var measurement in collector.Of(Metric))
            {
                CollectionAssert.AreEquivalent(new[] { "kind", "result" }, measurement.Tags.Keys.ToArray(),
                    "the signal has exactly kind+result tags - never the fragment or the NL intent");
                var kind = (String)measurement.Tags["kind"];
                var resultTag = (String)measurement.Tags["result"];
                Assert.IsTrue(resultTag is "valid" or "invalid", "result is bounded");
                Assert.IsFalse(kind.Contains("age") || kind.Contains("person"), "no fragment content leaks into a tag");
            }
        }
    }
}
