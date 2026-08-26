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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Traversal;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

/// <summary>
///   The browser gate. Runs the engine as a trimmed browser-wasm app on a runtime that can start
///   no thread, and checks the things a unit test on a threaded host structurally CANNOT.
///
///   <para>WHY THIS EXISTS AS A COMMITTED HARNESS. Every single-threaded arm in the engine is
///   selected by <c>HostCapabilities.SupportsBackgroundWork</c>, which is true on every machine the
///   test suite runs on, so the browser halves of the transaction writer, the checkpoint fan-out,
///   the change-feed teardown and the traversal sweep are executed by NOTHING ELSE: the checks below
///   are the only place those arms ever run. Twice now a review accepted that gap on the grounds that
///   "the trimmed browser probe is the compensating control", while the probe itself was a throwaway
///   in a scratchpad that nobody could run. A control nobody can run is not a control, so this one is
///   in the repository and in CI.</para>
///
///   <para>The verdict is the process exit code, so this is a gate and not a log. Each check prints
///   one line; a failure prints why and the run ends non-zero.</para>
/// </summary>
internal static class Program
{
    private static Int32 _failures;

    private static Int32 Main()
    {
        Console.WriteLine("Fallen-8 browser probe: trimmed browser-wasm, single-threaded runtime");
        Console.WriteLine(new String('-', 78));

        // The premise the whole design rests on. If this is ever false here, the probe is testing
        // a threaded host by accident and every check below proves nothing about a browser.
        Check("the runtime cannot start a thread, so this really is the single-threaded host",
            NoThreadCanStart);

        Check("an engine can be constructed and written to", () =>
        {
            using var engine = NewEngine();
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(new VertexDefinition { Label = "device", CreationDate = 0 });
            tx.AddVertex(new VertexDefinition { Label = "device", CreationDate = 0 });
            var info = engine.EnqueueTransaction(tx);

            if (info.TransactionState != TransactionState.Finished)
            {
                return "the transaction did not finish inline: " + info.TransactionState + " " + info.Error;
            }

            return engine.GetAllVertices().Count == 2 ? null : "expected 2 vertices";
        });

        // The blocker this feature closes. A browser has no dll files under the base directory, so
        // name-based discovery finds nothing and index creation used to be impossible here.
        Check("index creation FAILS before registration, because discovery finds no assemblies", () =>
        {
            using var engine = NewEngine();
            return engine.IndexFactory.TryCreateIndex(out _, "before", "DictionaryIndex")
                ? "an index was created without registration, so this probe is not running the " +
                    "browser packaging the feature was designed for and proves nothing"
                : null;
        });

        Check("index creation SUCCEEDS after RegisterPluginType, and the index works", () =>
        {
            using var engine = NewEngine();
            var registered = engine.RegisterPluginType<DictionaryIndex>();
            if (registered.TransactionState != TransactionState.Finished)
            {
                return "registration did not finish: " + registered.TransactionState + " " + registered.Error;
            }

            if (!engine.IndexFactory.TryCreateIndex(out var index, "claims", "DictionaryIndex"))
            {
                return "TryCreateIndex still refused a registered type";
            }

            var id = NewVertex(engine, "server");
            if (!engine.TryGetGraphElement(out var element, id))
            {
                return "the vertex just created was not readable";
            }

            index.AddOrUpdate("mac:44d2", element);
            if (!index.TryGetValue(out var hits, "mac:44d2") || hits.Count != 1)
            {
                return "the registered index did not answer its own key";
            }

            return null;
        });

        Check("vector search works after registering the vector index", () =>
        {
            using var engine = NewEngine();
            engine.RegisterPluginType<VectorIndex>();

            if (!engine.IndexFactory.TryCreateIndex(out var index, "vectors", "VectorIndex",
                    new Dictionary<String, Object> { { "dimension", 3 } }))
            {
                return "TryCreateIndex refused a registered VectorIndex";
            }

            var near = NewVertex(engine, "near");
            var far = NewVertex(engine, "far");
            engine.TryGetGraphElement(out var nearElement, near);
            engine.TryGetGraphElement(out var farElement, far);

            index.AddOrUpdate(new Single[] { 1f, 0f, 0f }, nearElement);
            index.AddOrUpdate(new Single[] { 0f, 0f, 1f }, farElement);

            if (index is not IVectorIndex vector)
            {
                return "the created index is not an IVectorIndex";
            }

            if (!vector.TryNearestNeighbors(out var result, new Single[] { 1f, 0f, 0f }, 1))
            {
                return "the nearest-neighbour search found nothing";
            }

            var entries = result.Entries;
            return entries.Count == 1 && entries[0].Element.Id == near
                ? null
                : "the nearest neighbour was not the element at the query vector";
        });

        // A checkpoint on this host runs its fan-out sequentially, and a load has to rehydrate an
        // index BY NAME - which is the half of the blocker that only OpenIndex-through-the-registry
        // can close. The file lands in the Emscripten in-memory filesystem.
        Check("a checkpoint round trip keeps a host-registered index and its content",
            CheckpointRoundTripKeepsTheIndex);

        Check("a host registration survives a Load instead of being wiped by it",
            RegistrationSurvivesALoad);

        // The traversal sweep runs as ONE sequential range here instead of a parallel fan-out over
        // partitions. Both arms owe the SAME number, so the count is the assertion: a sequential arm
        // that swept nothing would return just as quietly as one that swept everything.
        Check("the traversal sweep follows every out-edge on its sequential arm", () =>
        {
            using var engine = NewEngine();

            var vertices = new CreateVerticesTransaction();
            for (var i = 0; i < 64; i++)
            {
                vertices.AddVertex(new VertexDefinition { Label = "node", CreationDate = 0 });
            }

            var created = engine.EnqueueTransaction(vertices);
            if (created.TransactionState != TransactionState.Finished)
            {
                return "the vertices to sweep were not created: " + created.TransactionState + " " + created.Error;
            }

            // Half the vertices stay isolated, so the sweep also walks over adjacency-free vertices,
            // and one self-loop is one out-edge like any other.
            var ids = vertices.GetCreatedVertices();
            var edges = new CreateEdgesTransaction();
            for (var i = 0; i < 32; i++)
            {
                edges.AddEdge(ids[i].Id, "next", ids[(i + 5) % 64].Id, 0);
            }

            edges.AddEdge(ids[0].Id, "next", ids[0].Id, 0);

            var wired = engine.EnqueueTransaction(edges);
            if (wired.TransactionState != TransactionState.Finished)
            {
                return "the edges to sweep were not wired: " + wired.TransactionState + " " + wired.Error;
            }

            var traversed = OutEdgeSweep.Sweep(engine.GetAllVertices());
            return traversed == 33L
                ? null
                : "the sequential sweep followed " + traversed + " out-edges where 33 were wired, so " +
                    "the arm a browser host takes does not agree with the parallel one";
        });

        // The change-feed teardown's browser arm (why the dispatch loop is not joined here belongs to
        // ChangeFeedDispatcher.Dispose). A completed subscriber stream proves the teardown still did
        // its job, and the elapsed budget makes a regression FAIL instead of hanging: the join it
        // would reintroduce costs ten seconds on this host, every time.
        Check("a change-feed engine tears down without joining its dispatch loop", () =>
        {
            var engine = new Fallen8(NullLoggerFactory.Instance, new ChangeFeedOptions());
            if (!engine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription))
            {
                return "the change feed refused its first subscriber";
            }

            NewVertex(engine, "watched");

            var teardown = Stopwatch.StartNew();
            engine.Dispose();
            teardown.Stop();

            // Drained first because a reader completes only once its queue is closed AND empty, and
            // whether the dispatcher got as far as delivering that commit depends on an event loop
            // that does not run while Main does.
            while (subscription.Reader.TryRead(out _))
            {
            }

            if (!subscription.Reader.Completion.IsCompletedSuccessfully)
            {
                return "the subscriber stream did not complete on dispose, so a browser client would " +
                    "keep waiting on a feed that is already gone";
            }

            return teardown.Elapsed < TimeSpan.FromSeconds(2)
                ? null
                : "dispose took " + teardown.ElapsedMilliseconds + " ms, which is a wait for a dispatch " +
                    "loop that cannot run until dispose returns";
        });

