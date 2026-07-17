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
import {
  parseCreationDate,
  toPropertySpec,
  validateTypedValue,
  type TypedValue,
} from "../lib/literals";
import { Field } from "./Field";
import { TypedLiteralEditor } from "./TypedLiteralEditor";
import {
  PropertyListEditor,
  propertyRowsError,
  toPropertySpecs,
  type PropertyRow,
} from "./PropertyListEditor";
import { ErrorBox } from "./ErrorBox";

/**
 * Mutations (FR-21, tabs per feature studio-mutations-ux): one tab per mutation type,
 * each exposing its full REST specification. Everything goes out with
 * waitForCompletion=true (encoded in api/endpoints.ts), so a rolled-back transaction is
 * a visible 4xx/5xx here, never a silent 202.
 */

const TABS = [
  { id: "vertex", label: "Vertex" },
  { id: "edge", label: "Edge" },
  { id: "property", label: "Property" },
  { id: "remove", label: "Remove" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function MutationsPanel() {
  const instance = useActiveInstance()!;
  const [tab, setTab] = useState<TabId>("vertex");
  const [message, setMessage] = useState<string | null>(null);

  const [vertexLabel, setVertexLabel] = useState("");
  const [vertexDate, setVertexDate] = useState("");
  const [vertexProps, setVertexProps] = useState<PropertyRow[]>([]);

  const [edgeSource, setEdgeSource] = useState("");
  const [edgeTarget, setEdgeTarget] = useState("");
  const [edgeProperty, setEdgeProperty] = useState("");
  const [edgeLabel, setEdgeLabel] = useState("");
  const [edgeDate, setEdgeDate] = useState("");
  const [edgeProps, setEdgeProps] = useState<PropertyRow[]>([]);

  const [propElementId, setPropElementId] = useState("");
  const [propId, setPropId] = useState("");
  const [propValue, setPropValue] = useState<TypedValue>({
    type: "System.String",
    raw: "",
  });
  const [removeId, setRemoveId] = useState("");

  const vertexDateParsed = parseCreationDate(vertexDate);
  const vertexPropsError = propertyRowsError(vertexProps);
  const edgeDateParsed = parseCreationDate(edgeDate);
  const edgePropsError = propertyRowsError(edgeProps);

  // The create endpoints return 202 with no body (the id is not reported); find the new
  // element via the bulk view or a scan.
  const addVertex = useMutation({
    mutationFn: () =>
      createVertex(instance, {
        creationDate: vertexDateParsed.ok ? vertexDateParsed.seconds : 0,
        label: vertexLabel.trim() || undefined,
        properties: toPropertySpecs(vertexProps),
      }),
    onSuccess: () =>
      setMessage(
        `Vertex created${vertexLabel.trim() ? ` (label '${vertexLabel.trim()}')` : ""}${
          vertexProps.length ? ` with ${vertexProps.length} propert${vertexProps.length === 1 ? "y" : "ies"}` : ""
        }.`,
      ),
  });

  const addEdge = useMutation({
    mutationFn: () =>
      createEdge(instance, {
        creationDate: edgeDateParsed.ok ? edgeDateParsed.seconds : 0,
        sourceVertex: Number(edgeSource),
        targetVertex: Number(edgeTarget),
        edgePropertyId: edgeProperty.trim(),
        label: edgeLabel.trim() || undefined,
        properties: toPropertySpecs(edgeProps),
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
      <div className="panel-title">
        Mutations
        <span className="text-fg-faint normal-case">
          transactional, waits for completion
        </span>
      </div>
      <div className="space-y-4 p-3">
        <div className="border-line flex w-fit overflow-hidden rounded border">
          {TABS.map(({ id, label }) => (
            <button
              key={id}
              type="button"
              data-testid={`mutation-tab-${id}`}
              className={`px-3 py-1 text-[12px] ${
                tab === id ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg cursor-pointer"
              }`}
              onClick={() => setTab(id)}
            >
              {label}
            </button>
          ))}
        </div>

        {tab === "vertex" && (
          <div className="space-y-3" data-testid="mutation-form-vertex">
            <div className="flex flex-wrap items-end gap-2">
              <Field helpKey="mutVertexLabel" label="label (optional)" htmlFor="mv-label">
                <input
                  id="mv-label"
                  data-testid="new-vertex-label"
                  className="input w-40"
                  value={vertexLabel}
                  onChange={(e) => setVertexLabel(e.target.value)}
                  placeholder="person"
                />
              </Field>
              <Field
                helpKey="mutCreationDate"
                label="creation date (optional)"
                htmlFor="mv-date"
              >
                <input
                  id="mv-date"
                  data-testid="new-vertex-date"
                  className={`input w-48 ${vertexDateParsed.ok ? "" : "border-danger"}`}
                  value={vertexDate}
                  onChange={(e) => setVertexDate(e.target.value)}
                  placeholder="unix seconds or ISO"
                />
              </Field>
            </div>
            {!vertexDateParsed.ok && (
              <div className="text-danger text-[11px]">{vertexDateParsed.error}</div>
            )}
            <PropertyListEditor rows={vertexProps} onChange={setVertexProps} idPrefix="mv" />
            {vertexProps.length > 0 && vertexPropsError && (
              <div className="text-danger text-[11px]">{vertexPropsError}</div>
            )}
            <button
              type="button"
              className="btn btn-accent"
              data-testid="create-vertex"
              disabled={addVertex.isPending || !vertexDateParsed.ok || vertexPropsError !== null}
              onClick={() => addVertex.mutate()}
            >
              Create vertex
            </button>
          </div>
        )}

        {tab === "edge" && (
          <div className="space-y-3" data-testid="mutation-form-edge">
            <div className="flex flex-wrap items-end gap-2">
              <Field helpKey="mutEdgeSource" label="source vertex id" htmlFor="me-source">
                <input
                  id="me-source"
                  className="input w-24"
                  value={edgeSource}
                  onChange={(e) => setEdgeSource(e.target.value)}
                />
              </Field>
              <Field helpKey="mutEdgeTarget" label="target vertex id" htmlFor="me-target">
                <input
                  id="me-target"
                  className="input w-24"
                  value={edgeTarget}
                  onChange={(e) => setEdgeTarget(e.target.value)}
                />
              </Field>
              <Field helpKey="edgePropertyId" label="edge property id" htmlFor="me-prop">
                <input
                  id="me-prop"
                  className="input w-32"
                  value={edgeProperty}
                  onChange={(e) => setEdgeProperty(e.target.value)}
                  placeholder="knows"
                />
              </Field>
              <Field helpKey="mutEdgeLabel" label="label (optional)" htmlFor="me-label">
                <input
                  id="me-label"
                  className="input w-32"
                  value={edgeLabel}
                  onChange={(e) => setEdgeLabel(e.target.value)}
                />
              </Field>
              <Field
                helpKey="mutCreationDate"
                label="creation date (optional)"
                htmlFor="me-date"
              >
                <input
                  id="me-date"
                  className={`input w-48 ${edgeDateParsed.ok ? "" : "border-danger"}`}
                  value={edgeDate}
                  onChange={(e) => setEdgeDate(e.target.value)}
                  placeholder="unix seconds or ISO"
                />
              </Field>
            </div>
            {!edgeDateParsed.ok && (
              <div className="text-danger text-[11px]">{edgeDateParsed.error}</div>
            )}
            <PropertyListEditor rows={edgeProps} onChange={setEdgeProps} idPrefix="me" />
            {edgeProps.length > 0 && edgePropsError && (
              <div className="text-danger text-[11px]">{edgePropsError}</div>
            )}
            <button
              type="button"
              className="btn btn-accent"
              data-testid="create-edge"
              disabled={
                addEdge.isPending ||
                !Number.isInteger(Number(edgeSource)) ||
                edgeSource === "" ||
                !Number.isInteger(Number(edgeTarget)) ||
                edgeTarget === "" ||
                !edgeProperty.trim() ||
                !edgeDateParsed.ok ||
                edgePropsError !== null
              }
              onClick={() => addEdge.mutate()}
            >
              Create edge
            </button>
          </div>
        )}

        {tab === "property" && (
          <div className="space-y-3" data-testid="mutation-form-property">
            <div className="flex flex-wrap items-end gap-2">
              <Field helpKey="elementId" label="element id" htmlFor="mp-element">
                <input
                  id="mp-element"
                  className="input w-24"
                  value={propElementId}
                  onChange={(e) => setPropElementId(e.target.value)}
                />
              </Field>
              <Field helpKey="propertyId" label="property id" htmlFor="mp-id">
                <input
                  id="mp-id"
                  className="input w-32"
                  value={propId}
                  onChange={(e) => setPropId(e.target.value)}
                  placeholder="age"
                />
              </Field>
              <TypedLiteralEditor
                label="value"
                idPrefix="mp"
                value={propValue}
                onChange={setPropValue}
              />
              <button
                type="button"
                className="btn btn-accent"
                disabled={
                  upsertProperty.isPending ||
                  !propElementId ||
                  !propId.trim() ||
                  validateTypedValue(propValue) !== null
                }
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
            <p className="text-fg-faint text-[11px]">
              Set creates or overwrites the property on the element; Remove deletes it.
            </p>
          </div>
        )}

        {tab === "remove" && (
          <div className="space-y-3" data-testid="mutation-form-remove">
            <div className="flex flex-wrap items-end gap-2">
              <Field helpKey="mutRemoveElement" label="element id" htmlFor="mr-id">
                <input
                  id="mr-id"
                  className="input w-24"
                  value={removeId}
                  onChange={(e) => setRemoveId(e.target.value)}
                />
              </Field>
              <button
                type="button"
                className="btn btn-danger"
                disabled={dropElement.isPending || !removeId}
                onClick={() => dropElement.mutate()}
              >
                Remove element
              </button>
            </div>
            <p className="text-fg-faint text-[11px]">
              Removing a vertex also removes all of its edges. There is no undo.
            </p>
          </div>
        )}

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
