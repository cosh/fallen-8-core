import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { loader } from "@monaco-editor/react";

/**
 * Bundle Monaco locally (no CDN): the app must work offline/self-contained, and the C#
 * colorizer is the built-in basic-languages tokenizer - no language worker needed beyond
 * the core editor worker.
 */

let configured = false;

export function setupMonaco(): void {
  if (configured) return;
  configured = true;

  self.MonacoEnvironment = {
    getWorker: () => new editorWorker(),
  };

  loader.config({ monaco });
}

export { monaco };
