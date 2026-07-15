import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useActiveInstance } from "../instances/registry";
import {
  createEdge,
  createVertex,
  removeGraphElement,
  removeProperty,
  setProperty,
} from "../api/endpoints";
import { toPropertySpec, type TypedValue } from "../lib/literals";
import { TypedLiteralEditor } from "../components/TypedLiteralEditor";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Mutations (FR-21): create vertex/edge, add/update/remove property, remove element.
 * Everything goes out with waitForCompletion=true (encoded in api/endpoints.ts), so a
 * rolled-back transaction is a visible 4xx/5xx here, never a silent 202.
 */
export function MutationsPanel() {
  const instance = useActiveInstance()!;
  const [message, setMessage] = useState<string | null>(null);

  const [vertexLabel, setVertexLabel] = useState("");
  const [edgeSource, setEdgeSource] = useState("");
  const [edgeTarget, setEdgeTarget] = useState("");
  const [edgeProperty, setEdgeProperty] = useState("");
  const [edgeLabel, setEdgeLabel] = useState("");
  const [propElementId, setPropElementId] = useState("");
  const [propId, setPropId] = useState("");
  const [propValue, setPropValue] = useState<TypedValue>({
    type: "System.String",
    raw: "",
  });
  const [removeId, setRemoveId] = useState("");

  // The create endpoints return 202 with no body (the id is not reported); find the new
  // element via the bulk view or a scan.
  const addVertex = useMutation({
    mutationFn: () =>
      createVertex(instance, {
        creationDate: 0,
        label: vertexLabel.trim() || undefined,
      }),
    onSuccess: () =>
      setMessage(
        `Vertex created${vertexLabel.trim() ? ` (label '${vertexLabel.trim()}')` : ""}.`,
      ),
  });

  const addEdge = useMutation({
    mutationFn: () =>
      createEdge(instance, {
        creationDate: 0,
        sourceVertex: Number(edgeSource),
        targetVertex: Number(edgeTarget),
        edgePropertyId: edgeProperty.trim(),
        label: edgeLabel.trim() || undefined,
      }),
    onSuccess: () => setMessage(`Edge created (${edgeSource} → ${edgeTarget}).`),
  });

  const upsertProperty = useMutation({
    mutationFn: () =>
      setProperty(
        instance,
        Number(propElementId),
        propId.trim(),
        toPropertySpec(propId.trim(), propValue),
      ),
    onSuccess: () => setMessage(`Property '${propId}' set on #${propElementId}.`),
  });

  const dropProperty = useMutation({
    mutationFn: () => removeProperty(instance, Number(propElementId), propId.trim()),
    onSuccess: () => setMessage(`Property '${propId}' removed from #${propElementId}.`),
  });

  const dropElement = useMutation({
    mutationFn: () => removeGraphElement(instance, Number(removeId)),
    onSuccess: () => setMessage(`Element #${removeId} removed.`),
  });

  const failed = [addVertex, addEdge, upsertProperty, dropProperty, dropElement].find(
    (m) => m.isError,
  );

  return (
    <section className="panel">
      <div className="panel-title">Mutations (transactional, waits for completion)</div>
      <div className="space-y-4 p-3">
        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="label" htmlFor="mv-label">
              new vertex label
            </label>
            <input
              id="mv-label"
              data-testid="new-vertex-label"
              className="input w-40"
              value={vertexLabel}
              onChange={(e) => setVertexLabel(e.target.value)}
              placeholder="person"
            />
          </div>
          <button
            type="button"
            className="btn btn-accent"
            data-testid="create-vertex"
            disabled={addVertex.isPending}
            onClick={() => addVertex.mutate()}
          >
            Create vertex
          </button>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="label" htmlFor="me-source">
              source id
            </label>
            <input
              id="me-source"
              className="input w-24"
              value={edgeSource}
              onChange={(e) => setEdgeSource(e.target.value)}
            />
          </div>
          <div>
            <label className="label" htmlFor="me-target">
              target id
            </label>
            <input
              id="me-target"
              className="input w-24"
              value={edgeTarget}
              onChange={(e) => setEdgeTarget(e.target.value)}
            />
          </div>
          <div>
            <label className="label" htmlFor="me-prop">
              edge property id
            </label>
            <input
              id="me-prop"
              className="input w-32"
              value={edgeProperty}
              onChange={(e) => setEdgeProperty(e.target.value)}
              placeholder="knows"
            />
          </div>
          <div>
            <label className="label" htmlFor="me-label">
              label (optional)
            </label>
            <input
              id="me-label"
              className="input w-32"
              value={edgeLabel}
              onChange={(e) => setEdgeLabel(e.target.value)}
            />
          </div>
          <button
            type="button"
            className="btn btn-accent"
            disabled={
              addEdge.isPending ||
              !Number.isInteger(Number(edgeSource)) ||
              edgeSource === "" ||
              !Number.isInteger(Number(edgeTarget)) ||
              edgeTarget === "" ||
              !edgeProperty.trim()
            }
            onClick={() => addEdge.mutate()}
          >
            Create edge
          </button>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="label" htmlFor="mp-element">
              element id
            </label>
            <input
              id="mp-element"
              className="input w-24"
              value={propElementId}
              onChange={(e) => setPropElementId(e.target.value)}
            />
          </div>
          <div>
            <label className="label" htmlFor="mp-id">
              property id
            </label>
            <input
              id="mp-id"
              className="input w-32"
              value={propId}
              onChange={(e) => setPropId(e.target.value)}
              placeholder="age"
            />
          </div>
          <TypedLiteralEditor
            label="value"
            idPrefix="mp"
            value={propValue}
            onChange={setPropValue}
          />
          <button
            type="button"
            className="btn btn-accent"
            disabled={upsertProperty.isPending || !propElementId || !propId.trim()}
            onClick={() => upsertProperty.mutate()}
          >
            Set property
          </button>
          <button
            type="button"
            className="btn btn-danger"
            disabled={dropProperty.isPending || !propElementId || !propId.trim()}
            onClick={() => dropProperty.mutate()}
          >
            Remove property
          </button>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          <div>
            <label className="label" htmlFor="mr-id">
              element id
            </label>
            <input
              id="mr-id"
              className="input w-24"
              value={removeId}
              onChange={(e) => setRemoveId(e.target.value)}
            />
          </div>
          <button
            type="button"
            className="btn btn-danger"
            disabled={dropElement.isPending || !removeId}
            onClick={() => dropElement.mutate()}
          >
            Remove element
          </button>
        </div>

        {message && (
          <div className="text-accent text-[12px]" data-testid="mutation-message">
            {message}
          </div>
        )}
        {failed && <ErrorBox error={failed.error} />}
      </div>
    </section>
  );
}