        Console.WriteLine(new String('-', 78));
        if (_failures == 0)
        {
            Console.WriteLine("PROBE PASSED: the engine builds trimmed, runs on a single-threaded host, sweeps " +
                "and tears down on its sequential arms, and a host-registered index survives a " +
                "checkpoint round trip");
            return 0;
        }

        Console.WriteLine("PROBE FAILED: " + _failures + " check(s) failed");
        return 1;
    }

    /// <summary>
    ///   A checkpoint on this host runs its fan-out sequentially, and a load rehydrates an index BY
    ///   NAME - the half of the browser blocker only OpenIndex-through-the-registry can close. The
    ///   file lands in the Emscripten in-memory filesystem.
    ///
    ///   <para>The suppression records a REAL limitation rather than hiding one: the engine still
    ///   declares the checkpoint round trip trim-hostile, because a checkpoint stores property values
    ///   resolved reflectively on load. Index and service plugin NAMES no longer justify that
    ///   annotation now that both resolve registry-first, so the annotation is broader than the truth
    ///   and narrowing it is tracked as a finding. The probe exercises the path anyway, because
    ///   whether it WORKS on this runtime is a separate question from what it warns about.</para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The engine declares the checkpoint round trip trim-hostile for reflective " +
            "property values. The probe deliberately runs it to prove a browser host can save and " +
            "load at all; narrowing the engine's annotation is a separate, tracked change.")]
    private static String CheckpointRoundTripKeepsTheIndex()
    {
        {
            var dir = Path.Combine(Path.GetTempPath(), "f8probe");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "probe.f8s");
            Int32 id;

            using (var engine = NewEngine())
            {
                engine.RegisterPluginType<DictionaryIndex>();
                if (!engine.IndexFactory.TryCreateIndex(out var index, "claims", "DictionaryIndex"))
                {
                    return "TryCreateIndex refused a registered type before the save";
                }

                id = NewVertex(engine, "server");
                engine.TryGetGraphElement(out var element, id);
                index.AddOrUpdate("mac:44d2", element);

                var save = engine.EnqueueTransaction(new SaveTransaction { Path = path, SavePartitions = 1 });
                if (save.TransactionState != TransactionState.Finished)
                {
                    return "the save did not finish: " + save.TransactionState + " " + save.Error;
                }
            }

            using (var restored = NewEngine())
            {
                // The registration must be re-established by the host on every start - a host entry
                // is deliberately never persisted - and it must be in place BEFORE the load, because
                // the load resolves the index by name.
                restored.RegisterPluginType<DictionaryIndex>();

                var load = restored.EnqueueTransaction(new LoadTransaction { Path = path, StartServices = false });
                if (load.TransactionState != TransactionState.Finished)
                {
                    return "the load did not finish: " + load.TransactionState + " " + load.Error;
                }

                if (!restored.IndexFactory.TryGetIndex(out var index, "claims"))
                {
                    return "the index did not survive the round trip: OpenIndex could not resolve " +
                        "the host-registered type by name";
                }

                if (!index.TryGetValue(out var hits, "mac:44d2") || hits.Count != 1)
                {
                    return "the index survived but its content did not";
                }

                return restored.GetAllVertices().Count == 1 ? null : "the graph did not survive";
            }
        }
    }

    /// <summary>
    ///   <c>RehydratePlugins</c> used to end in a wholesale <c>ReplaceAll</c>, which would drop every
    ///   host registration on any Load - including the Load that needs those very types. Suppressed
    ///   for the same reason as the round-trip check above.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "See CheckpointRoundTripKeepsTheIndex: the engine's checkpoint annotation is " +
            "about reflective property values, and the probe runs the path deliberately.")]
    private static String RegistrationSurvivesALoad()
    {
        {
            var dir = Path.Combine(Path.GetTempPath(), "f8probe-merge");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "merge.f8s");

            using (var engine = NewEngine())
            {
                NewVertex(engine, "one");
                var save = engine.EnqueueTransaction(new SaveTransaction { Path = path, SavePartitions = 1 });
                if (save.TransactionState != TransactionState.Finished)
                {
                    return "the save did not finish: " + save.Error;
                }
            }

            using (var restored = NewEngine())
            {
                restored.RegisterPluginType<RegExIndex>();
                restored.EnqueueTransaction(new LoadTransaction { Path = path, StartServices = false })
                    .WaitUntilFinished();

                // RehydratePlugins used to end in a wholesale ReplaceAll, which would drop this.
                return restored.IndexFactory.TryCreateIndex(out _, "after-load", "RegExIndex")
                    ? null
                    : "the host registration did not survive the Load, so nothing registered before " +
                        "a load can be used after one";
            }
        }
    }

    /// <summary>
    ///   The premise every other check rests on. Deliberately calls the API the platform does not
    ///   support, which is why CA1416 is suppressed here and nowhere else: the analyzer is right that
    ///   <c>Thread.Start</c> is unsupported on browser, and proving it throws is the point.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416",
        Justification = "The probe asserts that Thread.Start is unsupported here. A host where this " +
            "call succeeds is not the single-threaded runtime the other checks assume, so the check " +
            "has to make the unsupported call to be worth anything.")]
    private static String NoThreadCanStart()
    {
        try
        {
            new Thread(() => { }) { IsBackground = true }.Start();
            return "a thread STARTED, so this host is not single-threaded and the probe is not " +
                "exercising the browser arms";
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///   Every engine here takes the default execution mode on purpose: the point is that the engine
    ///   PICKS the inline path from the host's capability, so forcing the mode would test the
    ///   configuration rather than the detection.
    /// </summary>
    private static Fallen8 NewEngine() => new Fallen8(NullLoggerFactory.Instance);

    private static Int32 NewVertex(Fallen8 engine, String label)
    {
        var tx = new CreateVerticesTransaction();
        tx.AddVertex(new VertexDefinition { Label = label, CreationDate = 0 });
        engine.EnqueueTransaction(tx).WaitUntilFinished();
        return tx.GetCreatedVertices().Single().Id;
    }

    /// <summary>Runs one check. The body returns null when it passed, or the reason it did not.</summary>
    private static void Check(String what, Func<String> body)
    {
        String failure;
        try
        {
            failure = body();
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (failure == null)
        {
            Console.WriteLine("  PASS  " + what);
            return;
        }

        _failures++;
        Console.WriteLine("  FAIL  " + what);
        Console.WriteLine("        " + failure);
    }
}
