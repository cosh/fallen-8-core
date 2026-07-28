// MIT License
//
// AdminControllerContractTest.cs
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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.Core;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Contract tests for <see cref="AdminController"/> built by direct construction (null deps fall
    /// back to defaults, per the constructor's documented unit-construction path).
    /// </summary>
    [TestClass]
    public class AdminControllerContractTest
    {
        [TestMethod]
        public void Load_WithNullBody_Returns400_NotA500()
        {
            // A JSON `null` body binds `definition` to null. Without the guard this NRE'd to a 500;
            // the null-guard now returns a 400 like the Save / AddVertex / ... siblings.
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            var controller = new AdminController(loggerFactory.CreateLogger<AdminController>(), fallen8, null, null);

            var result = controller.Load(null).Result;

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);

            fallen8.Dispose();
        }
    }
}
