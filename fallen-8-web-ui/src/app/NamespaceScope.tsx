import { useEffect } from "react";
import { Outlet, useNavigate, useParams } from "@tanstack/react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useRegistry, useActiveInstance, DEFAULT_NAMESPACE } from "../instances/registry";
import { listNamespaces, createNamespace } from "../api/endpoints";
import { purgeInstanceStore } from "../state/instanceStore";
import { bumpFeedGeneration } from "../state/liveFeed";

/**
 * Layout under /q/$ns/… (feature graph-namespaces): keeps the registry's active namespace
 * in sync with the URL (the URL is the deep-link source of truth), and renders the
 * "recreate or switch" recover state — never a blank screen — when the URL names a
 * namespace this Fallen-8 does not hold (dropped elsewhere, stale link).
 */
export function NamespaceScope() {
  const { ns } = useParams({ from: "/q/$ns" });
  const instance = useActiveInstance();
  const setActiveNamespace = useRegistry((s) => s.setActiveNamespace);
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  useEffect(() => {
    if (instance) setActiveNamespace(instance.id, ns);
  }, [instance?.id, ns, setActiveNamespace]);

  // The same poll the switcher uses; a namespace dropped elsewhere surfaces within a cycle —
  // or immediately, when any request's marked 404 announces it (see throwIfNotOk).
  const namespaces = useQuery({
    queryKey: [instance?.id, "namespaces"],
    queryFn: ({ signal }) => listNamespaces(instance!, signal),
    enabled: instance !== null,
    refetchInterval: 15_000,
    retry: 0,
  });
  const refetchNamespaces = namespaces.refetch;
  useEffect(() => {
    const onMissing = () => void refetchNamespaces();
    window.addEventListener("f8:namespace-missing", onMissing);
    return () => window.removeEventListener("f8:namespace-missing", onMissing);
  }, [refetchNamespaces]);

  const known = namespaces.data?.namespaces.map((entry) => entry.name);
  if (instance && known && !known.includes(ns)) {
    return (
      <div data-testid="namespace-recover" className="text-fg-dim flex flex-col items-start gap-3">
        <div>
          Namespace <span className="text-fg font-semibold">“{ns}”</span> does not exist on
          “{instance.name}” — it may have been dropped elsewhere.
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            data-testid="namespace-recover-recreate"
            className="btn"
            onClick={async () => {
              await createNamespace(instance, ns);
              // The recreated namespace is EMPTY: the old workspace (canvas, results) would
              // reference elements that no longer exist. Its change-feed stream died on the
              // 404 and the effect key did not change - the generation bump resubscribes it.
              purgeInstanceStore(instance.id, ns);
              bumpFeedGeneration();
              await queryClient.invalidateQueries({ queryKey: [instance.id, "namespaces"] });
            }}
          >
            Recreate “{ns}” (empty)
          </button>
          <button
            type="button"
            data-testid="namespace-recover-switch"
            className="btn"
            onClick={() =>
              navigate({ to: "/q/$ns/dashboard", params: { ns: DEFAULT_NAMESPACE } })
            }
          >
            Switch to “{DEFAULT_NAMESPACE}”
          </button>
        </div>
      </div>
    );
  }

  return <Outlet />;
}
