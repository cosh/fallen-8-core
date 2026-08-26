// MIT License
//
// TempDirectory.cs
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
using System.IO;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   A uniquely named directory under the OS temp path, created on construction and removed on
    ///   dispose. THE way a test gets scratch space for checkpoints, WALs and sidecars, replacing
    ///   the hand-rolled <c>Path.Combine(Path.GetTempPath(), prefix + Guid)</c> plus
    ///   <c>try/finally { Directory.Delete }</c> pair.
    ///
    ///   <para>CLEANUP IS BEST EFFORT: <see cref="Dispose"/> swallows every failure. That is a
    ///   deliberate choice, not laziness, and it matches what every hand-rolled site already did.
    ///   On Windows a checkpoint, WAL or memory-mapped sidecar can still hold a handle when the
    ///   test body ends (the engine's writer thread and the OS both release lazily), so a delete
    ///   that threw would turn a passing test into a flaky one and blame the assertion that
    ///   already succeeded. A leftover directory in the temp path is a far smaller problem than a
    ///   false failure; the OS reclaims it.</para>
    ///
    ///   <para>A test that needs to OBSERVE cleanup (that a file was removed, that a delete
    ///   failed) must not use this: assert on the path yourself.</para>
    /// </summary>
    internal sealed class TempDirectory : IDisposable
    {
        /// <param name="prefix">
        ///   Leading name fragment, so a leaked directory still names the test that made it.
        /// </param>
        internal TempDirectory(String prefix = "f8_")
        {
            FullName = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(FullName);
        }

        /// <summary>The absolute path of the created directory.</summary>
        internal String FullName { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(FullName))
                {
                    Directory.Delete(FullName, true);
                }
            }
            catch
            {
                // Best effort by design - see the class remarks.
            }
        }
    }
}
