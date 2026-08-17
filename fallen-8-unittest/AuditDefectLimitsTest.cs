// MIT License
//
// AuditDefectLimitsTest.cs
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
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the two request-limit defects of the configuration audit.
    ///
    /// B21: <c>Fallen8:Security:MaxSensitiveRequestBodyBytes</c> was bound but read nowhere while its
    /// XML doc promised a 413, so an operator could believe they had raised or tightened the
    /// code-endpoint body cap. The knob is gone; the effective cap is the compile-time
    /// <c>[RequestSizeLimit(1_048_576)]</c> on each sensitive action, which these tests read out of the
    /// action's metadata. An old config file that still carries the removed key must keep binding.
    ///
    /// B38: <c>GET /ns/{ns}/benchmark</c> accepted any positive iteration count, and one pass saturates every
    /// core. It is now bounded by <see cref="Fallen8SecurityOptions.BenchmarkMaxIterations"/> (400 above
    /// the ceiling, the omitted-count default clamped to it), following the ceiling that analytics puts
    /// on its own iterations.
    /// </summary>
    [TestClass]
    public class AuditDefectLimitsTest
    {
        /// <summary>The cap every sensitive (code/plugin) action carries as a compile-time literal.</summary>
        private const Int64 SensitiveBodyLimitBytes = 1_048_576;

        private sealed class LimitsFactory : WebApplicationFactory<Program>
        {
            private readonly IDictionary<String, String> _settings;

            public LimitsFactory(IDictionary<String, String> settings = null)
            {
                _settings = settings;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                // Volatile durability: booting the host writes no checkpoint/WAL into the test bin.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");

                if (_settings == null)
                {
                    return;
                }

                foreach (var setting in _settings)
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }
            }
        }

        #region helpers

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>
        ///   The addressed namespace: generation and the benchmark are the two scoped routes with NO
        ///   bare alias to "default" (feature graph-namespaces), and their bare form is refused with
        ///   a 400 - the same status the ceiling assertions below expect, so a bare URL here would
        ///   make them pass for the wrong reason.
        /// </summary>
        private const String Ns = "/ns/default";

        /// <summary>Gives the addressed namespace a small graph, so the benchmark has edges to follow.</summary>
        private static async Task Generate(HttpClient client)
        {
            using var response = await client.GetAsync(Ns + "/generate?nodeCount=20&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET " + Ns + "/generate");
        }

        /// <summary>
        /// The bytes argument of the action's <see cref="RequestSizeLimitAttribute"/>, read from
        /// metadata rather than a property so the assertion does not depend on the attribute exposing
        /// its constructor argument.
        /// </summary>
        private static Int64 RequestSizeLimitOf(Type controller, String action)
        {
            var method = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(method, action + " must exist on " + controller.Name);

            var attribute = CustomAttributeData.GetCustomAttributes(method)
                .SingleOrDefault(data => data.AttributeType == typeof(RequestSizeLimitAttribute));
            Assert.IsNotNull(attribute, action + " must carry [RequestSizeLimit]");
            Assert.AreEqual(1, attribute.ConstructorArguments.Count);

            return Convert.ToInt64(attribute.ConstructorArguments[0].Value);
        }

        #endregion

        #region B21 - the dead body-size knob

        [TestMethod]
        public void SecurityOptions_ExposeNoRequestBodyKnob()
        {
            var properties = typeof(Fallen8SecurityOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            CollectionAssert.DoesNotContain(properties, "MaxSensitiveRequestBodyBytes",
                "the option promised a 413 it never enforced and was removed");

            // Guard against the same lie coming back under a new name: the body cap is not
            // configurable at all, it is the attribute asserted below.
            foreach (var name in properties)
            {
                Assert.IsFalse(name.IndexOf("RequestBody", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               name.IndexOf("BodyBytes", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Fallen8SecurityOptions must not advertise a request-body cap it cannot enforce: " + name);
            }

            // The knobs that ARE read stay untouched.
            Assert.AreEqual(30, new Fallen8SecurityOptions().SensitiveRateLimitPermitPerWindow);
            Assert.AreEqual(10, new Fallen8SecurityOptions().RateLimitWindowSeconds);
        }

        [TestMethod]
        public void SensitiveActions_CarryTheFixedOneMebibyteBodyLimit()
        {
            Assert.AreEqual(SensitiveBodyLimitBytes,
                RequestSizeLimitOf(typeof(StoredQueriesController), "RegisterStoredQuery"));
            Assert.AreEqual(SensitiveBodyLimitBytes,
                RequestSizeLimitOf(typeof(DelegatesController), "ValidateDelegate"));
        }

        [TestMethod]
        public void ConfigurationStillCarryingTheRemovedKey_BindsWithoutError()
        {
            // Options binding ignores unknown keys: an existing appsettings.json / environment that
            // still sets the removed key keeps working, it just has no effect (which is what it
            // always had).
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Fallen8:Security:MaxSensitiveRequestBodyBytes"] = "104857600",
                    ["Fallen8:Security:ApiKeyHeader"] = "X-Custom-Key",
                    ["Fallen8:Security:BenchmarkMaxIterations"] = "42"
                })
                .Build();

            var options = new Fallen8SecurityOptions();
            configuration.GetSection(Fallen8SecurityOptions.SectionName).Bind(options);

            Assert.AreEqual("X-Custom-Key", options.ApiKeyHeader, "the neighbouring keys still bind");
            Assert.AreEqual(42, options.BenchmarkMaxIterations);
        }

        #endregion

        #region B38 - the unbounded benchmark

        [TestMethod]
        public void BenchmarkCeiling_DefaultsToTenThousand_AndRejectsNonPositiveConfiguration()
        {
            Assert.AreEqual(10000, new Fallen8SecurityOptions().BenchmarkMaxIterations);

            // Same guard as the analytics options: a 0 or negative in configuration would otherwise
            // reject every request, so it resets to the default.
            Assert.AreEqual(10000, new Fallen8SecurityOptions { BenchmarkMaxIterations = 0 }.BenchmarkMaxIterations);
            Assert.AreEqual(10000, new Fallen8SecurityOptions { BenchmarkMaxIterations = -5 }.BenchmarkMaxIterations);
            Assert.AreEqual(7, new Fallen8SecurityOptions { BenchmarkMaxIterations = 7 }.BenchmarkMaxIterations);
        }

        [TestMethod]
        public async Task Benchmark_AboveTheConfiguredCeiling_Returns400_NamingTheKeyAndTheCeiling()
        {
            using var factory = new LimitsFactory(new Dictionary<String, String>
            {
                ["Fallen8:Security:BenchmarkMaxIterations"] = "3"
            });
            using var client = factory.CreateClient();
            await Generate(client);

            using var tooMany = await client.GetAsync(Ns + "/benchmark?iterations=4");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.AreEqual("application/problem+json", tooMany.Content.Headers.ContentType?.MediaType);
            var detail = (await ReadJson(tooMany)).GetProperty("detail").GetString();
            StringAssert.Contains(detail, "3", "the ceiling is named so the caller can lower the count");
            StringAssert.Contains(detail, "Fallen8:Security:BenchmarkMaxIterations",
                "the caller is told which key raises it");

            // The boundary itself runs.
            using var atCeiling = await client.GetAsync(Ns + "/benchmark?iterations=3");
            Assert.AreEqual(HttpStatusCode.OK, atCeiling.StatusCode);
            Assert.AreEqual(3, (await ReadJson(atCeiling)).GetProperty("iterations").GetInt32(),
                "an accepted count is run and echoed, never silently clamped");
        }

        [TestMethod]
        public async Task Benchmark_WithoutIterations_ClampsTheDefaultToTheCeiling()
        {
            // The endpoint default is 1000; with a lower ceiling a request that names NO count must
            // still succeed (it never asked for the rejected value) and report what it ran.
            using var factory = new LimitsFactory(new Dictionary<String, String>
            {
                ["Fallen8:Security:BenchmarkMaxIterations"] = "2"
            });
            using var client = factory.CreateClient();
            await Generate(client);

            using var response = await client.GetAsync(Ns + "/benchmark");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, (await ReadJson(response)).GetProperty("iterations").GetInt32());
        }

        [TestMethod]
        public async Task Benchmark_UnconfiguredCeiling_RejectsTheMistypedExtraZero_AndKeepsTheOldFourHundreds()
        {
            using var factory = new LimitsFactory();
            using var client = factory.CreateClient();
            await Generate(client);

            // The pre-fix footgun: this used to start a pass nothing could interrupt.
            using var tooMany = await client.GetAsync(Ns + "/benchmark?iterations=10001");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.AreEqual("application/problem+json", tooMany.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains((await ReadJson(tooMany)).GetProperty("detail").GetString(), "10000");

            // Unchanged: the ceiling check is additional, the existing 400s still answer first.
            using var zero = await client.GetAsync(Ns + "/benchmark?iterations=0");
            Assert.AreEqual(HttpStatusCode.BadRequest, zero.StatusCode);
            StringAssert.Contains((await ReadJson(zero)).GetProperty("detail").GetString(), "greater than 0");

            using var garbage = await client.GetAsync(Ns + "/benchmark?iterations=abc");
            Assert.AreEqual(HttpStatusCode.BadRequest, garbage.StatusCode);
            StringAssert.Contains((await ReadJson(garbage)).GetProperty("detail").GetString(), "not a valid");

            // And a normal small run is unaffected.
            using var ok = await client.GetAsync(Ns + "/benchmark?iterations=2");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
            Assert.AreEqual(2, (await ReadJson(ok)).GetProperty("iterations").GetInt32());
        }

        #endregion
    }
}
