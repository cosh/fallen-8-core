// MIT License
//
// SampleGraphsPanel.tsx
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

import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { useStatus } from "../state/status";
import type { SampleBadge, SampleManifestEntry } from "../lib/samples";
import {
  embeddingGate,
  fetchSamplesManifest,
  loadSampleGraph,
  samplesBaseUrl,
  type EmbeddingGate,
  type LoadStep,
} from "../lib/sampleLoader";
import { buildJsonlGraph } from "../lib/jsonlGraph";
import { describeGithubSbomFailure, sbomToGraph, type SpdxSbom } from "../lib/sbomGraph";
import { importBulk, tabulaRasa, getGraph } from "../api/endpoints";
import { invalidateInstanceQueries } from "../api/queries";
import { DEFAULT_STYLE_CONFIG } from "../canvas/styleConfig";
import { ErrorBox } from "./ErrorBox";
import { ConfirmDialog } from "./ConfirmDialog";

/**
 * Sample graphs (feature sample-graphs): a manifest-driven gallery of one-click demo
 * graphs plus the dynamic GitHub dependency card. Each card spans the full width and
 * carries its "what you can test" steps up front; a tag bar filters the gallery by
 * capability (canvas / path / analytics / semantic / spatial). Datasets are fetched from a
 * public GitHub raw URL and ingested via /bulk/import — embeddings are baked in, so no
 * embedding work happens here. Loading into a non-empty graph is gated behind a typed
 * confirm and runs Tabula rasa first (import requires an empty target). Rendered by the
 * Samples screen (its own rail entry).
 */

