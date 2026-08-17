// MIT License
//
// NamespaceResidencyConventionTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Convention test for the ONE exemption from the not-loaded 503 (feature
    ///   namespace-startup-load, spec §4.7). <see cref="NamespaceResidencyOptionalAttribute"/> waives
    ///   the pre-action refusal in <see cref="NamespaceValidationFilter"/>, so every action carrying
    ///   it must branch on residency itself. That reasoning was done once, for the anonymous
    ///   connection probe; a second bearer added without it would answer over a namespace this
    ///   process has no engine for. Hence a pinned SET rather than a spot check.
    /// </summary>
    [TestClass]
    public class NamespaceResidencyConventionTest
    {
        /// <summary>The exemption's full inventory, as "Controller.Action".</summary>
        private static readonly String[] ExpectedBearers = new[] { "AdminController.Status" };

        [TestMethod]
        public void ResidencyOptional_IsCarriedByExactlyTheStatusProbe()
        {
            var bearers = new List<String>();
            foreach (var controller in typeof(AdminController).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
            {
                // inherit: false - the attribute is not inheritable, and reading it that way is part
                // of what this test pins.
                foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttributes(typeof(NamespaceResidencyOptionalAttribute), inherit: false).Length > 0))
                {
                    bearers.Add(controller.Name + "." + action.Name);
                }
            }

            CollectionAssert.AreEquivalent(ExpectedBearers, bearers,
                "an action exempt from the not-loaded 503 must handle residency itself; add it here only " +
                "together with that branch (and never as a whole controller)");
        }

        [TestMethod]
        public void ResidencyOptional_TheExemptAction_IsTheAnonymousStatusProbeAndReadsResidency()
        {
            var status = typeof(AdminController).GetMethod(nameof(AdminController.Status));

            var route = status.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
                .Cast<HttpGetAttribute>().Single();
            Assert.AreEqual("/status", route.Template,
                "the exemption exists for the connection probe specifically, not for whatever /status becomes");
            Assert.AreEqual(1, status.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: false).Length,
                "the reason a 503 here is unacceptable is that this is the ANONYMOUS probe every client calls first");
        }

        [TestMethod]
        public void ResidencyOptional_CannotBeAppliedToAControllerOrInherited()
        {
            var usage = typeof(NamespaceResidencyOptionalAttribute)
                .GetCustomAttributes(typeof(AttributeUsageAttribute), inherit: false)
                .Cast<AttributeUsageAttribute>().Single();

            // A class-level or inherited exemption would waive the refusal for actions that never
            // reasoned about it - the silent way a data route starts answering with no engine.
            Assert.AreEqual(AttributeTargets.Method, usage.ValidOn);
            Assert.IsFalse(usage.Inherited);
            Assert.IsFalse(usage.AllowMultiple);
        }
    }
}
