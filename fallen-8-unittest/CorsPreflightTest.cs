// MIT License
//
// CorsPreflightTest.cs
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

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// CORS preflight behaviour for a cross-origin standalone F8 Studio (feature standalone-ui),
    /// through the real ASP.NET pipeline. A preflight from an allowed origin must succeed
    /// ANONYMOUSLY even against a protected endpoint (UseCors precedes authentication and the rate
    /// limiter, so the OPTIONS short-circuits before auth), and must carry the cached-preflight
    /// max-age; a preflight from a disallowed origin gets no allow-origin header (default deny).
    /// </summary>
    [TestClass]
    public class CorsPreflightTest
    {
        private const string Origin = "http://localhost:3000";

        private static VolatileAppFactory NewHost()
        {
            // A key is configured (auth is ON) AND the UI origin is allow-listed via the indexed
            // array form the binder requires.
            return new VolatileAppFactory(new Dictionary<string, string>
            {
                ["Fallen8:Security:ApiKey"] = "test-secret-key",
                ["Fallen8:Security:AllowedCorsOrigins:0"] = Origin,
            });
        }

        private static HttpRequestMessage Preflight(string path, string origin, string method = "POST")
        {
            var req = new HttpRequestMessage(HttpMethod.Options, path);
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", method);
            req.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            return req;
        }

        [TestMethod]
        public async Task Preflight_FromAllowedOrigin_Is204Anonymous_WithMaxAge()
        {
            using var factory = NewHost();
            using var client = factory.CreateClient();

            // A protected code endpoint: no API key is sent, so if the preflight were gated by auth
            // it would be 401. It must instead short-circuit in CORS.
            using var res = await client.SendAsync(Preflight("/path/0/to/1", Origin));

            Assert.AreEqual(HttpStatusCode.NoContent, res.StatusCode,
                "An allowed-origin preflight must be 204 (short-circuited by CORS before auth), not 401.");
            Assert.AreEqual(Origin, res.Headers.GetValues("Access-Control-Allow-Origin").Single(),
                "The preflight response must echo the allowed origin.");
            Assert.AreEqual("600", res.Headers.GetValues("Access-Control-Max-Age").Single(),
                "SetPreflightMaxAge must cache the preflight so the SSE reconnect loop / bulk import do not preflight every request.");
        }

        [TestMethod]
        public async Task Preflight_FromDisallowedOrigin_HasNoAllowOriginHeader()
        {
            using var factory = NewHost();
            using var client = factory.CreateClient();

            using var res = await client.SendAsync(Preflight("/path/0/to/1", "http://evil.example.com"));

            Assert.IsFalse(res.Headers.Contains("Access-Control-Allow-Origin"),
                "A preflight from an origin not in the allow-list must not receive an Allow-Origin header (default deny).");
        }
    }
}
