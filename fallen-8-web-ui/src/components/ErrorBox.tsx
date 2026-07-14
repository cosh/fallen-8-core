import { ApiError } from "../api/client";

/**
 * Every failed request shows HTTP status + server message (NFR: never a silent console
 * error). A network-level failure renders as the disconnected state with a retry.
 */
export function ErrorBox({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  let title = "Request failed";
  let detail = "";

  if (error instanceof ApiError) {
    title = `HTTP ${error.status}`;
    detail = error.body || "(no response body)";
  } else if (error instanceof TypeError) {
    title = "Instance unreachable";
    detail = "The endpoint did not respond. Is the server running?";
  } else if (error instanceof Error) {
    detail = error.message;
  }

  return (
    <div
      role="alert"
      className="border-danger/40 bg-danger/5 text-danger rounded border px-3 py-2 text-[12px]"
    >
      <div className="font-semibold">{title}</div>
      {detail && <div className="text-danger/80 mt-1 break-all whitespace-pre-wrap">{detail}</div>}
      {onRetry && (
        <button type="button" className="btn btn-danger mt-2" onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  );
}
