// MIT License
//
// IntegrationsProviderCatalogTest.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The catalog's STARTUP judgement of a descriptor, for the part of it that becomes a link in
    ///   somebody's browser: <see cref="ProviderDescriptor.DocsUrl"/>.
    ///
    ///   <para>Checked here rather than on the HTTP surface because the failure this prevents is a
    ///   provider that never loads at all - the catalog throws while the process is starting, which is
    ///   the whole point of the check. The shipped providers' own links are asserted over the real route
    ///   in <c>IntegrationsEndpointTest</c>; these are the values only a wrong provider would send.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsProviderCatalogTest
    {
        #region a docs link the catalog accepts

        [TestMethod]
        public void AProviderDeclaringNoDocsUrl_IsAccepted()
        {
            var catalog = Catalog(null);

            Assert.AreEqual(1, catalog.Descriptors.Length,
                "documentation is optional: a provider written before the field existed, or one whose " +
                "author wrote no page, must still load");
            Assert.IsNull(catalog.Descriptors[0].DocsUrl);
        }

        [TestMethod]
        public void AnHttpsDocsUrl_IsAccepted()
        {
            var catalog = Catalog("https://docs.example.com/thing/");

            Assert.AreEqual("https://docs.example.com/thing/", catalog.Descriptors[0].DocsUrl);
        }

        [TestMethod]
        public void AnHttpDocsUrl_IsAccepted()
        {
            // Plain http is allowed on purpose: a provider written for an operator's own network may
            // document itself on an intranet host that serves no TLS, and refusing it would push that
            // author back to crowding the description instead.
            var catalog = Catalog("http://wiki.internal/integrations/thing");

            Assert.AreEqual("http://wiki.internal/integrations/thing", catalog.Descriptors[0].DocsUrl);
        }

        [TestMethod]
        public void ADocsUrlWithAFragment_IsAccepted()
        {
            // The shipped ARXML provider uses exactly this shape: one page documents every integration,
            // so a provider worth its own section links the section.
            var catalog = Catalog("https://docs.fallen-8.com/integrations/#reading-a-vehicle-network");

            Assert.AreEqual("https://docs.fallen-8.com/integrations/#reading-a-vehicle-network",
                catalog.Descriptors[0].DocsUrl);
        }

        #endregion

        #region a docs link the catalog refuses at startup

        [TestMethod]
        public void ARelativeDocsUrl_StopsTheProcessStarting()
        {
            // It would resolve against whatever origin Studio itself is served from and land on a path
            // Studio does not serve, so the link is dead in a way nothing about the row shows.
            AssertRefused("/integrations/", "relative");
        }

        [TestMethod]
        public void AJavascriptDocsUrl_StopsTheProcessStarting()
        {
            // The descriptor crosses the network from a deployable Studio does not ship with, so this is
            // the scheme that matters: it would run in the operator's browser on a click.
            AssertRefused("javascript:alert(1)", "javascript");
        }

        [TestMethod]
        public void ANonWebSchemeDocsUrl_StopsTheProcessStarting()
        {
            // Absolute, parses, and still not a link a browser follows to a page.
            AssertRefused("file:///c:/docs/thing.html", "file");
        }

        [TestMethod]
        public void AnEmptyDocsUrl_StopsTheProcessStarting()
        {
            // Not the same statement as declaring none: the field is present, so the intent was a link,
            // and an empty one renders as a docs link that goes nowhere.
            AssertRefused(String.Empty, "empty");
        }

        [TestMethod]
        public void AWhitespaceDocsUrl_StopsTheProcessStarting()
        {
            AssertRefused("   ", "whitespace");
        }

        [TestMethod]
        public void ARefusalNamesTheProviderAndTheValue()
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => Catalog("/integrations/"));

            StringAssert.Contains(error.Message, StubId,
                "a startup failure listing neither the provider nor the value leaves an operator " +
                "restarting a container with nothing to fix");
            StringAssert.Contains(error.Message, "/integrations/");
        }

        #endregion

        #region helpers

        private const String StubId = "stub-provider";

        private static ProviderCatalog Catalog(String docsUrl)
            => new ProviderCatalog(
                new IIntegrationProvider[] { new StubProvider(docsUrl) },
                IdentifierVocabulary.Shipped);

        private static void AssertRefused(String docsUrl, String what)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Catalog(docsUrl),
                "a " + what + " docsUrl was accepted. Caught at startup it costs a restart; accepted, it " +
                "reaches a row in the Studio as a link");
        }

        /// <summary>
        ///   The smallest thing the catalog will judge: a descriptor and a method that is never called,
        ///   since nothing here runs a job.
        /// </summary>
        private sealed class StubProvider : IIntegrationProvider
        {
            public StubProvider(String docsUrl)
            {
                Descriptor = new ProviderDescriptor
                {
                    Id = StubId,
                    DisplayName = "Stub",
                    Description = "Reads nothing.",
                    DocsUrl = docsUrl,
                };
            }

            public ProviderDescriptor Descriptor { get; }

            public Task<SnapshotDocument> ObserveAsync(ProviderContext context, CancellationToken cancellationToken)
                => throw new NotSupportedException("this provider exists to be judged, not to run");
        }

        #endregion
    }
}
