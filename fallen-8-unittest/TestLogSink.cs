// MIT License
//
// TestLogSink.cs
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
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Records the log entries an engine emits, so a test can pin that a decision was reported
    ///   LOUDLY (an error) rather than taken silently. Hand <see cref="CreateFactory"/> to whatever
    ///   takes an <see cref="ILoggerFactory"/>; entries from every category land in this one sink.
    ///
    ///   <para>THE log-capturing provider for this suite: <c>public</c> and file-scoped to nothing, so a
    ///   test that needs to read what was logged uses this instead of growing another private
    ///   <c>CapturingLoggerProvider</c>. <see cref="Entries"/> is the raw record (level + formatted
    ///   message) and <see cref="Contains"/> the usual assertion over it.</para>
    /// </summary>
    public sealed class TestLogSink : ILoggerProvider
    {
        private readonly Object _gate = new Object();
        private readonly List<(LogLevel Level, String Message)> _entries = new List<(LogLevel, String)>();

        /// <summary>
        ///   A logger factory writing into this sink INSTEAD of the console, at
        ///   <see cref="LogLevel.Trace"/> so nothing a test asserts on is filtered away.
        /// </summary>
        public ILoggerFactory CreateFactory()
        {
            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(this);
            });
        }

        /// <summary>A point-in-time copy of what has been logged so far.</summary>
        public IReadOnlyList<(LogLevel Level, String Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return new List<(LogLevel, String)>(_entries);
                }
            }
        }

        /// <summary>
        ///   Whether an entry of <paramref name="level"/> (or worse) contains every one of
        ///   <paramref name="fragments"/>, compared ordinally.
        /// </summary>
        public Boolean Contains(LogLevel level, params String[] fragments)
        {
            foreach (var entry in Entries)
            {
                if (entry.Level < level || entry.Message == null)
                {
                    continue;
                }

                var all = true;
                foreach (var fragment in fragments)
                {
                    if (!entry.Message.Contains(fragment, StringComparison.Ordinal))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    return true;
                }
            }

            return false;
        }

        public ILogger CreateLogger(String categoryName)
        {
            return new SinkLogger(this);
        }

        public void Dispose()
        {
        }

        private void Record(LogLevel level, String message)
        {
            lock (_gate)
            {
                _entries.Add((level, message));
            }
        }

        private sealed class SinkLogger : ILogger
        {
            private readonly TestLogSink _owner;

            public SinkLogger(TestLogSink owner)
            {
                _owner = owner;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return NullScope.Instance;
            }

            public Boolean IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, String> formatter)
            {
                _owner.Record(logLevel, formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new NullScope();

            public void Dispose()
            {
            }
        }
    }
}
