import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { useStatus } from "../state/status";
import type { SampleManifestEntry } from "../lib/samples";
import {
  embeddingGate,
  fetchSamplesManifest,
  loadSampleGraph,
  samplesBaseUrl,
  type EmbeddingGate,
  type LoadStep,
} from "../lib/sampleLoader";
import { buildJsonlGraph } from "../lib/jsonlGraph";
import { sbomToGraph, type SpdxSbom } from "../lib/sbomGraph";
import { importBulk, tabulaRasa, getGraph } from "../api/endpoints";
import { ErrorBox } from "./ErrorBox";
import { ConfirmDialog } from "./ConfirmDialog";

/**
 * Sample graphs (feature sample-graphs): a manifest-driven gallery of one-click demo
 * graphs plus the dynamic GitHub dependency card. Datasets are fetched from a public
 * GitHub raw URL and ingested via /bulk/import — embeddings are baked in, so no embedding
 * work happens here. Loading into a non-empty graph is gated behind a typed confirm and
 * runs Tabula rasa first (import requires an empty target).
 */

const STEP_LABEL: Record<LoadStep, string> = {
  wiping: "erasing current graph…",
  fetching: "fetching dataset…",
  importing: "importing…",
  indexing: "building indices…",
  rendering: "loading canvas…",
};

type Pending = { entry: SampleManifestEntry; kind: "sample" } | { kind: "github" };

