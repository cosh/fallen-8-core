// MIT License
//
// BenchmarkTest.cs
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
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Benchmark;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class BenchmarkTest
    {
        // Helper method to create logger factory consistently
        private ILoggerFactory CreateLoggerFactory()
        {
            return LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("NoSQL.GraphDB", LogLevel.Debug)
                    .AddConsole();
            });
        }

        [TestMethod]
        public void ScaleFreeNetwork_ShouldCreateExpectedGraph()
        {
            // Arrange - Create a new isolated instance for this test
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var benchmark = new ScaleFreeNetwork(fallen8);

            // Act
            benchmark.CreateScaleFreeNetwork(1000, 10);
            var result = benchmark.Bench(10);

            // Assert
            Assert.AreEqual(1000, fallen8.VertexCount, "Expected 1000 vertices in the scale free network");
            Assert.AreEqual(10000, fallen8.EdgeCount, "Expected 10000 edges in the scale free network");
        }
    }
}
