// MIT License
//
// Fallen8IdentityTest.cs
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
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The resolved fleet identity (feature fleet-observability Phase 1). Identity is ON with
    ///   zero config: unset ids auto-fill and names fall back to ids, so every signal carries a
    ///   coherent tenant + instance without any operator setup.
    /// </summary>
    [TestClass]
    public class Fallen8IdentityTest
    {
        [TestMethod]
        public void UnsetConfig_AutoFills_DefaultTenantAndGeneratedInstance()
        {
            var identity = new Fallen8Identity(new Fallen8IdentityOptions());

            Assert.AreEqual("default", identity.TenantId, "an unset tenant id defaults to 'default'");
            Assert.AreEqual("default", identity.TenantName, "an unset tenant name falls back to the tenant id");
            Assert.IsTrue(identity.InstanceId.StartsWith("f8-", StringComparison.Ordinal),
                "an unset instance id auto-generates an f8- token");
            Assert.IsFalse(String.IsNullOrWhiteSpace(identity.InstanceId));
            Assert.AreEqual(identity.InstanceId, identity.InstanceName,
                "an unset instance name falls back to the instance id");
        }

        [TestMethod]
        public void ConfiguredValues_PassThrough_AndNamesFallBackToIds()
        {
            var identity = new Fallen8Identity(new Fallen8IdentityOptions
            {
                Tenant = new Fallen8IdentityOptions.IdentityRef { Id = "acme", Name = "Acme Corp" },
                Instance = new Fallen8IdentityOptions.IdentityRef { Id = "box-7" }, // name unset
            });

            Assert.AreEqual("acme", identity.TenantId);
            Assert.AreEqual("Acme Corp", identity.TenantName);
            Assert.AreEqual("box-7", identity.InstanceId);
            Assert.AreEqual("box-7", identity.InstanceName, "an unset name falls back to its id");
        }

        [TestMethod]
        public void ResourceAttributes_CarryAllFourDimensions_AndAreStable()
        {
            var identity = new Fallen8Identity(new Fallen8IdentityOptions());

            var first = identity.ResourceAttributes().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.AreEqual(identity.TenantId, first[Fallen8Identity.TenantIdKey]);
            Assert.AreEqual(identity.TenantName, first[Fallen8Identity.TenantNameKey]);
            Assert.AreEqual(identity.InstanceId, first[Fallen8Identity.InstanceIdKey]);
            Assert.AreEqual(identity.InstanceName, first[Fallen8Identity.InstanceNameKey]);

            // The resolved values are computed once (a resource attribute must be stable for the
            // process lifetime), so a second read returns the same auto-generated instance id.
            var second = identity.ResourceAttributes().ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.AreEqual(first[Fallen8Identity.InstanceIdKey], second[Fallen8Identity.InstanceIdKey]);
        }

        [TestMethod]
        public void BlankConfiguredValues_AreTreatedAsUnset()
        {
            var identity = new Fallen8Identity(new Fallen8IdentityOptions
            {
                Tenant = new Fallen8IdentityOptions.IdentityRef { Id = "   ", Name = "" },
                Instance = new Fallen8IdentityOptions.IdentityRef { Id = null, Name = "  " },
            });

            Assert.AreEqual("default", identity.TenantId);
            Assert.IsTrue(identity.InstanceId.StartsWith("f8-", StringComparison.Ordinal));
            Assert.AreEqual(identity.InstanceId, identity.InstanceName);
        }
    }
}
