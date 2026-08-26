// MIT License
//
// VolatileAppFactory.cs
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   THE in-process host for a REST-surface test: the real apiApp pipeline with durability
    ///   turned OFF.
    ///
    ///   <para>WHY volatile is the default, explained here ONCE so no call site has to repeat it:
    ///   a durable host boots by discovering and loading a checkpoint and then writes its own
    ///   checkpoint/WAL files into the test bin directory. That makes tests order-dependent (one
    ///   test's leftover save point becomes the next one's starting graph), leaves litter behind,
    ///   and costs disk I/O for state no REST test asserts on. <c>Fallen8:Durability:Volatile</c>
    ///   gives every host a fresh empty graph and no files. A test that is ABOUT durability wants
    ///   the opposite and therefore does not use this factory.</para>
    ///
    ///   <para>Hand the constructor a settings dictionary to add host configuration on top (an API
    ///   key, a metadata directory, a feature switch); each entry becomes a
    ///   <see cref="IWebHostBuilder.UseSetting"/> call. The type is left open for derivation so a
    ///   test needing extra service wiring can override <see cref="ConfigureWebHost"/> and call
    ///   <c>base</c>.</para>
    /// </summary>
    internal class VolatileAppFactory : WebApplicationFactory<Program>
    {
        private readonly IDictionary<String, String> _settings;

        /// <param name="settings">Extra host settings, or <c>null</c> for volatile durability alone.</param>
        internal VolatileAppFactory(IDictionary<String, String> settings = null)
        {
            _settings = settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
}
