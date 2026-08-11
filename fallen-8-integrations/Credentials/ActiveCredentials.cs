// MIT License
//
// ActiveCredentials.cs
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
using System.Threading;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   What credential values runs are currently HOLDING, counted by VALUE - and therefore what
    ///   redaction substitutes against.
    ///
    ///   <para>Counted by value rather than per run because two instances can be configured against the
    ///   same credential, so per-run counting would switch the other run's redaction off the moment the
    ///   first run completed. With no run in flight the set is empty, so the process's common state is a
    ///   filter with nothing to do.</para>
    /// </summary>
    public sealed class ActiveCredentials
    {
        private readonly Object _gate = new Object();
        private readonly Dictionary<String, Int32> _held = new Dictionary<String, Int32>(StringComparer.Ordinal);

        /// <summary>
        ///   Snapshot cache, so the logging hot path does not take the lock per line. Held as a REFERENCE
        ///   (rather than an <c>ImmutableArray</c>, which a field cannot declare volatile) and published with
        ///   <see cref="Volatile" />, so a reader either sees the previous list or the new one and never a
        ///   half-built one.
        /// </summary>
        private IReadOnlyList<String> _snapshot = Array.Empty<String>();

        /// <summary>Records that a run holds <paramref name="value"/>. Blank values are ignored.</summary>
        public void Hold(String? value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                _held.TryGetValue(value!, out var count);
                _held[value!] = count + 1;
                Resnapshot();
            }
        }

        /// <summary>Records that a run has stopped holding <paramref name="value"/>.</summary>
        public void Release(String? value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_gate)
            {
                if (!_held.TryGetValue(value!, out var count))
                {
                    return;
                }

                if (count <= 1)
                {
                    _held.Remove(value!);
                }
                else
                {
                    _held[value!] = count - 1;
                }

                Resnapshot();
            }
        }

        /// <summary>
        ///   The values to substitute, LONGEST FIRST: a short credential that happens to be a substring
        ///   of a longer one would otherwise leave the longer one's tail in the line.
        /// </summary>
        public IReadOnlyList<String> Snapshot()
        {
            return Volatile.Read(ref _snapshot);
        }

        /// <summary>Whether anything is being held, so redaction can skip work entirely.</summary>
        public Boolean IsEmpty => Snapshot().Count == 0;

        private void Resnapshot()
        {
            var values = new List<String>(_held.Keys);
            values.Sort((left, right) =>
            {
                var byLength = right.Length.CompareTo(left.Length);
                return byLength != 0 ? byLength : String.CompareOrdinal(left, right);
            });
            Volatile.Write(ref _snapshot, values);
        }
    }
}
