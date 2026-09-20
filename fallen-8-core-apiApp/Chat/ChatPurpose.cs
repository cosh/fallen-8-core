// MIT License
//
// ChatPurpose.cs
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

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   What a chat completion is FOR, which is how the server picks the model that serves it
    ///   (feature agent-host). The set is closed in code and small on purpose: every caller of a
    ///   purpose is code in this repository, so a purpose is a contract between two parts of the
    ///   product rather than an extension point.
    ///   <para>
    ///     A purpose selects a name out of
    ///     <see cref="Configuration.Fallen8ChatOptions.ModelPurposes" />, which is where the reason
    ///     for having purposes at all is written down. It carries no behaviour of its own - no
    ///     prompt, no sampling, no stop sequences - so adding one is one enum member and one
    ///     setting per backend, and nothing else.
    ///   </para>
    /// </summary>
    public enum ChatPurpose
    {
        /// <summary>Studio's NL assist, and anything that asks for a completion without saying why.
        /// The default, so a request that omits the field behaves exactly as it did before purposes
        /// existed.</summary>
        Assist = 0,

        /// <summary>A tool-calling conversation driven by the agent host. Needs a model that can
        /// call tools, which the assist fine-tune deliberately is not.</summary>
        Agent = 1,
    }

    /// <summary>Reading a <see cref="ChatPurpose" /> off the wire.</summary>
    public static class ChatPurposes
    {
        /// <summary>The accepted wire spellings, in the order a refusal lists them.</summary>
        public static readonly String[] Accepted = { "assist", "agent" };

        /// <summary>
        ///   Parses the <c>purpose</c> field of a chat request. An absent or empty value is
        ///   <see cref="ChatPurpose.Assist" />, which is what keeps this an addition rather than a
        ///   change; anything else that is not a known name is REFUSED rather than defaulted,
        ///   because silently serving the assist model to an agent would answer a tool-calling
        ///   conversation with a model trained to emit one C# fragment.
        ///   <para>
        ///     Case is forgiven, unlike the backend selector, and the two are different kinds of
        ///     value: the selector is operator configuration where a typo must never resolve to
        ///     something plausible, while this is a field a client sends on every request and there
        ///     is no second purpose a different casing could be confused for.
        ///   </para>
        /// </summary>
        public static Boolean TryParse(String value, out ChatPurpose purpose)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                purpose = ChatPurpose.Assist;
                return true;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "assist":
                    purpose = ChatPurpose.Assist;
                    return true;

                case "agent":
                    purpose = ChatPurpose.Agent;
                    return true;

                default:
                    purpose = ChatPurpose.Assist;
                    return false;
            }
        }

        /// <summary>The configuration leaf a purpose reads, for a message that names the key an
        /// operator has to set rather than describing it.</summary>
        public static String SettingName(ChatPurpose purpose)
        {
            return purpose == ChatPurpose.Agent ? "Agent" : "Assist";
        }
    }
}
