// MIT License
//
// Program.cs
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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NoSQL.GraphDB.Core;

namespace NoSQL.GraphDB.Bench
{
    /// <summary>
    ///   Measures what the Capacity and performance page publishes, on the machine it is run on, and
    ///   writes a schema-conformant result file.
    ///
    ///   <para>Why this exists: a capacity number is only meaningful next to the hardware that
    ///   produced it. Hard-coding one person's laptop figures into the documentation made them read
    ///   like product guarantees. Instead the tool is the source, the result file carries its own
    ///   environment, and <c>scripts/update-capacity-doc.mjs</c> renders that file into the page.</para>
    /// </summary>
    public static class Program
    {
        /// <summary>Exit code for a usage error, distinct from a measurement failure.</summary>
        private const Int32 UsageError = 2;

        public static Int32 Main(String[] args)
        {
            String output = Path.Combine("fallen-8-bench", "results", "capacity-report.json");
            var profile = "quick";
            String? runnerLabel = null;
            String? only = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--output":
                    case "-o":
                        if (++i >= args.Length) { return Usage("--output needs a path"); }
                        output = args[i];
                        break;
                    case "--profile":
                    case "-p":
                        if (++i >= args.Length) { return Usage("--profile needs quick or full"); }
                        profile = args[i];
                        if (profile != "quick" && profile != "full") { return Usage("--profile must be quick or full"); }
                        break;
                    case "--runner-label":
                        if (++i >= args.Length) { return Usage("--runner-label needs a value"); }
                        runnerLabel = args[i];
                        break;
                    case "--only":
                        if (++i >= args.Length) { return Usage("--only needs a family name"); }
                        only = args[i];
                        if (only != "memory" && only != "writes" && only != "save" && only != "load" && only != "traversal")
                        {
                            return Usage("--only must be memory, writes, save, load or traversal");
                        }
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                    default:
                        return Usage("unknown argument: " + args[i]);
                }
            }

            Console.WriteLine("fallen-8-bench: profile=" + profile);
            Console.WriteLine("Measuring on " + (HardwareProbe.CpuName() ?? "unidentified CPU") + ", " +
                Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " logical processors, " +
                RuntimeInformation.OSDescription + ".");
            Console.WriteLine("Every number below describes THIS machine. Do not quote one without it.");
            Console.WriteLine();

            var report = Run(profile, runnerLabel, only);
            Write(report, output);

