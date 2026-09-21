// MIT License
//
// AFallen8TargetOptions.cs
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

namespace NoSQL.GraphDB.Rest.Configuration
{
    /// <summary>
    ///   The Fallen-8 a REST-only deployable points at, in the part that is the same for all of
    ///   them: where it is, what credential reaches it, and how long one call may take.
    ///
    ///   <para>
    ///     <b>One section name for the operator, one shape per deployable.</b> Every consumer binds
    ///     the same <c>Fallen8Target</c> section, because somebody configuring three sidecars should
    ///     not have to learn three spellings. What they do NOT share is the whole shape: the MCP
    ///     server has a lab-only TLS escape hatch the other two deliberately refuse, and the
    ///     integrations runtime has a default namespace and an embed concurrency that mean nothing
    ///     to a host which writes no graph. So each deployable derives, adds its own knobs, and
    ///     documents those; this class is the part that was otherwise the same paragraph three
    ///     times.
    ///   </para>
    ///   <para>
    ///     <b>A caller's credential is never forwarded.</b> <see cref="ApiKey" /> is the
    ///     deployable's OWN single downstream identity: it asks as itself, so a job, a tool call or
    ///     an agent cannot reach past what that deployable may already do, and a graph audit trail
    ///     names one writer per sidecar rather than whoever submitted the work. It is never
    ///     surfaced to callers and never logged.
    ///   </para>
    /// </summary>
    public abstract class AFallen8TargetOptions
    {
        /// <summary>The configuration section every consumer binds this from.</summary>
        public const String SectionName = "Fallen8Target";

        /// <summary>
        ///   Sets the per-deployable default deadline.
        /// </summary>
        /// <param name="defaultTimeoutSeconds">
        ///   What <see cref="TimeoutSeconds" /> starts at when an operator sets nothing. It is a
        ///   constructor parameter rather than one shared number because each deployable's default
        ///   sits deliberately above a DIFFERENT downstream budget, and the derived property's own
        ///   documentation is where that comparison belongs.
        /// </param>
        protected AFallen8TargetOptions(Int32 defaultTimeoutSeconds)
        {
            TimeoutSeconds = defaultTimeoutSeconds;
        }

        /// <summary>The base URL, for example <c>http://fallen8:8080</c> in-network or an https URL
        /// cross-host.</summary>
        public String BaseUrl { get; set; } = "http://localhost:8080";

        /// <summary>The API key this deployable presents to Fallen-8. See the type's own summary for
        /// why it is the deployable's rather than the caller's.</summary>
        public String? ApiKey { get; set; }

        /// <summary>The header the key is sent under (default <c>X-Api-Key</c>, which the apiApp
        /// accepts alongside an <c>Authorization: Bearer</c> token).</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>
        ///   The per-request deadline on one call to the target, in seconds, as the operator wrote
        ///   it. Read <see cref="Deadline" /> rather than this to arm anything: the bounds are the
        ///   difference between what was configured and what is in force, and reporting the raw
        ///   number is how a host came to print "no deadline" while giving every call one second.
        ///   <para>
        ///     The default is each deployable's own and is documented on the derived property. All
        ///     three sit deliberately ABOVE the longest budget the apiApp applies to the route they
        ///     call, for one reason worth stating once: two competing deadlines make the NEARER one
        ///     report a vague local failure instead of the downstream answer that names which
        ///     server setting to change.
        ///   </para>
        /// </summary>
        public Int32 TimeoutSeconds { get; set; }

        /// <summary>
        ///   The deadline actually in force: <see cref="TimeoutSeconds" /> through
        ///   <see cref="OptionBounds" />' floor and ceiling. There is deliberately no way to switch
        ///   it off - a call with no deadline is a deployable that hangs - so a non-positive value
        ///   is a SHORT deadline rather than an absent one, and the failure it produces names this
        ///   key, which is the cheapest thing an operator can act on.
        /// </summary>
        public TimeSpan Deadline => OptionBounds.Seconds(TimeoutSeconds);
    }
}
