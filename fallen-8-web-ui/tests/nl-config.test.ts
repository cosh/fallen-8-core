import { beforeEach, describe, expect, it, vi, afterEach } from "vitest";
import {
  DEFAULT_NL_CONFIG,
  isLoopbackEndpoint,
  isNlConfigured,
  usesApiKey,
} from "../src/delegate/nl/config";
import { chatWithModel } from "../src/delegate/nl/generate";
import { validateDelegate } from "../src/api/endpoints";
import type { InstanceConfig } from "../src/instances/types";

describe("NL assist enablement (FR-26.8)", () => {
  it("is disabled without an endpoint", () => {
    expect(isNlConfigured(DEFAULT_NL_CONFIG)).toBe(false);
    expect(isNlConfigured({ ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" })).toBe(
      true,
    );
  });
});

describe("loopback detection (FR-26.10)", () => {
  it("treats localhost forms as loopback", () => {
    expect(isLoopbackEndpoint("http://localhost:11434")).toBe(true);
    expect(isLoopbackEndpoint("http://127.0.0.1:11434")).toBe(true);
    expect(isLoopbackEndpoint("http://[::1]:11434")).toBe(true);
  });

  it("treats remote hosts as non-loopback", () => {
    expect(isLoopbackEndpoint("https://api.example.com")).toBe(false);
    expect(isLoopbackEndpoint("http://192.168.1.20:11434")).toBe(false);
  });
});

describe("API key applicability (FR-26.12)", () => {
  it("never applies to the Ollama kind, regardless of endpoint", () => {
    expect(usesApiKey(DEFAULT_NL_CONFIG)).toBe(false);
    expect(
      usesApiKey({ ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" }),
    ).toBe(false);
    expect(
      usesApiKey({ ...DEFAULT_NL_CONFIG, endpoint: "https://ollama.example.com" }),
    ).toBe(false);
  });

  it("applies to OpenAI-compatible custom endpoints", () => {
    expect(usesApiKey({ ...DEFAULT_NL_CONFIG, apiKind: "openai" })).toBe(true);
  });
});

describe("key isolation (FR-26.11)", () => {
  interface Recorded {
    url: string;
    headers: Record<string, string>;
  }
  let recorded: Recorded[] = [];

  beforeEach(() => {
    recorded = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        recorded.push({
          url,
          headers: (init?.headers as Record<string, string>) ?? {},
        });
        return new Response(
          JSON.stringify({
            message: { content: "return (v) => true;" },
            choices: [{ message: { content: "return (v) => true;" } }],
            valid: true,
            diagnostics: [],
          }),
          { status: 200 },
        );
      }),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("sends the model key only to the model endpoint, never to Fallen-8", async () => {
    const modelConfig = {
      ...DEFAULT_NL_CONFIG,
      endpoint: "http://models.example.com",
      apiKind: "openai" as const,
      apiKey: "MODEL-SECRET",
    };
    await chatWithModel(modelConfig, [{ role: "user", content: "hi" }]);

    const instance: InstanceConfig = {
      id: "t",
      name: "t",
      baseUrl: "http://f8.test",
      auth: { kind: "none" },
    };
    await validateDelegate(instance, "VertexFilter", "return (v) => true;");

    const modelCall = recorded.find((r) => r.url.includes("models.example.com"));
    const f8Call = recorded.find((r) => r.url.includes("f8.test"));
    expect(modelCall?.headers.Authorization).toBe("Bearer MODEL-SECRET");
    expect(f8Call).toBeDefined();
    expect(JSON.stringify(f8Call)).not.toContain("MODEL-SECRET");
  });

  it("uses the Ollama-native route for apiKind ollama", async () => {
    await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" },
      [{ role: "user", content: "hi" }],
    );
    expect(recorded[0].url).toBe("http://localhost:11434/api/chat");
  });

  it("uses /v1/chat/completions for openai-compatible endpoints", async () => {
    await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:8080", apiKind: "openai" },
      [{ role: "user", content: "hi" }],
    );
    expect(recorded[0].url).toBe("http://localhost:8080/v1/chat/completions");
  });
});
