// MIT License
//
// AgentHostCompositionTest.cs
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
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Hosting;
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   What the host's own registration produces, as opposed to what a test can build by hand.
    ///
    ///   <para>
    ///     This file exists because of a gap the review gate MEASURED rather than argued. The
    ///     adapter's deadline was moved onto the transport to fix a gateway that hung being recorded
    ///     as an agent somebody cancelled, and the test for it arms
    ///     <see cref="HttpClient.Timeout" /> on a client the test itself builds. That pins the
    ///     ADAPTER's behaviour given a bounded transport, and nothing pinned the host actually
    ///     bounding it: reverting the host to an infinite timeout left all 234 agent tests green
    ///     with the defect back in place.
    ///   </para>
    ///   <para>
    ///     So the rule here is narrow and worth keeping narrow: a claim about what the composed host
    ///     does is tested against the composed host. Nothing else belongs in this file.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentHostCompositionTest
    {
        private static ServiceProvider Host(params (String Key, String Value)[] settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(
                    s => new KeyValuePair<String, String>(s.Key, s.Value)))
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            AgentsHost.AddFallen8Agents(services, configuration);
            return services.BuildServiceProvider();
        }

        /// <summary>The named client the adapter is built over, as the host composes it.</summary>
        private static HttpClient ChatClient(ServiceProvider provider)
        {
            return provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(AgentsHost.ChatClientName);
        }

        [TestMethod]
        public void TheChatTransportCarriesTheConfiguredDeadline_NotAnInfiniteOne()
        {
            using var provider = Host(("Fallen8Target:TimeoutSeconds", "42"));
            using var http = ChatClient(provider);

            Assert.AreEqual(TimeSpan.FromSeconds(42), http.Timeout,
                "the host has to arm the deadline on the TRANSPORT. Armed anywhere else it is a "
                + "linked source, which cancels the caller's token, which is the one shape the "
                + "shared seam cannot tell from a caller who walked away.");
            Assert.AreNotEqual(System.Threading.Timeout.InfiniteTimeSpan, http.Timeout,
                "an unbounded transport is the state this whole fix removed");
        }

        /// <summary>
        ///   The clamp is in force on the property whose limit it was chosen for. The host's comment
        ///   claims exactly this, and it is the reason a very large configured value cannot throw
        ///   <see cref="ArgumentOutOfRangeException" /> out of the client factory at first use.
        /// </summary>
        [TestMethod]
        public void AnAbsurdDeadlineIsClamped_RatherThanThrowingOutOfTheClientFactory()
        {
            using var provider = Host(("Fallen8Target:TimeoutSeconds", Int32.MaxValue.ToString()));
            using var http = ChatClient(provider);

            Assert.AreEqual(TimeSpan.FromSeconds(OptionBounds.MaxSeconds), http.Timeout,
                "OptionBounds.MaxSeconds is HttpClient.Timeout's own limit, which is why it is the "
                + "tightest of the three the clamp feeds");
        }

        /// <summary>A non-positive value is floored rather than read as "no deadline", which is the
        /// shared clamp's documented rule and the reason it has no off switch.</summary>
        [TestMethod]
        public void ANonPositiveDeadlineIsFlooredAtOneSecond()
        {
            using var provider = Host(("Fallen8Target:TimeoutSeconds", "0"));
            using var http = ChatClient(provider);

            Assert.AreEqual(TimeSpan.FromSeconds(1), http.Timeout);
        }

        /// <summary>
        ///   With nothing configured the transport still carries this host's own default of 630
        ///   seconds, which sits deliberately ABOVE the gateway's own 600 so the instance's answer
        ///   is the one a caller sees rather than a local timeout that names nothing.
        /// </summary>
        [TestMethod]
        public void WithNothingConfigured_TheTransportCarriesThisHostsOwnDefault()
        {
            using var provider = Host();
            using var http = ChatClient(provider);

            Assert.AreEqual(TimeSpan.FromSeconds(630), http.Timeout,
                "the default is this host's, and it is above the gateway's 600 on purpose");
        }
    }
}
