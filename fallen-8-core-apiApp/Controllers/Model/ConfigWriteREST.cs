// MIT License
//
// ConfigWriteREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   A batch of configuration writes (feature writable-instance-config). Every key is validated
    ///   before ANY of them is stored, so a batch either applies whole or changes nothing.
    /// </summary>
    public sealed class ConfigWriteSpecification
    {
        /// <summary>
        ///   The keys to write, as configuration text keyed by full configuration key. A <c>null</c>
        ///   value CLEARS the stored override and restores whatever layer sits below it, which is the
        ///   undo, and is why this surface needs no versioning or history.
        /// </summary>
        [JsonPropertyName("settings")]
        public Dictionary<String, String> Settings
        {
            get; set;
        }
    }

    /// <summary>
    ///   What one written key ended up as (feature writable-instance-config). The value is read back off
    ///   the freshly bound options rather than echoed from the request, which is the only way a value the
    ///   options class coerced becomes visible instead of assumed.
    /// </summary>
    public sealed class ConfigWriteResultREST
    {
        /// <summary>The configuration key that was written.</summary>
        [JsonPropertyName("key")]
        public String Key
        {
            get; set;
        }

        /// <summary>
        ///   The value now in effect, read back after binding. It can differ from the value sent: several
        ///   options clamp in their setter, so asking for 0 can legitimately answer with the default.
        /// </summary>
        [JsonPropertyName("value")]
        public String Value
        {
            get; set;
        }

        /// <summary>
        ///   Whether the value sent was changed on the way in (a clamp or a reset). Named explicitly so a
        ///   client can say "stored, adjusted to X" rather than appearing to ignore the operator.
        /// </summary>
        [JsonPropertyName("coerced")]
        public Boolean Coerced
        {
            get; set;
        }

        /// <summary>Whether the override was removed rather than set.</summary>
        [JsonPropertyName("cleared")]
        public Boolean Cleared
        {
            get; set;
        }

        /// <summary>
        ///   When the value takes effect: <c>live</c>, <c>liveForNewWork</c> or <c>restart</c>. This is
        ///   the honest promise, so a client never reports a restart-tier write as applied.
        /// </summary>
        [JsonPropertyName("applyMode")]
        public String ApplyMode
        {
            get; set;
        }

        /// <summary>Whether this key is now waiting for a restart to take effect.</summary>
        [JsonPropertyName("restartPending")]
        public Boolean RestartPending
        {
            get; set;
        }

        /// <summary>
        ///   Why a live key did not reach the running process, or absent when it did. The value is stored
        ///   either way and the next boot will use it, so this reports a live apply that failed rather than
        ///   a write that failed, and <see cref="ApplyMode"/> is downgraded to <c>restart</c> to match.
        /// </summary>
        [JsonPropertyName("applyFailure")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String ApplyFailure
        {
            get; set;
        }
    }

    /// <summary>
    ///   The result of one <c>PATCH /config</c>: what each written key became, plus the instance's whole
    ///   pending-restart set, so a client can render the restart banner without a second round trip.
    /// </summary>
    public sealed class ConfigWriteREST
    {
        /// <summary>One result per written key, in the order the catalog lists them.</summary>
        [JsonPropertyName("results")]
        public List<ConfigWriteResultREST> Results
        {
            get; set;
        }

        /// <summary>
        ///   Every key whose configured value now differs from the value this process started with. The
        ///   same set <c>GET /config</c> publishes, included here so saving a restart-tier setting shows
        ///   its consequence immediately.
        /// </summary>
        [JsonPropertyName("pendingRestart")]
        public List<PendingRestartREST> PendingRestart
        {
            get; set;
        }
    }
}
