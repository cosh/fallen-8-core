/**
 * attack-surface — a seeded, BloodHound-flavoured Active-Directory estate (feature
 * sample-graphs). Users, admins, service accounts, workstations, servers, domain
 * controllers and groups across departments; edges carry an `exploitCost` (attack
 * difficulty) so Dijkstra finds the cheapest attack path. Every node has a one-line
 * `description`, embedded for semantic search ("where do the financial documents live").
 */

import { buildJsonlGraph, prop, type JsonlEdge, type JsonlVertex } from "../../src/lib/jsonlGraph";
import {
  boundVectorIndexRecipe,
  embeddingProperties,
  embedTexts,
  pick,
  seededRandom,
  type BuiltSample,
} from "./shared";

const DEPARTMENTS = ["Finance", "Engineering", "Sales", "HR", "IT", "Legal"] as const;

interface Node {
  id: number;
  label: string;
  name: string;
  icon: string;
  description: string;
}

export async function buildAttackSurface(): Promise<BuiltSample> {
  const rng = seededRandom(0x5eed_ad00);
  const nodes: Node[] = [];
  const edges: JsonlEdge[] = [];
  let nextId = 0;
  let nextEdgeId = 100_000;

  const add = (label: string, name: string, icon: string, description: string): Node => {
    const node: Node = { id: nextId++, label, name, icon, description };
    nodes.push(node);
    return node;
  };
  const link = (source: Node, target: Node, edgePropertyId: string, exploitCost: number) => {
    edges.push({
      id: nextEdgeId++,
      source: source.id,
      target: target.id,
      edgePropertyId,
      properties: { exploitCost: prop.double(Math.round(exploitCost * 100) / 100) },
    });
  };

  // One domain, its Domain Controllers and the crown-jewel Domain Admins group.
  const domainAdmins = add(
    "group",
    "DOMAIN ADMINS",
    "👑",
    "Domain Admins group — full control of the entire directory; the attacker's objective.",
  );
  const dcs = [0, 1].map((i) =>
    add("domaincontroller", `DC0${i + 1}`, "🏰", `Domain controller DC0${i + 1} holding the directory database and every credential hash.`),
  );
  for (const dc of dcs) link(dc, domainAdmins, "memberOf", 0.5);

  // Department groups + a departmental file server holding that team's documents.
  const deptGroup = new Map<string, Node>();
  const deptServer = new Map<string, Node>();
  for (const dept of DEPARTMENTS) {
    const group = add("group", `${dept.toUpperCase()} STAFF`, "👥", `${dept} department security group.`);
    deptGroup.set(dept, group);
    const docTopic =
      dept === "Finance"
        ? "the quarterly financial reports, invoices and payroll spreadsheets"
        : dept === "Legal"
          ? "signed contracts, NDAs and litigation files"
          : dept === "HR"
            ? "employee records, salaries and performance reviews"
            : dept === "Engineering"
              ? "source code, design docs and build artifacts"
              : `${dept} shared documents`;
    const server = add(
      "server",
      `${dept.slice(0, 3).toUpperCase()}-FS01`,
      "🗄️",
      `${dept} file server storing ${docTopic}.`,
    );
    deptServer.set(dept, server);
    link(server, dcs[0], "runsAs", 3.5);
  }

  // Privileged service accounts (Kerberoastable = cheap once cracked).
  const svcSql = add("serviceaccount", "svc_sql", "🤖", "SQL Server service account; Kerberoastable and over-privileged on the Finance file server.");
  link(svcSql, deptServer.get("Finance")!, "adminTo", 1.5);
  const svcBackup = add("serviceaccount", "svc_backup", "🤖", "Backup service account with local admin on every server for nightly snapshots.");
  for (const dept of DEPARTMENTS) link(svcBackup, deptServer.get(dept)!, "adminTo", 2.0);
  link(svcBackup, domainAdmins, "memberOf", 4.0);

  // Users + their primary workstation, per department. The Finance intern is the entry.
  const workstations: Node[] = [];
  for (const dept of DEPARTMENTS) {
    const userCount = dept === "Finance" ? 10 : 8;
    for (let u = 0; u < userCount; u++) {
      const role = u === 0 ? "admin" : "user";
      const icon = role === "admin" ? "🧑‍💼" : "👤";
      const isPhishedIntern = dept === "Finance" && u === userCount - 1;
      const name = isPhishedIntern ? "finance.intern" : `${dept.slice(0, 3).toLowerCase()}.user${u}`;
      const description = isPhishedIntern
        ? "Finance intern whose workstation was phished — the attacker's initial foothold in the estate."
        : `${role === "admin" ? "Departmental admin" : "Standard user"} in ${dept}.`;
      const user = add("user", name, icon, description);
      link(user, deptGroup.get(dept)!, "memberOf", 0.5);

      const ws = add("workstation", `WS-${dept.slice(0, 3).toUpperCase()}-${u}`, "💻", `Windows workstation used by ${name}.`);
      workstations.push(ws);
      link(user, ws, "hasSession", 1.0);
      // Some users can RDP to their department server.
      if (rng() < 0.35) link(ws, deptServer.get(dept)!, "canRDP", 2.5);
      // A departmental admin is admin to the department server (lateral movement).
      if (role === "admin") link(user, deptServer.get(dept)!, "adminTo", 1.5);
    }
  }

  // A couple of cross-department sessions on servers (the juicy lateral-movement edges).
  const itAdmin = nodes.find((n) => n.name === "it.user0")!;
  link(itAdmin, dcs[1], "hasSession", 3.0);
  link(svcSql, deptServer.get("Sales")!, "canRDP", pick(rng, [2.0, 2.5, 3.0]));

  // Embed every node's description for semantic search.
  const descriptions = nodes.map((n) => `${n.label} ${n.name}: ${n.description}`);
  const { vectors, model, dimension } = await embedTexts(descriptions);

  const jsonlVertices: JsonlVertex[] = nodes.map((node, index) => ({
    id: node.id,
    label: node.label,
    properties: {
      name: prop.string(node.name),
      department: prop.string(departmentOf(node)),
      icon: prop.string(node.icon),
      description: prop.string(node.description),
      ...embeddingProperties(vectors[index], model),
    },
  }));

  return {
    jsonl: buildJsonlGraph(jsonlVertices, edges),
    entry: {
      id: "attack-surface",
      title: "AD Attack Surface",
      emoji: "🛡️",
      pitch:
        "A synthetic Active-Directory estate: phish an intern's workstation, then find the cheapest path to Domain Admins.",
      vertexCount: jsonlVertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "path", "analytics", "semantic"],
      trySteps: [
        "Path → cheapest attack path from the phished 'finance.intern' workstation to the DOMAIN ADMINS group, using Dijkstra with cost property 'exploitCost' (look ids up on the Browser screen).",
        "Semantic search: 'where do the financial documents live' surfaces the Finance file server.",
        "Analytics → DEGREE or PAGERANK to spot the lateral-movement choke points (svc_backup, the DCs).",
        "Canvas → color by 'label' to see users / servers / groups; the 'icon' emoji renders per node.",
      ],
      file: "attack-surface.jsonl",
      styleConfig: {
        nodeColorMode: "label",
        nodeSizeMode: "degree",
        nodeImageProperty: "icon",
        edgeArrows: true,
      },
      indexRecipes: [boundVectorIndexRecipe(dimension)],
      embedding: { name: "default", model, dimension, metric: "Cosine" },
    },
  };
}

function departmentOf(node: Node): string {
  const match = /^(Finance|Engineering|Sales|HR|IT|Legal)/i.exec(node.name.replace(/^(\w{3})\./, "$1"));
  if (match) return match[1];
  const prefix = node.name.slice(0, 3).toLowerCase();
  const dept = DEPARTMENTS.find((d) => d.slice(0, 3).toLowerCase() === prefix);
  return dept ?? "Domain";
}
