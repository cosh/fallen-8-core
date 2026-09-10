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
        ///     they are not MCP tools, the registry implements them, and they are added after this
        ///     filter runs.
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

            var roles = new Dictionary<String, AgentRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Known)
            {
                // A configured list REPLACES the shipped one rather than adding to it, because the
                // only reason to configure one is to say "this role may use exactly these".
                var configured = options.Roles.TryGetValue(name, out var listed) ? listed?.Tools : null;
                var allowed = configured is { Count: > 0 }
                    ? configured.Where(t => !String.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray()
                    : ShippedAllowlists.TryGetValue(name, out var shipped)
                        ? shipped
                        : Array.Empty<String>();

                roles[name] = new AgentRole(name, Prompt(name), allowed);
            }

            return new RoleCatalog(new ReadOnlyDictionary<String, AgentRole>(roles));
        }

        /// <summary>
        ///   Whether a CALLER may spawn this role over the control plane. Two roles exist that it may
        ///   not, for different reasons, and both would otherwise produce an agent that cannot do
        ///   what its prompt tells it to:
        ///   <list type="bullet">
        ///     <item><c>worker</c> answers to an orchestrator and reports a typed result to it. One
        ///     spawned by a caller has nobody to answer, and its prompt tells it not to address the
        ///     user (spec section 3.2).</item>
        ///     <item><c>orchestrator</c> works by delegating, and the two tools it delegates with are
        ///     not attached in this phase. One spawned now is an agent commanded to call tools it does
        ///     not have.</item>
        ///   </list>
        ///   Both are still real roles with real prompts and real allowlists, because the registry
        ///   and the runner serve them already; what is missing is the swarm, so the refusal names
        ///   that rather than pretending the role does not exist.
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

            if (String.Equals(role, "orchestrator", StringComparison.OrdinalIgnoreCase))
            {
                problem = "An orchestrator works by delegating to workers, and the tools it "
                    + "delegates with are not available on this host yet, so it would be instructed "
                    + "to call tools it does not have. Spawn an assistant instead.";
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

        /// <summary>The MCP tool names this role may see. Empty means every advertised tool.</summary>
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
