// MIT License
//
// HardwareProbe.cs
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
using System.Buffers.Binary;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace NoSQL.GraphDB.Bench
{
    /// <summary>
    ///   Identifies the CPU by asking the CPU, so the report's environment block states what the
    ///   silicon is rather than what a human typed. This exists because a published report once
    ///   carried a runner label naming one processor while the machine was a different one, and
    ///   nothing in the file could contradict it: <c>runnerLabel</c> is free-form input, and the old
    ///   <c>PROCESSOR_IDENTIFIER</c> value only gave a family/model code few readers can decode. The
    ///   brand string is written by the manufacturer into the chip and cannot drift from the
    ///   hardware that produced the numbers.
    /// </summary>
    public static class HardwareProbe
    {
        /// <summary>
        ///   Resolved once: the answer cannot change while the process runs, and the report and the
        ///   console banner must agree with each other.
        /// </summary>
        private static readonly Lazy<String?> _cpuName = new Lazy<String?>(Resolve);

        /// <summary>
        ///   The CPU's marketing name, e.g. "AMD Ryzen 9 5950X 16-Core Processor". Read from the
        ///   CPUID brand string on x86/x64 (any operating system), from <c>/proc/cpuinfo</c> on
        ///   other Linux architectures, and from the <c>PROCESSOR_IDENTIFIER</c> environment
        ///   variable as the last resort. <c>null</c> when no source is available.
        /// </summary>
        public static String? CpuName() => _cpuName.Value;

        private static String? Resolve()
            => BrandStringFromCpuId()
               ?? NameFromProcCpuInfo()
               ?? Normalize(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));

        /// <summary>
        ///   CPUID leaves 0x80000002 to 0x80000004 return the 48-byte brand string, sixteen bytes
        ///   per leaf in register order EAX, EBX, ECX, EDX, little-endian. Every x86 CPU of this
        ///   century supports them, but the maximum extended leaf is checked anyway because that is
        ///   what the manual says to do.
        /// </summary>
        private static String? BrandStringFromCpuId()
        {
            if (!X86Base.IsSupported)
            {
                return null;
            }

            var (maxExtendedLeaf, _, _, _) = X86Base.CpuId(unchecked((Int32)0x80000000u), 0);
            if ((UInt32)maxExtendedLeaf < 0x80000004u)
            {
                return null;
            }

            var bytes = new Byte[48];
            for (var leaf = 0; leaf < 3; leaf++)
            {
                var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((Int32)(0x80000002u + (UInt32)leaf)), 0);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(leaf * 16), eax);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(leaf * 16 + 4), ebx);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(leaf * 16 + 8), ecx);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(leaf * 16 + 12), edx);
            }

            return Normalize(Encoding.ASCII.GetString(bytes));
        }

        /// <summary>
        ///   The Linux fallback for architectures without CPUID (ARM servers, Raspberry Pi).
        /// </summary>
        private static String? NameFromProcCpuInfo()
        {
            const String path = "/proc/cpuinfo";

            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                foreach (var line in File.ReadLines(path))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separator).Trim();
                    if (key == "model name" || key == "Model")
                    {
                        return Normalize(line.Substring(separator + 1));
                    }
                }

                return null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        ///   Brand strings are NUL-terminated and padded with spaces (older Intel parts even lead
        ///   with them), so the raw 48 bytes are collapsed to single-spaced text.
        /// </summary>
        private static String? Normalize(String? text)
        {
            if (text == null)
            {
                return null;
            }

            var words = text.Replace('\0', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 0 ? null : String.Join(' ', words);
        }
    }
}
