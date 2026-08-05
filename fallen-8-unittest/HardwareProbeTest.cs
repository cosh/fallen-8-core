// MIT License
//
// HardwareProbeTest.cs
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
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Bench;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the contract of <see cref="HardwareProbe" />: the capacity report's CPU identity comes
    ///   from the hardware, not from anything a human can mistype. The motivating incident: a
    ///   published report labelled a Ryzen 5950X as a 9950X, and the only machine-read field was a
    ///   family/model code that no reader decoded until the numbers already made no sense.
    /// </summary>
    [TestClass]
    public class HardwareProbeTest
    {
        [TestMethod]
        public void CpuName_OnAnX86Host_IsTheBrandStringNotTheFamilyModelCode()
        {
            if (!X86Base.IsSupported)
            {
                Assert.Inconclusive("CPUID is x86-only; on this architecture the probe uses a fallback source.");
            }

            var name = HardwareProbe.CpuName();

            Assert.IsNotNull(name, "an x86 CPU modern enough to run .NET 10 reports a brand string");
            // The old source (PROCESSOR_IDENTIFIER) has the shape "AMD64 Family 25 Model 33 ...".
            // If this ever matches, the probe has regressed to reporting the undecodable code again.
            Assert.IsFalse(Regex.IsMatch(name, @"^(AMD64|x86|Intel64|ARM64) Family \d"),
                "probe returned the family/model code instead of the brand string: " + name);
        }

        [TestMethod]
        public void CpuName_IsCleanSingleSpacedText()
        {
            var name = HardwareProbe.CpuName();
            if (name == null)
            {
                Assert.Inconclusive("no CPU name source on this platform, nothing to inspect");
            }

            // Brand strings arrive NUL-terminated and space-padded; the report must not carry that.
            Assert.AreEqual(name, name.Trim(), "leading or trailing whitespace survived normalisation");
            Assert.IsFalse(name.Contains('\0'), "a NUL byte survived normalisation");
            Assert.IsFalse(name.Contains("  "), "consecutive padding spaces survived normalisation");
            Assert.IsTrue(name.Length > 0, "an empty name must be null instead");
        }

        [TestMethod]
        public void CpuName_IsStableAcrossCalls()
        {
            // The report and the console banner both read it; they must agree.
            Assert.AreEqual(HardwareProbe.CpuName(), HardwareProbe.CpuName());
        }
    }
}