            Console.WriteLine();
            Console.WriteLine("Report written to " + Path.GetFullPath(output));
            Console.WriteLine("Render it into the docs page with: node scripts/update-capacity-doc.mjs " + output);
            return 0;
        }

        /// <summary>
        ///   Runs every measurement for the profile. <c>quick</c> is sized to finish on a shared CI
        ///   runner in a couple of minutes; <c>full</c> uses the larger graphs the page's guidance is
        ///   really about. The shapes are part of the published contract, so changing one changes what
        ///   the page's rows mean: say so in the commit.
        /// </summary>
        private static CapacityReport Run(String profile, String? runnerLabel, String? only)
        {
            // A family filter, so someone iterating on one measurement (traversal speed, say) does not
            // have to rebuild a hundred-million-edge graph four times to see one number move. A
            // filtered report is NOT publishable: the renderer requires every family, and it should,
            // because a page showing three of four families would silently look complete.
            Boolean Wanted(String family) => only == null || only == family;

            var full = profile == "full";

            // The full profile's headline shape is 10,000,000 vertices with 100,000,000 edges. That
            // graph retains on the order of 13 GB, so a full run wants a machine with 32 GB or more and
            // takes tens of minutes, most of it building edges. The scenarios run one after another and
            // each releases its graph, so the peak is one shape, not their sum.
            var memoryShapes = full
                ? new[]
                {
                    new Shape("degree 2", 2_000_000, 2),
                    new Shape("degree 10", 10_000_000, 10),
                    new Shape("degree 20", 1_000_000, 20)
                }
                : new[]
                {
                    new Shape("degree 2", 200_000, 2),
                    new Shape("degree 10", 200_000, 10),
                    new Shape("degree 20", 100_000, 20)
                };

            // Shared by the save-stall and the startup-load families on purpose: the two are the same
            // graph written and read back, so a reader can compare a checkpoint's cost with the cost
            // of booting from it without wondering whether the shapes match. They also run smallest
            // first, which the load measurement depends on (see Measurements.Load: it is deliberately
            // not warmed up, so the process's one-off costs land on the smallest row).
            var saveShapes = full
                ? new[]
                {
                    new Shape("1M elements", 334_000, 2),
                    new Shape("4M elements", 1_334_000, 2),
                    new Shape("20M elements", 6_667_000, 2)
                }
                : new[]
                {
                    new Shape("300k elements", 100_000, 2),
                    new Shape("1.2M elements", 400_000, 2)
                };

            // Traversal is measured at SEVERAL sizes, because one number hides the only thing that
            // really governs traversal speed: whether the working set fits in cache. A small graph
            // reports a rate the same machine cannot sustain on a large one, and the drop between them
            // is memory latency on the random target dereference, not the engine getting slower.
            //
            // A graph also has to be big enough that ONE pass takes tens of milliseconds: below that
            // the stopwatch resolution and the thread-pool ramp dominate and the rate is noise, which
            // is what the first version of this tool got wrong.
            var traversalShapes = full
                ? new[]
                {
                    new Shape("5M edges", 500_000, 10),
                    new Shape("20M edges", 2_000_000, 10),
                    new Shape("100M edges", 10_000_000, 10)
                }
                : new[]
                {
                    new Shape("1M edges", 100_000, 10),
                    new Shape("5M edges", 500_000, 10)
                };

            var writes = full ? 200_000 : 20_000;
            var producers = Math.Max(2, Math.Min(32, Environment.ProcessorCount * 2));

            // Per write scenario. Keeps an fsync-bound serial run from adding minutes to a full profile;
            // the rate is computed over whatever committed inside the window.
            var writeSeconds = full ? 30d : 10d;

            var report = new CapacityReport
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Profile = profile,
                Tool = new ToolInfo { Version = ToolVersion() },
                Source = SourceOf(),
                Environment = EnvironmentOf(runnerLabel)
            };

            if (Wanted("memory"))
            {
                foreach (var shape in memoryShapes)
                {
                    Console.WriteLine("memory: " + shape.Label + " ...");
                    report.Metrics.Memory.Add(Measurements.Memory(shape));
                }
            }

            if (Wanted("writes"))
            {
                Console.WriteLine("write throughput: serial ...");
                report.Metrics.WriteThroughput.Add(Measurements.WriteThroughput("serial (1 producer)", writes, 1, writeSeconds));
                Console.WriteLine("write throughput: " + producers + " producers ...");
                report.Metrics.WriteThroughput.Add(Measurements.WriteThroughput(
                    producers.ToString(CultureInfo.InvariantCulture) + " concurrent producers", writes, producers, writeSeconds));
            }

            if (Wanted("save"))
            {
                foreach (var shape in saveShapes)
                {
                    Console.WriteLine("save stall: " + shape.Label + " ...");
                    report.Metrics.SaveStall.Add(Measurements.SaveStall(shape));
                }
            }

            if (Wanted("load"))
            {
                foreach (var shape in saveShapes)
                {
                    Console.WriteLine("startup load: " + shape.Label + " ...");
                    report.Metrics.Load.Add(Measurements.Load(shape));
                }
            }

            if (Wanted("traversal"))
            {
                foreach (var shape in traversalShapes)
                {
                    Console.WriteLine("traversal: " + shape.Label + " ...");
                    report.Metrics.Traversal.Add(Measurements.Traversal(shape, full ? 5 : 10));
                }
            }

            return report;
        }

        private static SourceInfo SourceOf()
        {
            var source = new SourceInfo
            {
                EngineVersion = typeof(Fallen8).Assembly.GetName().Version?.ToString() ?? "0"
            };

            var commit = Git("rev-parse HEAD");
            if (!String.IsNullOrEmpty(commit))
            {
                source.Commit = commit;
                source.DirtyWorkingTree = !String.IsNullOrEmpty(Git("status --porcelain"));
            }

            return source;
        }

        private static EnvironmentInfo EnvironmentOf(String? runnerLabel)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return new EnvironmentInfo
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ProcessorName = HardwareProbe.CpuName(),
                TotalPhysicalMemoryMb = gcInfo.TotalAvailableMemoryBytes > 0
                    ? Math.Round(gcInfo.TotalAvailableMemoryBytes / 1048576.0, 0)
                    : (Double?)null,
                ServerGarbageCollection = System.Runtime.GCSettings.IsServerGC,
                // There is no public API for "is background GC enabled", and GCMemoryInfo.Concurrent
                // describes the LAST collection rather than the configuration. Batch latency is the
                // one mode that rules background collection out, so it is the honest proxy.
                ConcurrentGarbageCollection = System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.Batch,
                RunnerLabel = runnerLabel ?? Environment.GetEnvironmentVariable("F8_BENCH_RUNNER_LABEL")
            };
        }

        private static String ToolVersion()
            => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";

        /// <summary>Best-effort git read; a tarball with no .git is a normal case, not an error.</summary>
        private static String? Git(String arguments)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });

                if (process == null)
                {
                    return null;
                }

                var text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 ? text : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Write(CapacityReport report, String output)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(output, json + Environment.NewLine);
        }

        private static Int32 Usage(String message)
        {
            Console.Error.WriteLine("fallen-8-bench: " + message);
            PrintUsage();
            return UsageError;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: dotnet run --project fallen-8-bench -- [options]");
            Console.WriteLine();
            Console.WriteLine("  -o, --output <path>     Where to write the report");
            Console.WriteLine("                          (default fallen-8-bench/results/capacity-report.json)");
            Console.WriteLine("  -p, --profile <name>    quick (CI-sized, the default) or full (larger graphs)");
            Console.WriteLine("      --runner-label <s>   Free-form name for this machine. The CPU is identified");
            Console.WriteLine("                          automatically; use the label for what the tool cannot");
            Console.WriteLine("                          read, e.g. \"DDR4-3600, dual channel\" or a CI image name");
            Console.WriteLine("  -h, --help              This text");
            Console.WriteLine();
            Console.WriteLine("The result file conforms to fallen-8-bench/capacity-report.schema.json.");
        }
    }
}
