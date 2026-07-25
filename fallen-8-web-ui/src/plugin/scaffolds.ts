import type { AlgorithmContract, PluginAuthoringCategory } from "../api/types";

/**
 * Per-category/-contract authoring scaffolds (feature plugin-registration §6): the starter
 * WHOLE type — correct usings, the contract interface, the IPlugin members and the contract
 * method stubbed — so the author fills in the body, not the boilerplate. This is the
 * whole-type analogue of the delegate editor's opening snippet (src/delegate/kinds.ts).
 *
 * The scaffold's PluginName string is the exact registration name (the server validates
 * equality); the C# class identifier is a sanitized form, since a plugin name may contain a
 * dash while a C# identifier may not. Algorithm contract methods are stubbed with
 * NotImplementedException (they compile and satisfy the one-implementor contract, so the
 * skeleton validates immediately); the function scaffold ships a working label scan.
 */

/** The algorithm contracts a scaffold can target, and the interface each implements. */
export const ALGORITHM_CONTRACTS: AlgorithmContract[] = ["Path", "SubGraph", "Analytics"];

const ALGORITHM_INTERFACE: Record<AlgorithmContract, string> = {
  Path: "IShortestPathAlgorithm",
  SubGraph: "ISubGraphAlgorithm",
  Analytics: "IGraphAnalyticsAlgorithm",
};

/** The interface the source must implement for a given category/contract (for display). */
export function contractInterface(
  category: PluginAuthoringCategory,
  contract: AlgorithmContract,
): string {
  return category === "function" ? "IGraphFunction" : ALGORITHM_INTERFACE[contract];
}

/** A sensible default registration name per category (also a valid C# identifier). */
export const DEFAULT_PLUGIN_NAME: Record<PluginAuthoringCategory, string> = {
  algorithm: "MyAlgorithm",
  function: "MyFunction",
};

/** Turns a registration name into a legal C# type identifier (dash → underscore, etc.). */
export function toClassIdentifier(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9_]/g, "_");
  if (cleaned === "") return "MyPlugin";
  return /^[A-Za-z_]/.test(cleaned) ? cleaned : `_${cleaned}`;
}

function algorithmScaffold(
  contract: AlgorithmContract,
  className: string,
  pluginName: string,
): string {
  const iface = ALGORITHM_INTERFACE[contract];
  const body: Record<AlgorithmContract, { usings: string; method: string }> = {
    Path: {
      usings: "using NoSQL.GraphDB.Core.Algorithms.Path;",
      method: `    public bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition)
    {
        // TODO: compute paths between definition.SourceVertex and definition.TargetVertex.
        throw new NotImplementedException();
    }`,
    },
    SubGraph: {
      usings: "using NoSQL.GraphDB.Core.Algorithms.SubGraph;",
      method: `    public bool TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition)
    {
        // TODO: build the subgraph from definition.VertexFilter / EdgeFilter / Pattern.
        throw new NotImplementedException();
    }`,
    },
    Analytics: {
      usings: "using NoSQL.GraphDB.Core.Algorithms.Analytics;",
      method: `    public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
    {
        // TODO: score or partition the graph and return a GraphAnalyticsResult.
        throw new NotImplementedException();
    }`,
    },
  };

  return `using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
${body[contract].usings}
using NoSQL.GraphDB.Core.Plugin;

public sealed class ${className} : ${iface}
{
    public string PluginName    => "${pluginName}";
    public Type   PluginCategory => typeof(${iface});
    public string Description   => "A custom ${contract} algorithm.";
    public string Manufacturer  => "you";

    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
    public void Dispose() { }

${body[contract].method}
}
`;
}

function functionScaffold(className: string, pluginName: string): string {
  return `using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class ${className} : IGraphFunction
{
    private IFallen8 _graph;

    public string PluginName    => "${pluginName}";
    public Type   PluginCategory => typeof(IGraphFunction);
    public string Description   => "A stored graph function.";
    public string Manufacturer  => "you";

    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) => _graph = fallen8;
    public void Dispose() { }

    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    {
        // Read the graph and return a view of existing vertices/edges (read-only in v1).
        var label = parameters != null && parameters.TryGetValue("label", out var l) ? l as string : null;
        result = GraphFunctionResult.FromElements(_graph.GetAllVertices(label), edges: null);
        return true;
    }
}
`;
}

/**
 * The starter source for a category (+ contract for an algorithm), with the registration
 * name interpolated as both the PluginName string and the C# class identifier.
 */
export function scaffoldFor(
  category: PluginAuthoringCategory,
  contract: AlgorithmContract,
  name: string,
): string {
  const pluginName = name.trim() || DEFAULT_PLUGIN_NAME[category];
  const className = toClassIdentifier(pluginName);
  return category === "function"
    ? functionScaffold(className, pluginName)
    : algorithmScaffold(contract, className, pluginName);
}
