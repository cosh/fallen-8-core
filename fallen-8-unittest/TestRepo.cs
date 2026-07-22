// MIT License
//
// TestRepo.cs
//
// Copyright (c) 2026 Henning Rauch
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

using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Locates checked-in repository files from tests that need them (source conventions,
    /// pinned snapshots). Tests run from the bin directory, so the repo root is found by
    /// walking up to the directory holding the solution file.
    /// </summary>
    internal static class TestRepo
    {
        internal static string Root()
        {
            var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "fallen-8-core.sln")))
            {
                directory = directory.Parent;
            }

            Assert.IsNotNull(directory, "could not locate fallen-8-core.sln above the test assembly");
            return directory.FullName;
        }
    }
}
