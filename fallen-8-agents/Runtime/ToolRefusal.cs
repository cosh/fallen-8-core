// MIT License
//
// ToolRefusal.cs
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

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   A tool call that was refused rather than performed, as the value the tool returns.
    ///
    ///   <para>
    ///     <b>Why a value, and not text and not an exception.</b> A refusal has two readers with
    ///     opposite needs. The MODEL has to be told in words on this turn, or it cannot act on the
    ///     cap (delegate less, await what it has). The RECORD has to show a call that did not
    ///     happen, or a trace step, a <c>toolCalled</c> event and <c>f8a.agents.tool.calls</c> all
    ///     report a breached cap as work that was done. Plain text serves the model and lies to the
    ///     record. A thrown exception serves the record and costs the run: the framework turns it
    ///     into an error result and counts it against
    ///     <c>FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest</c>, which is 3 and is
    ///     left at its default, so a model that keeps asking ends the RUN rather than the turn.
    ///     This type is how one return does both: <see cref="AgentRunner" />'s invoker journals it
    ///     as a failed call and hands <see cref="Message" /> to the framework as an ordinary
    ///     result, so nothing counts as an error and the turn continues.
    ///   </para>
    ///   <para>
    ///     The message is the refusing component's own, in the operator's words. It lands in the
    ///     trace step's <c>error</c> and is what the model was told; the step carries no
    ///     <c>result</c>, because there was none.
    ///   </para>
    ///   <para>
    ///     <b>A tool that returns this must be built with an identity
    ///     <c>AIFunctionFactoryOptions.MarshalResult</c>.</b> The factory's default serializes a
    ///     return value to JSON, and a refusal that arrived as a <c>JsonElement</c> would be
    ///     indistinguishable from any other result. <see cref="SwarmTools.Tools" /> does that where
    ///     the tool is built.
    ///   </para>
    /// </summary>
    public sealed class ToolRefusal
    {
        /// <param name="message">Why the call was refused, in words the model can act on.</param>
        public ToolRefusal(String message)
        {
            Message = String.IsNullOrWhiteSpace(message)
                ? "This call was refused and no reason was given."
                : message;
        }

        /// <summary>Why the call was refused. Reaches the model as the tool's result and the trace
        /// step as its <c>error</c>.</summary>
        public String Message
        {
            get;
        }
    }
}
