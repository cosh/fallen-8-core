// MIT License
//
// RunGate.cs
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

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   ONE JOB AT A TIME PER IDENTITY, refused with a conflict rather than queued.
    ///
    ///   <para>Two concurrent runs under one identity both resolve against the graph as it was before either
    ///   wrote, so both create the elements the other is creating: the duplicate-everything failure, with no
    ///   index ever going missing. It REFUSES rather than queueing because the caller is waiting on this call.</para>
    /// </summary>
    public sealed class RunGate
    {
        private readonly Object _gate = new Object();
        private readonly HashSet<String> _running = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        ///   Claims the identity for the duration of a run. Dispose the returned token to release it.
        /// </summary>
        /// <exception cref="JobRejectedException">Another run is already in flight for this identity. Nothing was
        /// read and nothing written, and the caller has something to fix.</exception>
        public IDisposable Enter(String instanceId)
        {
            if (String.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("An integration instance id is required.", nameof(instanceId));
            }

            lock (_gate)
            {
                if (!_running.Add(instanceId))
                {
                    throw new JobRejectedException(JobErrorKinds.Conflict, String.Format(
                        "A job is already running as '{0}'. Two concurrent runs under one identity both resolve " +
                        "against the graph as it was before either wrote, so both create the elements the other " +
                        "is creating.", instanceId));
                }
            }

            return new Token(this, instanceId);
        }

        private void Release(String instanceId)
        {
            lock (_gate)
            {
                _running.Remove(instanceId);
            }
        }

        private sealed class Token : IDisposable
        {
            private readonly RunGate _gate;
            private readonly String _instanceId;
            private Boolean _released;

            public Token(RunGate gate, String instanceId)
            {
                _gate = gate;
                _instanceId = instanceId;
            }

            public void Dispose()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                _gate.Release(_instanceId);
            }
        }
    }
}
