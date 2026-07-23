import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useInstanceStore } from "../instances/registry";
import { describeEndpoint } from "../instances/types";
import { useStatus } from "../state/status";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import {
  addToIndex,
  addVectorToIndex,
  createIndex,
  deleteIndex,
  removeFromIndex,
  removeIndexKey,
} from "../api/endpoints";
import type { IndexDescription, VectorIndexAddSpecification } from "../api/types";
import { toIndexKey, type TypedValue } from "../lib/literals";
import { parseVector } from "../lib/vector";
import { indexCapabilities } from "../lib/indexCapabilities";
import { TypedLiteralEditor } from "../components/TypedLiteralEditor";
import { Field } from "../components/Field";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Indexes workspace (feature index-workspace): the top-level home for index OBJECTS —
 * live inventory (id, type, capabilities, counts, embedding binding), create, delete
 * behind a typed confirmation, and per-index content management over the full REST
 * surface. Querying an index lives on the Query screen; the Query row action jumps
 * there with the index preselected.
 */

export function IndexesScreen() {
  const { instance, store } = useInstanceStore();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const setScanPrefill = store((s) => s.setScanPrefill);

  const status = useStatus(instance);
  const inventory = status.data?.indices ?? [];
  // An older /status without the inventory field: the table cannot render, so delete
  // falls back to a free-form id (create is unaffected).
  const inventoryAbsent = Boolean(status.data) && status.data!.indices == null;
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selected = inventory.find((i) => i.indexId === selectedId) ?? null;
  const [confirmTarget, setConfirmTarget] = useState<IndexDescription | null>(null);
  const [fallbackDeleteId, setFallbackDeleteId] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  const suggestions = shapeSuggestions(useGraphShape(instance).data);
  const refreshInventory = () =>
    queryClient.invalidateQueries({ queryKey: [instance.id, "status"] });

  const remove = useMutation({
    mutationFn: (indexId: string) => deleteIndex(instance, indexId),
    onSuccess: (_, indexId) => {
      setMessage(`Index '${indexId}' deleted.`);
      if (selectedId === indexId) setSelectedId(null);
      refreshInventory();
    },
  });

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <section className="panel">
        <div className="panel-title">
          Indexes
          {message && <span className="text-accent normal-case">{message}</span>}
        </div>
        {inventory.length === 0 ? (
          <div>
            <p className="text-fg-faint p-3 text-[12px]" data-testid="inventory-empty">
              {inventoryAbsent
                ? "This server does not report an index inventory (older /status contract) — create below; delete needs the id typed here."
                : "No indexes on this instance yet — create one below."}
            </p>
            {inventoryAbsent && (
              <div
                className="flex flex-wrap items-end gap-2 px-3 pb-3"
                data-testid="fallback-delete"
              >
                <Field helpKey="indexId" label="index id" htmlFor="fallback-delete-id">
                  <input
                    id="fallback-delete-id"
                    className="input w-40"
                    value={fallbackDeleteId}
                    onChange={(e) => setFallbackDeleteId(e.target.value)}
                    placeholder="myIndex"
                  />
                </Field>
                <button
                  type="button"
                  className="btn btn-danger"
                  disabled={!fallbackDeleteId.trim() || remove.isPending}
                  onClick={() =>
                    setConfirmTarget({
                      indexId: fallbackDeleteId.trim(),
                      pluginType: null,
                    })
                  }
                >
                  Delete
                </button>
              </div>
            )}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[12px]" data-testid="index-inventory">
              <thead>
                <tr className="text-fg-faint">
                  <th className="table-cell">id</th>
                  <th className="table-cell">type</th>
                  <th className="table-cell">answers</th>
                  <th className="table-cell text-right">keys</th>
                  <th className="table-cell text-right">values</th>
                  <th className="table-cell">binding</th>
                  <th className="table-cell w-40" />
                </tr>
              </thead>
              <tbody>
                {inventory.map((index) => (
                  <tr
                    key={index.indexId}
                    data-testid={`index-row-${index.indexId}`}
                    className={`cursor-pointer ${
                      index.indexId === selectedId ? "bg-panel-2" : "hover:bg-panel-2/50"
                    }`}
                    onClick={() =>
                      setSelectedId(index.indexId === selectedId ? null : index.indexId)
                    }
                  >
                    <td className="table-cell font-semibold">{index.indexId}</td>
                    <td className="table-cell text-fg-dim">{index.pluginType ?? "—"}</td>
                    <td className="table-cell text-fg-dim">
                      {indexCapabilities(index).join(" · ")}
                    </td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.keys?.toLocaleString() ?? "—"}
                    </td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.values?.toLocaleString() ?? "—"}
                    </td>
                    <td className="table-cell">
                      {index.embeddingName ? (
                        <span
                          className="border-accent/50 text-accent rounded border px-1"
                          data-testid={`index-bound-${index.indexId}`}
                          title={`self-maintained projection of the '${index.embeddingName}' element embedding — content manages itself`}
                        >
                          bound:{index.embeddingName}
                        </span>
                      ) : (
                        <span className="text-fg-faint">—</span>
                      )}
                      {index.model && (
                        <div
                          className="text-fg-faint text-[10px]"
                          data-testid={`index-model-${index.indexId}`}
                          title="declared model identity — vectors from any other model are refused"
                        >
                          {index.model}
                        </div>
                      )}
                    </td>
                    <td className="table-cell">
                      <div className="flex justify-end gap-1">
                        <button
                          type="button"
                          className="btn"
                          data-testid={`index-query-${index.indexId}`}
                          onClick={(e) => {
                            e.stopPropagation();
                            setScanPrefill({ indexId: index.indexId });
                            navigate({ to: "/query" });
                          }}
                        >
                          Query
                        </button>
                        <button
                          type="button"
                          className="btn btn-danger"
                          data-testid={`index-delete-${index.indexId}`}
                          onClick={(e) => {
                            e.stopPropagation();
                            setConfirmTarget(index);
                          }}
                        >
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {remove.isError && (
          <div className="px-3 pb-3">
            <ErrorBox error={remove.error} />
          </div>
        )}
        <p className="text-fg-faint px-3 pb-3 text-[11px]">
          Click a row to manage its content. Index definitions are immutable — there is no
          update; delete and recreate instead.
        </p>
      </section>

      <CreatePanel onCreated={refreshInventory} />

      {selected && <ContentPanel key={selected.indexId} index={selected} />}

      <ConfirmDialog
        open={confirmTarget !== null}
        title={`Delete index '${confirmTarget?.indexId}'`}
        description={
          confirmTarget?.embeddingName
            ? `This drops the index — but it is a bound projection of the '${confirmTarget.embeddingName}' element embedding, so recreating it with the same binding rebuilds its content automatically.`
            : "This drops the index AND its content. Definitions are immutable (no update): it must be re-created and re-populated."
        }
        instanceName={instance.name}
        endpoint={describeEndpoint(instance)}
        confirmLabel="Delete index"
        onConfirm={() => {
          if (confirmTarget) remove.mutate(confirmTarget.indexId);
          setConfirmTarget(null);
        }}
        onCancel={() => setConfirmTarget(null)}
      />

      {/* Identifier suggestions from the Graph shape snapshot (empty until computed). */}
      <datalist id="shape-property-keys">
        {suggestions.propertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>
      <datalist id="shape-embedding-names">
        {suggestions.embeddingNames.map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
    </div>
  );
}

function CreatePanel({ onCreated }: { onCreated: () => void }) {
  const { instance } = useInstanceStore();
  const [indexId, setIndexId] = useState("");
  const [indexType, setIndexType] = useState("DictionaryIndex");
  const [message, setMessage] = useState<string | null>(null);
  const [dimension, setDimension] = useState("384");
  const [metric, setMetric] = useState("Cosine");
  const [embeddingName, setEmbeddingName] = useState("");
  const [model, setModel] = useState("");

  // The type dropdown feeds on the server's plugin discovery (free-form fallback for
  // servers predating the field / an unreachable status).
  const status = useStatus(instance).data;
  const availableTypes = status?.availableIndexPlugins ?? [];

  const isVectorIndex = indexType.trim() === "VectorIndex";
  // SpatialIndex.Initialize needs CLR objects (metric, dimensions) that JSON plugin
  // options cannot carry — POST /index always answers false for it, so Create is gated
  // (pinned by StatusIndexInventoryTest; spec studio-index-discovery).
  const isSpatialIndex = indexType.trim() === "SpatialIndex";

  const create = useMutation({
    mutationFn: () =>
      createIndex(instance, {
        uniqueId: indexId,
        pluginType: indexType,
        // VectorIndex options travel as typed literals (vector-index README §creation).
        // embeddingName/model are added only when set, so a raw index stays exactly the
        // old two-option shape (pinned by index-management.test.tsx).
        pluginOptions: isVectorIndex
          ? {
              dimension: {
                propertyId: "dimension",
                propertyValue: dimension,
                fullQualifiedTypeName: "System.Int32",
              },
              metric: {
                propertyId: "metric",
                propertyValue: metric,
                fullQualifiedTypeName: "System.String",
              },
              ...(embeddingName.trim()
                ? {
                    embeddingName: {
                      propertyId: "embeddingName",
                      propertyValue: embeddingName.trim(),
                      fullQualifiedTypeName: "System.String",
                    },
                  }
                : {}),
              ...(model.trim()
                ? {
                    model: {
                      propertyId: "model",
                      propertyValue: model.trim(),
                      fullQualifiedTypeName: "System.String",
                    },
                  }
                : {}),
            }
          : undefined,
      }),
    onSuccess: (ok) => {
      setMessage(
        ok
          ? `Index '${indexId}' created.`
          : `Index '${indexId}' was NOT created — the id may already exist or the options are invalid.`,
      );
      if (ok) onCreated();
    },
  });

  return (
    <section className="panel">
      <div className="panel-title">Create index</div>
      <div className="flex flex-wrap items-end gap-2 p-3">
        <Field helpKey="indexId" label="index id" htmlFor="index-id">
          <input
            id="index-id"
            className="input w-40"
            value={indexId}
            onChange={(e) => setIndexId(e.target.value)}
            placeholder="myIndex"
          />
        </Field>
        <Field helpKey="indexPluginType" label="plugin type" htmlFor="index-type">
          {availableTypes.length > 0 ? (
            <select
              id="index-type"
              data-testid="index-type"
              className="input w-48"
              value={indexType}
              onChange={(e) => setIndexType(e.target.value)}
            >
              {!availableTypes.includes(indexType) && <option>{indexType}</option>}
              {availableTypes.map((t) => (
                <option key={t}>{t}</option>
              ))}
            </select>
          ) : (
            <input
              id="index-type"
              data-testid="index-type"
              className="input w-48"
              value={indexType}
              onChange={(e) => setIndexType(e.target.value)}
              placeholder="DictionaryIndex"
            />
          )}
        </Field>
        {isVectorIndex && (
          <>
            <Field
              helpKey="vectorDimension"
              label="dimension (1–4096)"
              htmlFor="vector-dimension-opt"
            >
              <input
                id="vector-dimension-opt"
                className="input w-24"
                type="number"
                min={1}
                max={4096}
                value={dimension}
                onChange={(e) => setDimension(e.target.value)}
              />
            </Field>
            <Field helpKey="vectorMetric" label="metric" htmlFor="vector-metric">
              <select
                id="vector-metric"
                className="input w-auto"
                value={metric}
                onChange={(e) => setMetric(e.target.value)}
              >
                <option>Cosine</option>
                <option>DotProduct</option>
                <option>L2</option>
              </select>
            </Field>
            <Field
              helpKey="vectorBindEmbeddingName"
              label="bind embedding (optional)"
              htmlFor="vector-embedding-name"
            >
              <input
                id="vector-embedding-name"
                data-testid="vector-embedding-name"
                className="input w-32"
                list="shape-embedding-names"
                value={embeddingName}
                onChange={(e) => setEmbeddingName(e.target.value)}
                placeholder="default"
              />
            </Field>
            <Field helpKey="vectorModel" label="model (optional)" htmlFor="vector-model">
              <input
                id="vector-model"
                className="input w-40"
                value={model}
                onChange={(e) => setModel(e.target.value)}
                placeholder="bge-micro-v2#384#Cosine"
              />
            </Field>
          </>
        )}
        <button
          type="button"
          className="btn btn-accent"
          disabled={!indexId.trim() || create.isPending || isSpatialIndex}
          onClick={() => create.mutate()}
        >
          Create
        </button>
        {message && <span className="text-accent text-[12px]">{message}</span>}
        {isSpatialIndex ? (
          <p className="text-warn basis-full text-[11px]" data-testid="spatial-create-note">
            SpatialIndex cannot be created over REST — its configuration (metric, space
            dimensions) is not expressible as JSON plugin options. Delete still works.
          </p>
        ) : (
          !isVectorIndex && (
            <span className="text-fg-faint text-[11px]" data-testid="no-options-note">
              this index type takes no creation options
            </span>
          )
        )}
        {isVectorIndex && embeddingName.trim() && (
          <p className="text-fg-faint basis-full text-[11px]" data-testid="bound-create-note">
            Bound: this index auto-maintains a projection of the '{embeddingName.trim()}'
            element embedding — write embeddings on the elements (Browser → Embeddings) and
            the index follows; explicit vector-adds are rejected.
          </p>
        )}
      </div>
      {create.isError && (
        <div className="px-3 pb-3">
          <ErrorBox error={create.error} />
        </div>
      )}
      <p className="text-fg-faint px-3 pb-3 text-[11px]">
        Plugin types are suggested live from the server. Property and embedding-name
        suggestions still need a Graph shape snapshot (Analytics screen → Compute).
      </p>
    </section>
  );
}

/**
 * Per-index content management. Which forms show follows the index's capabilities:
 * key-literal families get typed-key add / remove-key; the vector family gets the
 * vector-add form; every family gets remove-element. A BOUND vector index gets none —
 * it is a self-maintained projection (adds are rejected by the server, removes would
 * just fight the writer thread).
 */
function ContentPanel({ index }: { index: IndexDescription }) {
  const { instance } = useInstanceStore();
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);

  const [addElementId, setAddElementId] = useState("");
  const [addKey, setAddKey] = useState<TypedValue>({ type: "System.String", raw: "" });
  const [removeKey, setRemoveKey] = useState<TypedValue>({ type: "System.String", raw: "" });
  const [removeElementId, setRemoveElementId] = useState("");
  const [vaElementId, setVaElementId] = useState("");
  const [vaMode, setVaMode] = useState<"property" | "explicit">("property");
  const [vaPropertyId, setVaPropertyId] = useState("embedding");
  const [vaVectorText, setVaVectorText] = useState("");

  const capabilities = indexCapabilities(index);
  const isVector = capabilities.includes("vector");
  const isSpatial = capabilities.includes("spatial");
  const takesKeyLiterals = !isVector && !isSpatial;
  // A fulltext index files only string keys — the server's AddOrUpdate silently ignores
  // any other type (IndexHelper.CheckObject<String>), so the UI does not offer one.
  const stringKeysOnly = capabilities.includes("fulltext");
  const bound = Boolean(index.embeddingName);

  const refreshCounts = () =>
    queryClient.invalidateQueries({ queryKey: [instance.id, "status"] });

  const add = useMutation({
    mutationFn: () =>
      addToIndex(instance, index.indexId, {
        graphElementId: Number(addElementId),
        key: toIndexKey(addKey),
      }),
    onSuccess: (ok) => {
      setMessage(
        ok
          ? `Element ${addElementId} filed under the key.`
          : `Element ${addElementId} was not added — check the element id.`,
      );
      if (ok) refreshCounts();
    },
  });

  const dropKey = useMutation({
    mutationFn: () => removeIndexKey(instance, index.indexId, toIndexKey(removeKey)),
    onSuccess: (ok) => {
      // The server's false covers both "key not in the index" and "index gone meanwhile".
      setMessage(ok ? "Key removed." : "Nothing removed — the key (or the index) was not found.");
      if (ok) refreshCounts();
    },
  });

  const dropElement = useMutation({
    mutationFn: () => removeFromIndex(instance, index.indexId, Number(removeElementId)),
    onSuccess: (ok) => {
      setMessage(
        ok
          ? `Element ${removeElementId} removed from the index.`
          : `Element ${removeElementId} was not removed — check the element id.`,
      );
      if (ok) refreshCounts();
    },
  });

  const vectorAdd = useMutation({
    mutationFn: () => {
      const spec: VectorIndexAddSpecification = {
        graphElementId: Number(vaElementId),
      };
      if (vaMode === "property") {
        spec.propertyId = vaPropertyId.trim();
      } else {
        const parsed = parseVector(vaVectorText);
        if (!parsed.ok) throw new Error(`Vector: ${parsed.error}.`);
        spec.vector = parsed.vector;
      }
      return addVectorToIndex(instance, index.indexId, spec);
    },
    onSuccess: (ok) => {
      setMessage(
        ok
          ? `Vector for element ${vaElementId} added.`
          : `Element ${vaElementId} was not added — check the element id, property, and dimension.`,
      );
      if (ok) refreshCounts();
    },
  });

  return (
    <section className="panel" data-testid="index-content">
      <div className="panel-title">
        content — {index.indexId}
        {message && <span className="text-accent normal-case">{message}</span>}
      </div>

      {bound ? (
        <p className="text-fg-dim p-3 text-[12px]" data-testid="bound-content-note">
          '{index.indexId}' is a bound projection of the '{index.embeddingName}' element
          embedding and maintains itself — write embeddings on the elements (Browser →
          Embeddings) and the index follows. Explicit adds are rejected by the server;
          removing entries here would just fight the writer thread.
        </p>
      ) : (
        <div className="space-y-3 p-3">
          {takesKeyLiterals && (
            <>
              <div className="flex flex-wrap items-end gap-2" data-testid="content-add">
                <Field
                  helpKey="indexContentElementId"
                  label="element id"
                  htmlFor="content-add-element"
                >
                  <input
                    id="content-add-element"
                    className="input w-28"
                    value={addElementId}
                    onChange={(e) => setAddElementId(e.target.value)}
                  />
                </Field>
                {stringKeysOnly ? (
                  <Field helpKey="indexContentKey" label="key (string)" htmlFor="content-add-key-value">
                    <input
                      id="content-add-key-value"
                      data-testid="content-add-key-value"
                      className="input w-40"
                      value={addKey.raw}
                      onChange={(e) => setAddKey({ type: "System.String", raw: e.target.value })}
                    />
                  </Field>
                ) : (
                  <TypedLiteralEditor
                    helpKey="indexContentKey"
                    label="key"
                    idPrefix="content-add-key"
                    value={addKey}
                    onChange={setAddKey}
                  />
                )}
                <button
                  type="button"
                  className="btn btn-accent"
                  disabled={!addElementId.trim() || add.isPending}
                  onClick={() => add.mutate()}
                >
                  Add element
                </button>
              </div>
              <div className="flex flex-wrap items-end gap-2" data-testid="content-remove-key">
                {stringKeysOnly ? (
                  <Field helpKey="indexContentKey" label="key (string)" htmlFor="content-remove-key-value">
                    <input
                      id="content-remove-key-value"
                      data-testid="content-remove-key-value"
                      className="input w-40"
                      value={removeKey.raw}
                      onChange={(e) =>
                        setRemoveKey({ type: "System.String", raw: e.target.value })
                      }
                    />
                  </Field>
                ) : (
                  <TypedLiteralEditor
                    helpKey="indexContentKey"
                    label="key"
                    idPrefix="content-remove-key"
                    value={removeKey}
                    onChange={setRemoveKey}
                  />
                )}
                <button
                  type="button"
                  className="btn btn-danger"
                  disabled={dropKey.isPending}
                  onClick={() => dropKey.mutate()}
                >
                  Remove key
                </button>
              </div>
            </>
          )}

          {isVector && (
            <div className="flex flex-wrap items-end gap-2" data-testid="vector-add">
              <Field helpKey="vectorAddElementId" label="element id" htmlFor="va-element">
                <input
                  id="va-element"
                  className="input w-28"
                  value={vaElementId}
                  onChange={(e) => setVaElementId(e.target.value)}
                />
              </Field>
              <div className="border-line flex overflow-hidden rounded border">
                {(["property", "explicit"] as const).map((mode) => (
                  <button
                    key={mode}
                    type="button"
                    data-testid={`vector-add-mode-${mode}`}
                    className={`px-2 py-1 text-[11px] ${
                      vaMode === mode
                        ? "bg-panel-2 text-accent"
                        : "text-fg-dim hover:text-fg"
                    }`}
                    onClick={() => setVaMode(mode)}
                  >
                    {mode}
                  </button>
                ))}
              </div>
              {vaMode === "property" ? (
                <Field
                  helpKey="vectorAddPropertyId"
                  label="property id"
                  htmlFor="va-property"
                >
                  <input
                    id="va-property"
                    className="input w-32"
                    list="shape-property-keys"
                    value={vaPropertyId}
                    onChange={(e) => setVaPropertyId(e.target.value)}
                    placeholder="embedding"
                  />
                </Field>
              ) : (
                <Field
                  helpKey="vectorAddVector"
                  label="vector"
                  htmlFor="va-vector"
                  className="grow"
                >
                  <input
                    id="va-vector"
                    className="input w-full font-mono"
                    value={vaVectorText}
                    onChange={(e) => setVaVectorText(e.target.value)}
                    placeholder="[0.12, -0.5, 0.33]"
                  />
                </Field>
              )}
              <button
                type="button"
                className="btn btn-accent"
                disabled={
                  !vaElementId.trim() ||
                  (vaMode === "property" ? !vaPropertyId.trim() : !vaVectorText.trim()) ||
                  vectorAdd.isPending
                }
                onClick={() => vectorAdd.mutate()}
              >
                Add vector
              </button>
              <p className="text-fg-faint basis-full text-[11px]">
                property mode is WAL-recoverable — the honest default; bulk embedding loads
                belong to your pipeline, not a browser.
              </p>
            </div>
          )}

          {isSpatial && (
            <p className="text-fg-faint text-[11px]" data-testid="spatial-content-note">
              Spatial keys (geometries) cannot travel as REST literals — only element
              removal is available here.
            </p>
          )}

          <div className="flex flex-wrap items-end gap-2" data-testid="content-remove-element">
            <Field
              helpKey="indexContentElementId"
              label="element id"
              htmlFor="content-remove-element"
            >
              <input
                id="content-remove-element"
                className="input w-28"
                value={removeElementId}
                onChange={(e) => setRemoveElementId(e.target.value)}
              />
            </Field>
            <button
              type="button"
              className="btn btn-danger"
              disabled={!removeElementId.trim() || dropElement.isPending}
              onClick={() => dropElement.mutate()}
            >
              Remove element
            </button>
          </div>
        </div>
      )}

      {(add.isError || dropKey.isError || dropElement.isError || vectorAdd.isError) && (
        <div className="px-3 pb-3">
          <ErrorBox
            error={add.error ?? dropKey.error ?? dropElement.error ?? vectorAdd.error}
          />
        </div>
      )}
    </section>
  );
}