export function SampleGraphsPanel() {
  const { instance, store } = useInstanceStore();
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const setStyleConfig = store((s) => s.setStyleConfig);
  const clearCanvas = store((s) => s.clearCanvas);
  const queryClient = useQueryClient();
  const status = useStatus(instance);

  const baseUrl = samplesBaseUrl();
  const manifest = useQuery({
    queryKey: ["samples-manifest", baseUrl],
    queryFn: ({ signal }) => fetchSamplesManifest(baseUrl, signal),
    staleTime: 5 * 60_000,
  });

  const [step, setStep] = useState<LoadStep | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [trySteps, setTrySteps] = useState<{ title: string; steps: string[] } | null>(null);
  const [confirm, setConfirm] = useState<Pending | null>(null);
  const [repoInput, setRepoInput] = useState("");

  const graphIsEmpty = (status.data?.vertexCount ?? 0) === 0 && (status.data?.edgeCount ?? 0) === 0;

  const afterLoad = (title: string, steps: string[], vertices: number, edges: number) => {
    setMessage(`Loaded ${title}: ${vertices.toLocaleString()} vertices, ${edges.toLocaleString()} edges.`);
    setTrySteps({ title, steps });
    queryClient.invalidateQueries({ queryKey: [instance.id] });
  };

  const sampleMutation = useMutation({
    mutationFn: async ({ entry, wipeFirst }: { entry: SampleManifestEntry; wipeFirst: boolean }) => {
      setMessage(null);
      setTrySteps(null);
      const result = await loadSampleGraph(instance, entry, baseUrl, {
        wipeFirst,
        onStep: setStep,
      });
      return { entry, result };
    },
    onSuccess: ({ entry, result }) => {
      clearCanvas();
      mergeIntoCanvas(result.graph.vertices, result.graph.edges);
      setStyleConfig(entry.styleConfig);
      afterLoad(entry.title, entry.trySteps, result.verticesCreated, result.edgesCreated);
    },
    onSettled: () => setStep(null),
  });

  const githubMutation = useMutation({
    mutationFn: async ({ repo, wipeFirst }: { repo: string; wipeFirst: boolean }) => {
      setMessage(null);
      setTrySteps(null);
      setStep("fetching");
      const sbom = await fetchRepoSbom(repo);
      const { vertices, edges, ecosystemCounts } = sbomToGraph(sbom);
      if (vertices.length === 0) {
        throw new Error(`${repo} has no dependency data (empty SBOM).`);
      }
      const jsonl = buildJsonlGraph(vertices, edges);

      if (wipeFirst) {
        setStep("wiping");
        await tabulaRasa(instance);
      }
      setStep("importing");
      await importBulk(instance, new Blob([jsonl]));
      setStep("rendering");
      const graph = (await getGraph(instance, 20_000)) ?? { vertices: [], edges: [] };
      return { repo, graph, vertices: vertices.length, edges: edges.length, ecosystemCounts };
    },
    onSuccess: ({ repo, graph, vertices, edges, ecosystemCounts }) => {
      clearCanvas();
      mergeIntoCanvas(graph.vertices, graph.edges);
      setStyleConfig({
        nodeColorMode: "property",
        nodeColorProperty: "ecosystem",
        nodeSizeMode: "in-degree",
        nodeImageProperty: "icon",
        edgeArrows: true,
      });
      const summary = Object.entries(ecosystemCounts)
        .sort((a, b) => b[1] - a[1])
        .map(([name, count]) => `${name} ${count}`)
        .join(", ");
      afterLoad(`${repo} dependencies`, [
        `Ecosystems: ${summary}.`,
        "Analytics → PAGERANK for the most-depended-on packages; WCC to separate ecosystems.",
        "Canvas → color by 'ecosystem' or 'license', size by in-degree.",
      ], vertices, edges);
    },
    onSettled: () => setStep(null),
  });

  const busy = sampleMutation.isPending || githubMutation.isPending;

  const startSample = (entry: SampleManifestEntry) => {
    if (graphIsEmpty) sampleMutation.mutate({ entry, wipeFirst: false });
    else setConfirm({ entry, kind: "sample" });
  };
  const startGithub = () => {
    const repo = normalizeRepo(repoInput);
    if (!repo) return;
    if (graphIsEmpty) githubMutation.mutate({ repo, wipeFirst: false });
    else setConfirm({ kind: "github" });
  };

  return (
    <section className="panel" data-testid="sample-graphs">
      <div className="panel-title">Sample graphs</div>
      <div className="space-y-3 p-3">
        {manifest.isError && <ErrorBox error={manifest.error} onRetry={() => manifest.refetch()} />}
        {manifest.isPending && <div className="text-fg-faint text-[12px]">Loading gallery…</div>}

        {manifest.data && (
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            {manifest.data.samples.map((entry) => (
              <SampleCard
                key={entry.id}
                entry={entry}
                gate={embeddingGate(entry.embedding, status.data ?? null)}
                busy={busy}
                onLoad={() => startSample(entry)}
              />
            ))}
            <ScaleCard busy={busy} />
            <GithubCard
              repoInput={repoInput}
              setRepoInput={setRepoInput}
              busy={busy}
              onLoad={startGithub}
            />
          </div>
        )}

        {step && (
          <div className="text-accent text-[12px]" data-testid="sample-progress">
            {STEP_LABEL[step]}
          </div>
        )}
        {message && (
          <div className="text-accent text-[12px]" data-testid="sample-message">
            {message}
          </div>
        )}
        {trySteps && (
          <div className="border-line rounded border p-3 text-[12px]" data-testid="sample-try">
            <div className="text-fg mb-1 font-bold">Try this on {trySteps.title}:</div>
            <ul className="text-fg-dim list-inside list-disc space-y-1">
              {trySteps.steps.map((s, i) => (
                <li key={i}>{s}</li>
              ))}
            </ul>
          </div>
        )}
        {sampleMutation.isError && <ErrorBox error={sampleMutation.error} />}
        {githubMutation.isError && <ErrorBox error={githubMutation.error} />}
      </div>

      <ConfirmDialog
        open={confirm !== null}
        title="Replace the current graph"
        description="Loading a sample runs Tabula rasa first — every vertex, edge, and index is erased and replaced. Save a checkpoint first if you need it."
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Erase and load"
        onConfirm={() => {
          const pending = confirm;
          setConfirm(null);
          if (pending?.kind === "sample") sampleMutation.mutate({ entry: pending.entry, wipeFirst: true });
          else if (pending?.kind === "github") {
            const repo = normalizeRepo(repoInput);
            if (repo) githubMutation.mutate({ repo, wipeFirst: true });
          }
        }}
        onCancel={() => setConfirm(null)}
      />
    </section>
  );
}

function badgeText(badge: string): string {
  return badge;
}

function SampleCard({
  entry,
  gate,
  busy,
  onLoad,
}: {
  entry: SampleManifestEntry;
  gate: EmbeddingGate;
  busy: boolean;
  onLoad: () => void;
}) {
  return (
    <div className="border-line flex flex-col gap-2 rounded border p-3" data-testid={`sample-card-${entry.id}`}>
      <div className="flex items-baseline gap-2">
        <span className="text-lg">{entry.emoji}</span>
        <span className="text-fg font-bold">{entry.title}</span>
        <span className="text-fg-faint ml-auto text-[11px]">
          {entry.vertexCount.toLocaleString()}V · {entry.edgeCount.toLocaleString()}E
        </span>
      </div>
      <p className="text-fg-dim text-[12px]">{entry.pitch}</p>
      <div className="flex flex-wrap gap-1">
        {entry.badges.map((b) => (
          <span key={b} className="border-line text-fg-faint rounded border px-1.5 py-0.5 text-[10px] uppercase">
            {badgeText(b)}
          </span>
        ))}
      </div>
      {gate.kind === "provider-off" && (
        <p className="text-warn text-[11px]" data-testid="gate-provider-off">
          Vectors load and index scans work; text-in semantic search needs the embedding
          provider (off on this instance).
        </p>
      )}
      {gate.kind === "mismatch" && (
        <p className="text-warn text-[11px]" data-testid="gate-mismatch">
          Vector scans work; text-in search is 409 here — {gate.detail}.
        </p>
      )}
      <button
        type="button"
        className="btn btn-accent mt-auto"
        data-testid={`load-sample-${entry.id}`}
        disabled={busy}
        onClick={onLoad}
      >
        Load
      </button>
    </div>
  );
}

