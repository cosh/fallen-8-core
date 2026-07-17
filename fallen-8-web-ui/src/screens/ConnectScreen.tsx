import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useRegistry } from "../instances/registry";
import type { InstanceConfig } from "../instances/types";
import { describeEndpoint } from "../instances/types";
import { getStatus, isAuthorized } from "../api/endpoints";

/**
 * Connect / Instances (FR-1a): registry with add/edit/remove, lazy health overview via
 * GET /status per instance, and active-instance selection. States the trusted-network
 * assumption plainly (spec §8 security posture).
 */

function InstanceHealth({ instance }: { instance: InstanceConfig }) {
  const health = useQuery({
    queryKey: [instance.id, "status"],
    queryFn: ({ signal }) => getStatus(instance, signal),
    refetchInterval: 20_000,
    retry: 0,
  });

  if (health.isPending) return <span className="text-fg-faint">checking…</span>;
  if (health.isError || !health.data)
    return <span className="text-danger">unreachable</span>;
  if (!isAuthorized(health.data))
    return <span className="text-danger">unauthorized — check the API key</span>;
  return (
    <span className="text-accent">
      {health.data.vertexCount.toLocaleString()} v · {health.data.edgeCount.toLocaleString()} e
    </span>
  );
}

function InstanceForm({
  initial,
  onSave,
  onCancel,
}: {
  initial?: InstanceConfig;
  onSave: (values: Omit<InstanceConfig, "id">) => void;
  onCancel: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? "");
  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? "");
  const [apiKey, setApiKey] = useState(
    initial?.auth.kind === "apiKey" ? initial.auth.key : "",
  );

  return (
    <form
      className="grid grid-cols-[1fr_2fr_2fr_auto_auto] items-end gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (!name.trim()) return;
        onSave({
          name: name.trim(),
          baseUrl,
          auth: apiKey.trim() ? { kind: "apiKey", key: apiKey.trim() } : { kind: "none" },
        });
      }}
    >
      <div>
        <label className="label" htmlFor="inst-name">
          name
        </label>
        <input
          id="inst-name"
          data-testid="instance-name"
          className="input"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="prod-eu"
        />
      </div>
      <div>
        <label className="label" htmlFor="inst-url">
          base url (empty = same origin)
        </label>
        <input
          id="inst-url"
          data-testid="instance-url"
          className="input"
          value={baseUrl}
          onChange={(e) => setBaseUrl(e.target.value)}
          placeholder="http://localhost:17408"
        />
      </div>
      <div>
        <label className="label" htmlFor="inst-key">
          api key (optional, sent as bearer)
        </label>
        <input
          id="inst-key"
          className="input"
          type="password"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder="X-Api-Key / bearer value"
        />
      </div>
      <button type="submit" className="btn btn-accent" data-testid="instance-save">
        {initial ? "Save" : "Add"}
      </button>
      <button type="button" className="btn" onClick={onCancel}>
        Cancel
      </button>
    </form>
  );
}

export function ConnectScreen() {
  const { instances, activeId, addInstance, updateInstance, removeInstance, setActive } =
    useRegistry();
  const [editing, setEditing] = useState<string | "new" | null>(null);

  return (
    <div className="mx-auto max-w-4xl space-y-4">
      <section className="panel">
        <div className="panel-title">Instances</div>
        <table className="w-full text-[12px]">
          <thead>
            <tr className="text-fg-faint">
              <th className="table-cell w-6"></th>
              <th className="table-cell">name</th>
              <th className="table-cell">endpoint</th>
              <th className="table-cell">auth</th>
              <th className="table-cell">health</th>
              <th className="table-cell w-40">actions</th>
            </tr>
          </thead>
          <tbody>
            {instances.map((instance) => (
              <tr key={instance.id} data-testid={`instance-row-${instance.name}`}>
                <td className="table-cell">
                  <input
                    type="radio"
                    name="active-instance"
                    aria-label={`activate ${instance.name}`}
                    checked={activeId === instance.id}
                    onChange={() => setActive(instance.id)}
                  />
                </td>
                <td className="table-cell font-semibold">{instance.name}</td>
                <td className="table-cell text-fg-dim">{describeEndpoint(instance)}</td>
                <td className="table-cell text-fg-dim">
                  {instance.auth.kind === "apiKey" ? "api key" : "none"}
                </td>
                <td className="table-cell">
                  <InstanceHealth instance={instance} />
                </td>
                <td className="table-cell">
                  <div className="flex gap-1">
                    <button type="button" className="btn" onClick={() => setEditing(instance.id)}>
                      Edit
                    </button>
                    <button
                      type="button"
                      className="btn btn-danger"
                      disabled={instances.length === 1}
                      onClick={() => removeInstance(instance.id)}
                    >
                      Remove
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="p-3">
          {editing === null && (
            <button
              type="button"
              className="btn btn-accent"
              data-testid="instance-add"
              onClick={() => setEditing("new")}
            >
              + Register instance
            </button>
          )}
          {editing === "new" && (
            <InstanceForm
              onSave={(values) => {
                addInstance(values);
                setEditing(null);
              }}
              onCancel={() => setEditing(null)}
            />
          )}
          {editing !== null && editing !== "new" && (
            <InstanceForm
              initial={instances.find((instance) => instance.id === editing)}
              onSave={(values) => {
                updateInstance(editing, values);
                setEditing(null);
              }}
              onCancel={() => setEditing(null)}
            />
          )}
        </div>
      </section>

      <p className="text-fg-faint text-[11px]">
        Fallen-8 instances are developer/operator tools for trusted networks. An instance
        secured with an API key receives it as an <code>Authorization: Bearer</code> header
        (or a custom header); keys are stored in this browser only and are never sent to any
        other instance. Delegate fragments are arbitrary C# executed by the server that
        compiles them.
      </p>
    </div>
  );
}
