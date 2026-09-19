// MIT License
//
// RoleCatalog.cs
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
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   The three roles an agent can be spawned as, each one a prompt plus a tool allowlist.
    ///
    ///   <para>
    ///     <b>The prompt is the load-bearing part, which is why it is embedded and refused when
    ///     empty.</b> Phase 0 measured the same model, tool schema and temperature producing parsed
    ///     tool calls under a prompt that named the tools and forbade invented results, and
    ///     producing a FABRICATED result under a bare imperative. A fabricated result is the worst
    ///     failure this feature has, because it looks like an answer. So a missing or blank prompt
    ///     stops the process at startup rather than degrading it into a confident liar.
    ///   </para>
    ///   <para>
    ///     <b>That measurement does NOT hold for the model this host ships with, and this is the
    ///     one home that says so</b> (spec 3.2a's amendment; findings.md section 1). Measured
    ///     later and more carefully against the stock agent model: with no instructions at all it
    ///     calls the tool, and with ANY instruction text present it emits the literal text of a
    ///     tool-call marker followed by an invented result. The polarity is reversed, and our own
    ///     gateway is exonerated, because a request sent direct to the platform behaves
    ///     identically.
    ///   </para>
    ///   <para>
    ///     So what may be claimed for a role prompt is narrower than it was: it carries the honesty
    ///     properties a reviewer depends on (never invent a result, cite the call a figure came
    ///     from, one tool at a time) and it is what a caller must not be able to overwrite. What it
    ///     may no longer be called is the thing that makes tool calling work. Four other sites
    ///     asserted the pre-amendment claim as live fact and now point here instead, one of them
    ///     through the published OpenAPI document.
    ///   </para>
    ///   <para>
    ///     <b>The allowlist narrows and can never widen.</b> It is applied to the tool list handed
    ///     to the agent, so a tool outside it is not merely discouraged by prose: the model never
    ///     sees it and cannot name it. The MCP server's own tiers remain the outer bound, enforced
    ///     server-side, so a widened list here buys nothing.
    ///   </para>
    /// </summary>
    public sealed class RoleCatalog
    {
        /// <summary>The role a spawn request gets when it names none.</summary>
        public const String DefaultRole = "assistant";

        /// <summary>
        ///   The one entry in <c>Agents:Roles:&lt;role&gt;:Tools</c> that WIDENS: it means every
        ///   tool the MCP server advertises, whatever the role ships with.
        ///   <para>
        ///     It exists because there was otherwise no way to say it. An absent or empty list keeps
        ///     the role's shipped allowlist, so widening the orchestrator meant enumerating the
        ///     server's tools, and such a list is wrong again the moment a tier is enabled. It
        ///     widens only as far as that server advertises: the tiers are the outer bound and are
        ///     enforced server-side.
        ///   </para>
        /// </summary>
        public const String EveryTool = "*";

        private static readonly String[] Known = { "assistant", "orchestrator", "worker" };

        /// <summary>
        ///   The shipped allowlists. <c>assistant</c> and <c>worker</c> see everything the MCP
        ///   server advertises, so their entries are absent rather than exhaustive: a list here
        ///   would go stale the moment the server gains a tool, and going stale would silently take
        ///   a capability away.
        ///   <para>
        ///     <c>orchestrator</c> is narrowed on purpose. An orchestrator that can look but must
        ///     delegate decomposes better than one that can do everything itself, so it gets the
        ///     overview read and the swarm tools and nothing else. The swarm tools are not listed:
        ///     they are not MCP tools, <see cref="SwarmTools" /> implements them, and the runner
        ///     appends them after this filter runs.
        ///   </para>
        /// </summary>
        private static readonly IReadOnlyDictionary<String, String[]> ShippedAllowlists =
            new Dictionary<String, String[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["orchestrator"] = new[] { "f8_overview" },
            };

        private readonly IReadOnlyDictionary<String, AgentRole> _roles;

        private RoleCatalog(IReadOnlyDictionary<String, AgentRole> roles)
        {
            _roles = roles;
        }

        /// <summary>The role names, for the startup line and for the message a bad request gets.</summary>
        public IReadOnlyCollection<String> Names => (IReadOnlyCollection<String>)_roles.Keys;

        /// <summary>
        ///   Reads the three embedded prompts and folds the configured allowlists over the shipped
        ///   ones.
        /// </summary>
        /// <exception cref="InvalidOperationException">A prompt is missing from the assembly or is
        /// blank. Deliberately fatal; see the type's remarks.</exception>
        public static RoleCatalog Load(AgentsOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // A key under Agents:Roles that names no role is FATAL, for the reason the options
            // type gives for its own shape: an allowlist that silently fails to narrow is worse
            // than one that refuses to load. Nothing enumerated these keys, so a misspelled role
            // name was ignored and the role it was meant to hold to reads kept every tool the MCP
            // server advertises, with the status route showing exactly what a default host shows.
            var unknown = options.Roles.Keys
                .Where(key => !Known.Contains(key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(String.Format(
                    "Agents:Roles names no role called {0}. The roles are {1}. A misspelled role "
                    + "name would otherwise be ignored, leaving the role it was meant to narrow "
                    + "holding every tool the MCP server advertises.",
                    String.Join(", ", unknown.Select(name => "'" + name + "'")),
                    String.Join(", ", Known)));
            }

            var roles = new Dictionary<String, AgentRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Known)
            {
                roles[name] = new AgentRole(name, Prompt(name), Allowed(name, options));
            }

            return new RoleCatalog(new ReadOnlyDictionary<String, AgentRole>(roles));
        }

        /// <summary>
        ///   Whether a CALLER may spawn this role over the control plane. ONE role may not:
        ///   <c>worker</c> answers to an orchestrator and reports a typed result to it, so one
        ///   spawned by a caller has nobody to answer, and its prompt tells it not to address the
        ///   user (spec section 3.2). The refusal names that rather than pretending the role does
        ///   not exist, because it is a real role with a real prompt that an orchestrator's
        ///   <c>spawn_worker</c> uses.
        ///   <para>
        ///     <c>orchestrator</c> IS spawnable. This said it was refused because the two tools it
        ///     delegates with were not attached, while the body already returned true for it:
        ///     <see cref="SwarmTools" /> ships them and the runner appends them for that role
        ///     alone, so the agent has what its prompt tells it to delegate with.
        ///   </para>
        /// </summary>
        public static Boolean IsSpawnableByACaller(String role, out String problem)
        {
            problem = String.Empty;

            if (String.Equals(role, "worker", StringComparison.OrdinalIgnoreCase))
            {
                problem = "A worker is spawned by an orchestrator, not over this API: it reports a "
                    + "typed result to the orchestrator that gave it its part of a task, and one "
                    + "spawned here would have nobody to report to. Spawn an assistant instead.";
                return false;
            }

            return true;
        }

        /// <summary>The named role, or false with the accepted set named for the caller.</summary>
        public Boolean TryGet(String? name, out AgentRole role, out String problem)
        {
            role = null!;
            problem = String.Empty;

            var wanted = String.IsNullOrWhiteSpace(name) ? DefaultRole : name.Trim();
            if (_roles.TryGetValue(wanted, out var found))
            {
                role = found;
                return true;
            }

            problem = String.Format("Unknown role '{0}'. Accepted roles are {1}.", wanted,
                String.Join(", ", Known));
            return false;
        }

        /// <summary>
        ///   The allowlist one role ends up with, folding what is configured over what ships.
        ///   <para>
        ///     A configured list REPLACES the shipped one rather than adding to it, because the only
        ///     reason to configure one is to say "this role may use exactly these". Blank entries
        ///     are dropped BEFORE that decision is made: they used to pass the count check and then
        ///     filter down to nothing, so a list holding one empty element lifted the role's
        ///     allowlist entirely, which is the opposite of what a typo should do.
        ///   </para>
        ///   <para>
        ///     An empty result is how "nothing narrows this role" is represented downstream, which
        ///     is what <see cref="AgentRole.Filter" /> and the status route both read.
        ///   </para>
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="EveryTool" /> beside a tool name.
        /// Deliberately fatal, like a missing prompt and like a role key that names no role: the two
        /// are opposite instructions, and an allowlist that silently fails to narrow is worse than
        /// one that refuses to load.</exception>
        private static IReadOnlyList<String> Allowed(String role, AgentsOptions options)
        {
            var named = (options.Roles.TryGetValue(role, out var listed) ? listed?.Tools : null)
                ?.Where(t => !String.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray()
                ?? Array.Empty<String>();

            if (named.Length == 0)
            {
                return ShippedAllowlists.TryGetValue(role, out var shipped)
                    ? shipped
                    : Array.Empty<String>();
            }

            if (!named.Contains(EveryTool, StringComparer.Ordinal))
            {
                return named;
            }

            // Counted over the names that NARROW, so the refusal states what is actually wrong.
            // Subtracting one from the length said "beside 1 tool names" for a list of two stars,
            // which names neither the problem nor the number.
            var narrowing = named.Count(t => !String.Equals(t, EveryTool, StringComparison.Ordinal));
            if (narrowing > 0)
            {
                throw new InvalidOperationException(String.Format(
                    "Agents:Roles:{0}:Tools names '{1}' beside {2} tool {3}. '{1}' means every tool "
                    + "the MCP server advertises and cannot be combined with a list that narrows.",
                    role, EveryTool, narrowing, narrowing == 1 ? "name" : "names"));
            }

            return Array.Empty<String>();
        }

        private static String Prompt(String role)
        {
            var assembly = typeof(RoleCatalog).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Prompts." + role + ".md", StringComparison.Ordinal));

            if (resource == null)
            {
                throw new InvalidOperationException(String.Format(
                    "The embedded prompt for role '{0}' is missing from {1}.", role,
                    assembly.GetName().Name));
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(String.Format(
                    "The embedded prompt for role '{0}' could not be read.", role));
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            if (String.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(String.Format(
                    "The embedded prompt for role '{0}' is blank. A role with no prompt fabricates "
                    + "tool results instead of calling tools, so this host does not start without it.",
                    role));
            }

            return text.Trim();
        }
    }

    /// <summary>One role: its name, its prompt and the tools it may see.</summary>
    public sealed class AgentRole
    {
        internal AgentRole(String name, String prompt, IReadOnlyList<String> allowedTools)
        {
            Name = name;
            Prompt = prompt;
            AllowedTools = allowedTools;
        }

        public String Name
        {
            get;
        }

        /// <summary>The system prompt, verbatim from the embedded file.</summary>
        public String Prompt
        {
            get;
        }

        /// <summary>The MCP tool names this role may see, after configuration is folded over what it
        /// ships with (<see cref="RoleCatalog.EveryTool" /> and an absent key both land here). Empty
        /// means every advertised tool.</summary>
        public IReadOnlyList<String> AllowedTools
        {
            get;
        }

        /// <summary>
        ///   The tools this role gets, out of everything the MCP server advertised. An empty
        ///   allowlist passes the list through; a non-empty one keeps the named tools and drops the
        ///   rest.
        ///   <para>
        ///     A named tool the server does NOT advertise is silently absent, and that is the
        ///     correct reading: the allowlist says what this role may use, not what must exist.
        ///     Which tools a role actually ended up with is reported by the status route, so an
        ///     allowlist that names nothing real is visible rather than merely harmless.
        ///   </para>
        /// </summary>
        public IReadOnlyList<AITool> Filter(IEnumerable<AITool> advertised)
        {
            if (advertised == null)
            {
                throw new ArgumentNullException(nameof(advertised));
            }

            if (AllowedTools.Count == 0)
            {
                return advertised.ToList();
            }

            var wanted = new HashSet<String>(AllowedTools, StringComparer.OrdinalIgnoreCase);
            return advertised.Where(t => wanted.Contains(t.Name)).ToList();
        }
    }
}