function ScaleCard({ busy }: { busy: boolean }) {
  return (
    <div className="border-line flex flex-col gap-2 rounded border border-dashed p-3" data-testid="sample-card-scale">
      <div className="flex items-baseline gap-2">
        <span className="text-lg">📈</span>
        <span className="text-fg font-bold">Scale: 100k × 1M</span>
        <span className="text-fg-faint ml-auto text-[11px]">100,000V · ~1,000,000E</span>
      </div>
      <p className="text-fg-dim text-[12px]">
        A 100k-vertex, ~1M-edge preferential-attachment graph — ingest speed, memory
        footprint, and analytics at scale (real hubs).
      </p>
      <p className="text-fg-faint mt-auto text-[11px]">
        Generated server-side, not fetched — use the{" "}
        <span className="text-fg-dim">Benchmark</span> tab's "scale" preset, then run
        PAGERANK on the Analytics screen.
      </p>
      <button type="button" className="btn" disabled={busy} data-testid="scale-hint" aria-disabled>
        On the Benchmark tab →
      </button>
    </div>
  );
}

function GithubCard({
  repoInput,
  setRepoInput,
  busy,
  onLoad,
}: {
  repoInput: string;
  setRepoInput: (v: string) => void;
  busy: boolean;
  onLoad: () => void;
}) {
  return (
    <div className="border-line flex flex-col gap-2 rounded border p-3" data-testid="sample-card-github">
      <div className="flex items-baseline gap-2">
        <span className="text-lg">🐙</span>
        <span className="text-fg font-bold">Any GitHub repo</span>
        <span className="text-fg-faint ml-auto text-[11px]">live</span>
      </div>
      <p className="text-fg-dim text-[12px]">
        Fetch any public repository's dependency graph from GitHub just-in-time and ingest
        it — the dynamic twin of the Fallen-8 Dependencies sample.
      </p>
      <div className="mt-auto flex gap-2">
        <input
          className="input flex-1"
          data-testid="github-repo-input"
          placeholder="owner/repo"
          value={repoInput}
          onChange={(e) => setRepoInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") onLoad();
          }}
        />
        <button
          type="button"
          className="btn btn-accent"
          data-testid="load-github"
          disabled={busy || !repoInput.trim()}
          onClick={onLoad}
        >
          Fetch
        </button>
      </div>
    </div>
  );
}

/** Accepts "owner/repo" or a full/bare GitHub URL; returns "owner/repo" or null. */
export function normalizeRepo(input: string): string | null {
  const trimmed = input
    .trim()
    .replace(/^https?:\/\//i, "")
    .replace(/^www\./i, "")
    .replace(/^github\.com\//i, "")
    .replace(/\.git$/, "")
    .replace(/\/$/, "");
  return /^[\w.-]+\/[\w.-]+$/.test(trimmed) ? trimmed : null;
}

async function fetchRepoSbom(repo: string): Promise<SpdxSbom> {
  const response = await fetch(`https://api.github.com/repos/${repo}/dependency-graph/sbom`, {
    headers: { Accept: "application/vnd.github+json" },
  });
  if (response.status === 404) {
    throw new Error(`Repository '${repo}' not found (or private, or has no dependency graph).`);
  }
  if (response.status === 403) {
    const reset = response.headers.get("x-ratelimit-reset");
    const remaining = response.headers.get("x-ratelimit-remaining");
    if (remaining === "0" && reset) {
      const mins = Math.max(1, Math.ceil((Number(reset) * 1000 - Date.now()) / 60_000));
      throw new Error(`GitHub's anonymous rate limit is exhausted — try again in ~${mins} min.`);
    }
    throw new Error("GitHub refused the request (403).");
  }
  if (!response.ok) {
    throw new Error(`GitHub returned ${response.status} for '${repo}'.`);
  }
  return ((await response.json()) as { sbom: SpdxSbom }).sbom;
}
