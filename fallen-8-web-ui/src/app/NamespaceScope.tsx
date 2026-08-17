// MIT License
//
// NamespaceScope.tsx
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

import { useEffect } from "react";
import { Outlet, useNavigate, useParams } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRegistry, useActiveInstance, DEFAULT_NAMESPACE } from "../instances/registry";
import { activateNamespace, listNamespaces, createNamespace } from "../api/endpoints";
import { purgeInstanceStore } from "../state/instanceStore";
import { bumpFeedGeneration } from "../state/liveFeed";
import { ErrorBox } from "../components/ErrorBox";
import { useStudioConfig } from "./studioConfig";

/**
 * Layout under /q/$ns/… (feature graph-namespaces): keeps the registry's active namespace
 * in sync with the URL (the URL is the deep-link source of truth), and renders the
 * "recreate or switch" recover state — never a blank screen — when the URL names a
 * namespace this Fallen-8 does not hold (dropped elsewhere, stale link).
 *
 * Its third branch is a namespace that EXISTS but was not loaded into the running process
 * (feature namespace-startup-load), which is a different situation with a different answer:
 * see the comment on that branch for why it must not reuse the recover state.
 */
export function NamespaceScope() {
  const { ns } = useParams({ from: "/q/$ns" });
  const instance = useActiveInstance();
  const setActiveNamespace = useRegistry((s) => s.setActiveNamespace);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { lockNamespace } = useStudioConfig();

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

  // The way back from a wrong exclusion without a restart (feature namespace-startup-load). The
  // activated namespace only becomes readable to this screen once the INVENTORY says so, because
  // the branch below decides on `entry.state` - hence the invalidate, rather than telling an
  // operator who just fixed it to reload the page.
  const activate = useMutation({
    mutationFn: () => activateNamespace(instance!, ns),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [instance!.id, "namespaces"] }),
  });

  const entries = namespaces.data?.namespaces;
  const entry = entries?.find((candidate) => candidate.name === ns);
  if (instance && entries && !entry) {
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
          {/* Not offered when the embed is pinned to one namespace (feature
              studio-embeddable): switching away is exactly what lockNamespace hides. */}
          {!lockNamespace && (
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
          )}
        </div>
      </div>
    );
  }

  // A namespace this Fallen-8 catalogs but did not load (feature namespace-startup-load).
  // Deliberately NOT the recover state above: its primary action recreates the namespace EMPTY,
  // and here the graph is intact on disk, so offering it would destroy exactly what the operator
  // came back for. Prose register and the ordinary dim text rather than the warn/danger palette:
  // nothing is broken, the namespace is simply not resident in this process.
  if (instance && entry?.state === "notLoaded") {
    return (
      <div
        data-testid="namespace-not-loaded"
        className="text-fg-dim flex flex-col items-start gap-3"
      >
        <div>
          Namespace <span className="text-fg font-semibold">“{ns}”</span> exists on
          “{instance.name}” but was not loaded into the running process, so no screen can read it
          here. Its graph and its write-ahead log are untouched on disk.
        </div>
        {/* Two ways back that answer two different questions, stated as facts about the server
            rather than as button instructions, so the sentence still reads correctly in an embed
            where the buttons below are hidden. Activation NOT changing the persisted policy is
            the load-bearing half: an operator who reads it as permanent loses the namespace again
            at the next restart. */}
        <div>
          Activating it loads it into this process right away, with no restart. Its “at startup”
          policy, in the Namespaces panel on the Connect screen, decides the next boot instead:
          activating does not change that policy, and the policy takes effect on restart, so a
          namespace left on <span className="text-fg">skip</span> is not loaded again after one.
        </div>
        {/* No buttons at all under lockNamespace (feature studio-embeddable): an embed scoped to
            one graph must not re-plan the host's boot - activation included, since it decides what
            the host's process holds - and switching away is precisely what lockNamespace hides. */}
        {!lockNamespace && (
          <div className="flex flex-col items-start gap-3">
            <div className="flex gap-2">
              <button
                type="button"
                data-testid="namespace-not-loaded-activate"
                className="btn btn-accent"
                disabled={activate.isPending}
                /* The name is encoded exactly as the client encodes it in the request URL: a
                   namespace name may hold a space, "#" or "%", and a title that prints the raw
                   one hands the operator a URL that does not work. */
                title={`POST /ns/${encodeURIComponent(ns)}/activate - loads it into this process; the startup-load policy is untouched`}
                onClick={() => activate.mutate()}
              >
                {activate.isPending ? "Activating…" : "Activate now"}
              </button>
              <button
                type="button"
                data-testid="namespace-not-loaded-manage"
                className="btn"
                onClick={() => navigate({ to: "/" })}
              >
                Manage namespaces
              </button>
              <button
                type="button"
                data-testid="namespace-not-loaded-switch"
                className="btn"
                onClick={() =>
                  navigate({ to: "/q/$ns/dashboard", params: { ns: DEFAULT_NAMESPACE } })
                }
              >
                Switch to “{DEFAULT_NAMESPACE}”
              </button>
            </div>
            {/* A refused activation is shown, never swallowed: its 500 detail is the loader's own
                account of why the checkpoint could not be restored, which is the only thing that
                tells an operator whether retrying is pointless. */}
            {activate.isError && <ErrorBox error={activate.error} />}
          </div>
        )}
      </div>
    );
  }

  return <Outlet />;
}
