import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Fallen-8 REST routes are root-level (see features/done/web-ui/spec.md §5). In dev the app is
// served by Vite, so requests against the same-origin "local" instance (baseUrl "") are
// proxied to a locally running fallen-8-core-apiApp. In production the SPA is served by the
// apiApp itself (see Program.cs, gap G-1) and no proxy is involved.
const API_PREFIXES = [
  "/status",
  "/graph",
  "/vertex",
  "/edge",
  "/graphelement",
  "/scan",
  "/path",
  "/subgraph",
  "/index",
  "/delegates",
  "/save",
  "/load",
  "/trim",
  "/tabularasa",
  "/generate",
  "/benchmark",
  "/plugin",
  "/changefeed",
];

const API_TARGET = process.env.F8_API_URL ?? "http://localhost:5000";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: Object.fromEntries(
      API_PREFIXES.map((prefix) => [prefix, { target: API_TARGET, changeOrigin: true }]),
    ),
  },
  build: {
    chunkSizeWarningLimit: 4500, // monaco + sigma are intentionally bundled (self-contained)
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./tests/setup.ts"],
    include: ["tests/**/*.test.{ts,tsx}"],
    globals: true,
  },
});
