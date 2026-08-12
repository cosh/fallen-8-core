// MIT License
//
// DurableFileIoTest.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Covers <c>DurableFileIo.PublishWithRetry</c> - the publish half of the durable-file discipline,
    ///   which every temp-to-final rename in the engine goes through - and, through a real checkpoint,
    ///   the engine's use of it. It had no tests at all, so reverting any call site to a bare
    ///   <c>File.Move</c> failed nothing. WHICH refusals are retried and which are not is stated once, on
    ///   <c>DurableFileIo.IsTransientRefusal</c>; these tests only pin the behaviour.
    ///
    ///   <para>No test here makes a timing assumption: the held destination handle is released from the
    ///   retry log callback itself, so attempt two is the first attempt that can succeed.</para>
    /// </summary>
    [TestClass]
    public class DurableFileIoTest
    {
        private String _dir;

        [TestInitialize]
        public void TestInitialize()
        {
            _dir = Path.Combine(Path.GetTempPath(), "f8_dfio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        #region reaching the internal publish (the engine declares no InternalsVisibleTo)

        private static readonly MethodInfo _publishWithRetry = typeof(DurableFileIo)
            .GetMethod("PublishWithRetry", BindingFlags.NonPublic | BindingFlags.Static);

        private static void Publish(String temp, String path, ILogger logger)
        {
            try
            {
                _publishWithRetry.Invoke(null, new Object[] { temp, path, logger });
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        /// <summary>
        ///   Counts the retry notifications and, optionally, reacts to the first one - which is what makes
        ///   the "refused once, then allowed" case deterministic instead of a sleep race.
        ///
        ///   <para>THREAD-SAFE, because one instance is handed to every engine component through
        ///   <see cref="WatchingLoggerFactory" /> and a checkpoint's sidecar fan-out logs from pooled
        ///   tasks: the counter is interlocked (which also makes "the first retry" fire exactly once) and
        ///   the message list is a concurrent queue.</para>
        /// </summary>
        private sealed class RetryWatcher : ILogger
        {
            private readonly Action _onFirstRetry;

            private Int32 _retries;

            private readonly ConcurrentQueue<String> _otherMessages = new ConcurrentQueue<String>();

            internal Int32 Retries => Volatile.Read(ref _retries);

            internal IReadOnlyCollection<String> OtherMessages => _otherMessages;

            internal RetryWatcher(Action onFirstRetry = null)
            {
                _onFirstRetry = onFirstRetry;
            }

            public IDisposable BeginScope<TState>(TState state) => null;

            public Boolean IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                                    Func<TState, Exception, String> formatter)
            {
                var message = formatter == null ? String.Empty : formatter(state, exception);

                // Matched on the message, not merely on the level: a real engine run logs plenty of other
                // debug lines, and reacting to one of those would release the destination before the
                // rename it is supposed to refuse.
                if (logLevel == LogLevel.Debug && message.StartsWith("Publishing", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref _retries) == 1)
                    {
                        _onFirstRetry?.Invoke();
                    }

                    return;
                }

                _otherMessages.Enqueue(message);
            }
        }

        /// <summary>Feeds <see cref="RetryWatcher" /> to every engine component, so a checkpoint's own
        /// publish notifications are observable.</summary>
        private sealed class WatchingLoggerFactory : ILoggerFactory
        {
            private readonly ILogger _logger;

            internal WatchingLoggerFactory(ILogger logger)
            {
                _logger = logger;
            }

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public ILogger CreateLogger(String categoryName) => _logger;

            public void Dispose()
            {
            }
        }

        private String WriteTemp(String content)
        {
            var temp = Path.Combine(_dir, "payload" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, content);
            return temp;
        }

        #endregion

        [TestMethod]
        public void PublishWithRetry_RetriesARefusedRename_AndThenPublishes()
        {
            var target = Path.Combine(_dir, "manifest.json");
            File.WriteAllText(target, "old");
            var temp = WriteTemp("new");

            var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var watcher = new RetryWatcher(held.Dispose);

            Publish(temp, target, watcher);

            Assert.AreEqual(1, watcher.Retries,
                "the held destination must refuse attempt one, so exactly one retry is expected");
            Assert.AreEqual("new", File.ReadAllText(target),
                "the retried publish must leave the NEW content at the destination");
            Assert.IsFalse(File.Exists(temp), "a published temp file must be gone");
        }

        [TestMethod]
        public void PublishWithRetry_DoesNotRetryAMissingTempFile()
        {
            var missing = Path.Combine(_dir, "never-written.tmp");
            var target = Path.Combine(_dir, "target.json");
            var watcher = new RetryWatcher();

            Assert.ThrowsException<FileNotFoundException>(() => Publish(missing, target, watcher));

            Assert.AreEqual(0, watcher.Retries, "a missing temp file must not be retried at all");
        }

        [TestMethod]
        public void PublishWithRetry_DoesNotRetryAMissingDirectory()
        {
            var temp = WriteTemp("payload");
            var target = Path.Combine(_dir, "no-such-directory", "target.json");
            var watcher = new RetryWatcher();

            Assert.ThrowsException<DirectoryNotFoundException>(() => Publish(temp, target, watcher));

            Assert.AreEqual(0, watcher.Retries, "a bad path fails identically on every attempt");
        }

        [TestMethod]
        public void PublishWithRetry_GivesUpAfterTheAttemptCap()
        {
            var target = Path.Combine(_dir, "manifest.json");
            File.WriteAllText(target, "old");
            var temp = WriteTemp("new");
            var watcher = new RetryWatcher();

            Exception thrown = null;
            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    Publish(temp, target, watcher);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            }

            Assert.IsNotNull(thrown, "a destination that stays held must eventually surface the real error");
            Assert.IsTrue(thrown is IOException || thrown is UnauthorizedAccessException,
                "the refusal itself must reach the caller, not a substitute: " + thrown);
            Assert.AreEqual(4, watcher.Retries,
                "the retry is BOUNDED at five attempts, so exactly four of them are retries");
            Assert.AreEqual("old", File.ReadAllText(target),
                "a publish that never succeeded must leave the previous file untouched");
        }

        [TestMethod]
        public void ReplaceAllTextDurably_RemovesItsTempFile_WhenThePublishNeverSucceeds()
        {
            var target = Path.Combine(_dir, "registry.json");
            File.WriteAllText(target, "old");
            var watcher = new RetryWatcher();

            Exception thrown = null;
            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    DurableFileIo.ReplaceAllTextDurably(target, "new", watcher);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                Assert.IsNotNull(thrown, "the caller must learn that the pointer file was NOT replaced");
                Assert.AreEqual(1, Directory.GetFiles(_dir).Length,
                    "a failed attempt must not litter the directory with a temp file: " +
                    String.Join(", ", Directory.GetFiles(_dir)));
            }

            Assert.AreEqual("old", File.ReadAllText(target), "the old pointer must still be readable");
        }

        [TestMethod]
        public void ReplaceAllTextDurably_WritesTheContent_AndKeepsNoTempFile()
        {
            var target = Path.Combine(_dir, "registry.json");

            DurableFileIo.ReplaceAllTextDurably(target, "first", null);
            DurableFileIo.ReplaceAllTextDurably(target, "second", null);

            Assert.AreEqual("second", File.ReadAllText(target));
            CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(_dir),
                "the temp name must never survive a successful publish");
        }

        [TestMethod]
        public void ACheckpointSurvivesARefusedSidecarRename()
        {
            // The checkpoint's OWN temp-to-final renames must take the same retry the WAL's do. The
            // refusal is injected into a REAL save by holding the sidecar that a second save republishes;
            // the sidecar NAME comes from the first save rather than from arithmetic here, so what is held
            // is whatever the engine itself named.
            FileStream held = null;
            var watcher = new RetryWatcher(() => held?.Dispose());

            try
            {
                using var fallen8 = new Fallen8(new WatchingLoggerFactory(watcher));
                var create = new CreateVerticesTransaction();
                create.AddVertex(new VertexDefinition { Label = "device", CreationDate = 0 });
                fallen8.EnqueueTransaction(create).WaitUntilFinished();

                var path = Path.Combine(_dir, "checkpoint.f8s");
                Assert.AreEqual(TransactionState.Finished, Save(fallen8, path), "the first save must finish");

                var sidecar = Directory.GetFiles(
                    _dir, Path.GetFileName(path) + Constants.GraphElementsSaveString + "*");
                Assert.AreEqual(1, sidecar.Length,
                    "one vertex saves as exactly one graph-element sidecar: " + String.Join(", ", sidecar));

                // Removing the header makes the next save reuse the same base path, and therefore
                // republish over the sidecar left behind here.
                File.Delete(path);
                held = new FileStream(sidecar[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                Assert.AreEqual(TransactionState.Finished, Save(fallen8, path),
                    "a refused sidecar rename must not roll back a save whose bytes are already durable");
                Assert.IsTrue(watcher.Retries >= 1,
                    "the held sidecar must actually have refused a rename, or this test proves nothing");
            }
            finally
            {
                held?.Dispose();
            }
        }

        [TestMethod]
        public void TheWatcherKeepsEveryNotification_WhenTheEngineLogsFromSeveralThreads()
        {
            // ACheckpointSurvivesARefusedSidecarRename hands ONE watcher to every engine component, and a
            // checkpoint's sidecar fan-out publishes from pooled tasks - so the watcher's own state is
            // written concurrently. This pins that: non-atomic counting loses notifications, a plain
            // List.Add loses or corrupts messages (or throws), and a read-after-increment "is this the
            // first?" test can fire the callback more than once or never.
            const Int32 threads = 8;
            const Int32 perThread = 5000;

            var firstRetryCalls = 0;
            var watcher = new RetryWatcher(() => Interlocked.Increment(ref firstRetryCalls));
            var failures = new ConcurrentQueue<Exception>();
            var start = new ManualResetEventSlim(false);
            var workers = new Thread[threads];

            for (var t = 0; t < threads; t++)
            {
                workers[t] = new Thread(() =>
                {
                    start.Wait();

                    try
                    {
                        for (var i = 0; i < perThread; i++)
                        {
                            LogTo(watcher, LogLevel.Debug, "Publishing \"sidecar\" was refused on attempt 1 of 5");
                            LogTo(watcher, LogLevel.Information, "an ordinary engine line");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                });

                workers[t].Start();
            }

            start.Set();
            foreach (var worker in workers)
            {
                worker.Join();
            }

            Assert.AreEqual(0, failures.Count,
                "logging from several threads must not throw: " + (failures.TryPeek(out var first) ? first.ToString() : ""));
            Assert.AreEqual(threads * perThread, watcher.Retries, "every retry notification must be counted");
            Assert.AreEqual(threads * perThread, watcher.OtherMessages.Count, "every other message must be kept");
            Assert.AreEqual(1, firstRetryCalls, "the first-retry reaction must run exactly once");
        }

        private static void LogTo(ILogger logger, LogLevel level, String message)
        {
            logger.Log(level, new EventId(0), message, null, (state, _) => state);
        }

        private static TransactionState Save(Fallen8 fallen8, String path)
        {
            var info = fallen8.EnqueueTransaction(new SaveTransaction { Path = path });
            info.WaitUntilFinished();
            Assert.IsNull(info.Error, "the save reported: " + info.Error);
            return info.TransactionState;
        }
    }
}