/** Filter chips in a fixed order; only tags present in the manifest are offered. */
const TAG_ORDER: readonly SampleBadge[] = ["canvas", "path", "analytics", "semantic", "spatial"];

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
  const [githubInputError, setGithubInputError] = useState<string | null>(null);
  // Tag filter (OR/union across selected tags); empty = show everything, including the two
  // special cards (Scale, GitHub) that have no manifest tags to match on.
  const [tags, setTags] = useState<Set<SampleBadge>>(new Set());

  const samples = manifest.data?.samples ?? [];
  const availableTags = TAG_ORDER.filter((t) => samples.some((s) => s.badges.includes(t)));
  const filtered = useMemo(
    () => samples.filter((s) => tags.size === 0 || s.badges.some((b) => tags.has(b))),
    [samples, tags],
  );
  const toggleTag = (t: SampleBadge) =>
    setTags((prev) => {
      const next = new Set(prev);
      if (next.has(t)) next.delete(t);
      else next.add(t);
      return next;
    });

  // "Empty" for the no-wipe fast path means NOTHING to lose AND nothing that would clash:
  // import 409s on any element, and a leftover index of the same id would fail createIndex
  // after a successful import. Indices can outlive elements, so count them too.
  const graphIsEmpty =
    (status.data?.vertexCount ?? 0) === 0 &&
    (status.data?.edgeCount ?? 0) === 0 &&
    (status.data?.indices?.length ?? 0) === 0;

  const afterLoad = (title: string, steps: string[], vertices: number, edges: number) => {
    setMessage(`Loaded ${title}: ${vertices.toLocaleString()} vertices, ${edges.toLocaleString()} edges.`);
    setTrySteps({ title, steps });
    invalidateInstanceQueries(queryClient, instance.id);
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
      // Reset to defaults first: setStyleConfig is a patch-merge, so a prior sample's keys
      // (e.g. Movie Night's edge-width-by-rating) would otherwise leak onto this graph.
      setStyleConfig({ ...DEFAULT_STYLE_CONFIG, ...entry.styleConfig });
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
        ...DEFAULT_STYLE_CONFIG,
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
    if (!repo) {
      setMessage(null);
      setGithubInputError("Enter a public repo as owner/repo (or a github.com URL).");
      return;
    }
    setGithubInputError(null);
    if (graphIsEmpty) githubMutation.mutate({ repo, wipeFirst: false });
    else setConfirm({ kind: "github" });
  };

  return (
    <section className="panel" data-testid="sample-graphs">
      <div className="panel-title">
        Sample graphs
        <span className="text-fg-faint normal-case">
          one-click demo datasets · loading replaces the active graph
        </span>
      </div>
      <div className="space-y-3 p-3">
        {manifest.isError && <ErrorBox error={manifest.error} onRetry={() => manifest.refetch()} />}
        {manifest.isPending && <div className="text-fg-faint text-[12px]">Loading gallery…</div>}

        {manifest.data && (
          <>
            {availableTags.length > 0 && (
              <div
                className="flex flex-wrap items-center gap-1.5"
                data-testid="sample-tag-filter"
                role="group"
                aria-label="Filter samples by capability"
              >
                <span className="text-fg-faint mr-1 text-[10px] tracking-widest uppercase">
                  filter
                </span>
                <TagChip label="all" active={tags.size === 0} onClick={() => setTags(new Set())} />
                {availableTags.map((tag) => (
                  <TagChip
                    key={tag}
                    label={tag}
                    active={tags.has(tag)}
                    onClick={() => toggleTag(tag)}
                    testid={`sample-tag-${tag}`}
                  />
                ))}
              </div>
            )}

            <div className="space-y-3">
              {filtered.map((entry) => (
                <SampleCard
                  key={entry.id}
                  entry={entry}
                  gate={embeddingGate(entry.embedding, status.data ?? null)}
                  busy={busy}
                  onLoad={() => startSample(entry)}
                />
              ))}
              {filtered.length === 0 && (
                <p className="text-fg-faint text-[12px]" data-testid="sample-no-match">
                  No samples match the selected tags.
                </p>
              )}
              {/* The special cards carry no manifest tags — only show them with no filter on. */}
              {tags.size === 0 && (
                <>
                  <ScaleCard busy={busy} />
                  <GithubCard
                    repoInput={repoInput}
                    setRepoInput={setRepoInput}
                    busy={busy}
                    onLoad={startGithub}
                    inputError={githubInputError}
                  />
                </>
              )}
            </div>
          </>
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

/** A filter pill matching the card-badge look; active state uses the accent border/text. */
function TagChip({
  label,
  active,
  onClick,
  testid,
}: {
  label: string;
  active: boolean;
  onClick: () => void;
  testid?: string;
}) {
  return (
    <button
      type="button"
      data-testid={testid}
      aria-pressed={active}
      onClick={onClick}
      className={`rounded border px-2 py-0.5 text-[10px] tracking-wide uppercase transition-colors ${
        active
          ? "border-accent text-accent"
          : "border-line text-fg-faint hover:text-fg-dim"
      }`}
    >
      {label}
    </button>
  );
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
    <div className="border-line rounded border p-4" data-testid={`sample-card-${entry.id}`}>
      <div className="flex flex-wrap items-baseline gap-2">
        <span className="text-lg">{entry.emoji}</span>
        <span className="text-fg font-bold">{entry.title}</span>
        <span className="text-fg-faint text-[11px]">
          {entry.vertexCount.toLocaleString()}V · {entry.edgeCount.toLocaleString()}E
        </span>
        <div className="ml-auto flex flex-wrap gap-1">
          {entry.badges.map((b) => (
            <span
              key={b}
              className="border-line text-fg-faint rounded border px-1.5 py-0.5 text-[10px] uppercase"
            >
              {b}
            </span>
          ))}
        </div>
      </div>
      <p className="text-fg-dim mt-2 text-[12px]">{entry.pitch}</p>
      {gate.kind === "provider-off" && (
        <p className="text-warn mt-2 text-[11px]" data-testid="gate-provider-off">
          Vectors load and index scans work; text-in semantic search needs the embedding
          provider (off on this instance).
        </p>
      )}
      {gate.kind === "mismatch" && (
        <p className="text-warn mt-2 text-[11px]" data-testid="gate-mismatch">
          Vector scans work; text-in search is 409 here — {gate.detail}.
        </p>
      )}
      <div className="mt-3 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div className="min-w-0">
          <div className="text-fg-faint text-[10px] tracking-widest uppercase">
            what you can test
          </div>
          <ul
            className="text-fg-dim mt-1 list-inside list-disc space-y-0.5 text-[12px]"
            data-testid={`sample-trysteps-${entry.id}`}
          >
            {entry.trySteps.map((s, i) => (
              <li key={i}>{s}</li>
            ))}
          </ul>
        </div>
        <button
          type="button"
          className="btn btn-accent shrink-0 md:w-32"
          data-testid={`load-sample-${entry.id}`}
          disabled={busy}
          onClick={onLoad}
        >
          Load
        </button>
      </div>
    </div>
  );
}

function ScaleCard({ busy }: { busy: boolean }) {
  return (
    <div
      className="border-line rounded border border-dashed p-4"
      data-testid="sample-card-scale"
    >
      <div className="flex flex-wrap items-baseline gap-2">
        <span className="text-lg">📈</span>
        <span className="text-fg font-bold">Scale: 100k × 1M</span>
        <span className="text-fg-faint text-[11px]">100,000V · ~1,000,000E</span>
      </div>
      <p className="text-fg-dim mt-2 text-[12px]">
        A 100k-vertex, ~1M-edge preferential-attachment graph — ingest speed, memory
        footprint, and analytics at scale (real hubs).
      </p>
      <div className="mt-3 flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <p className="text-fg-faint text-[11px]">
          Generated server-side, not fetched — use the{" "}
          <span className="text-fg-dim">Benchmark</span> tab's "scale" preset, then run
          PAGERANK on the Analytics screen.
        </p>
        <button
          type="button"
          className="btn shrink-0"
          disabled={busy}
          data-testid="scale-hint"
          aria-disabled
        >
          On the Benchmark tab →
        </button>
      </div>
    </div>
  );
}

function GithubCard({
  repoInput,
  setRepoInput,
  busy,
  onLoad,
  inputError,
}: {
  repoInput: string;
  setRepoInput: (v: string) => void;
  busy: boolean;
  onLoad: () => void;
  inputError: string | null;
}) {
  return (
    <div className="border-line rounded border p-4" data-testid="sample-card-github">
      <div className="flex flex-wrap items-baseline gap-2">
        <span className="text-lg">🐙</span>
        <span className="text-fg font-bold">Any GitHub repo</span>
        <span className="text-fg-faint text-[11px]">live</span>
      </div>
      <p className="text-fg-dim mt-2 text-[12px]">
        Fetch any public repository's dependency graph from GitHub just-in-time and ingest
        it — the dynamic twin of the Fallen-8 Dependencies sample. Color by{" "}
        <span className="text-fg-dim">ecosystem</span>, size by in-degree, then run PAGERANK
        on the Analytics screen for the most-depended-on packages.
      </p>
      <div className="mt-3 flex flex-col gap-2 sm:flex-row">
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
          className="btn btn-accent shrink-0 sm:w-32"
          data-testid="load-github"
          disabled={busy || !repoInput.trim()}
          onClick={onLoad}
        >
          Fetch
        </button>
      </div>
      {inputError && (
        <p className="text-warn mt-2 text-[11px]" data-testid="github-input-error">
          {inputError}
        </p>
      )}
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
    .replace(/\/$/, "") // strip a trailing slash BEFORE .git so "owner/repo.git/" resolves
    .replace(/\.git$/, "");
  return /^[\w.-]+\/[\w.-]+$/.test(trimmed) ? trimmed : null;
}

async function fetchRepoSbom(repo: string): Promise<SpdxSbom> {
  let response: Response;
  try {
    response = await fetch(`https://api.github.com/repos/${repo}/dependency-graph/sbom`, {
      headers: { Accept: "application/vnd.github+json" },
    });
  } catch {
    // fetch only rejects on a NETWORK failure (offline, DNS, blocked) — never on an HTTP
    // error status. Give that its own clear message instead of a raw "Failed to fetch".
    throw new Error(
      `Couldn't reach github.com to fetch '${repo}'. Check your internet connection (and any ` +
        `proxy/ad-blocker) and try again.`,
    );
  }

  if (!response.ok) {
    // GitHub's error body carries a human "message" (e.g. "Dependency graph is disabled…")
    // that distinguishes the three different 404 causes; surface it via the shared mapper.
    const message = await githubErrorMessage(response);
    throw new Error(
      describeGithubSbomFailure(
        repo,
        response.status,
        message,
        {
          remaining: response.headers.get("x-ratelimit-remaining"),
          reset: response.headers.get("x-ratelimit-reset"),
        },
        Date.now(),
      ),
    );
  }

  const body = (await response.json().catch(() => ({}))) as { sbom?: SpdxSbom };
  // Untyped boundary: a 200 without an `sbom` object is treated as "no dependency data"
  // by the caller (sbomToGraph tolerates {}), never an undefined-property crash.
  return body.sbom ?? {};
}

/** GitHub error responses are JSON with a `message`; return it, or "" if the body isn't that. */
async function githubErrorMessage(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { message?: unknown };
    return typeof body.message === "string" ? body.message : "";
  } catch {
    return "";
  }
}
