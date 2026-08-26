// MIT License
//
// SensitiveRequestBodyLimitTest.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The request-body cap on the sensitive (code/plugin) endpoints.
    ///
    /// <c>Fallen8:Security:MaxSensitiveRequestBodyBytes</c> was bound but read nowhere while its
    /// XML doc promised a 413, so an operator could believe they had raised or tightened the
    /// code-endpoint body cap. The knob is gone; the effective cap is the compile-time
    /// <c>[RequestSizeLimit(1_048_576)]</c> on each sensitive action, which these tests read out of the
    /// action's metadata. An old config file that still carries the removed key must keep binding.
    ///
    /// That the options class exposes no such knob, and cannot grow one back under a new name, is
    /// pinned by <see cref="SettingCatalogTest.SecurityOptions_ExposeNoRequestBodyKnob"/>.
    /// </summary>
    [TestClass]
    public class SensitiveRequestBodyLimitTest
    {
        /// <summary>The cap every sensitive (code/plugin) action carries as a compile-time literal.</summary>
        private const Int64 SensitiveBodyLimitBytes = 1_048_576;

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
    }
}
