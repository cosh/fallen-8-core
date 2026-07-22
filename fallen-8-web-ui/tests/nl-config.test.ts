import { beforeEach, describe, expect, it, vi, afterEach } from "vitest";
import {
  BUILTIN_NL_BACKEND,
  DEFAULT_NL_CONFIG,
  effectiveNlConfig,
  isLoopbackEndpoint,
  isNlConfigured,
  migrateNlState,
  NL_PRESETS,
  usesApiKey,
} from "../src/delegate/nl/config";
import { chatWithModel, probeEndpoint } from "../src/delegate/nl/generate";
import { validateDelegate } from "../src/api/endpoints";
import type { InstanceConfig } from "../src/instances/types";

describe("NL assist enablement (FR-26.8 / nl-assist-ux FR-1)", () => {
  it("builtin mode is always configured — zero-config default", () => {
    expect(DEFAULT_NL_CONFIG.mode).toBe("builtin");
    expect(isNlConfigured(DEFAULT_NL_CONFIG)).toBe(true);
    // Pin the shipped default fine-tune (delegate-model-variants clean rename): the builtin/default
    // model is 'phi4-f8-mini', NOT the retired 'f8-delegate'. A literal check (not vs a constant) so
    // a regression fails CI instead of silently 404ing every builtin NL-assist call.
    expect(BUILTIN_NL_BACKEND.model).toBe("phi4-f8-mini");
    expect(DEFAULT_NL_CONFIG.model).toBe("phi4-f8-mini");
  });

  it("custom mode needs an endpoint and model", () => {
    expect(isNlConfigured({ ...DEFAULT_NL_CONFIG, mode: "custom" })).toBe(false);
    expect(
      isNlConfigured({
        ...DEFAULT_NL_CONFIG,
        mode: "custom",
        endpoint: "http://localhost:11434",
      }),
    ).toBe(true);
    expect(
      isNlConfigured({
        ...DEFAULT_NL_CONFIG,
        mode: "custom",
        endpoint: "http://localhost:11434",
        model: " ",
      }),
    ).toBe(false);
  });
});

describe("effective config resolution (nl-assist-ux FR-1/FR-3)", () => {
  it("builtin pins the compose-shipped backend regardless of stored fields", () => {
    const effective = effectiveNlConfig({
      ...DEFAULT_NL_CONFIG,
      mode: "builtin",
      endpoint: "https://somewhere.example.com",
      apiKind: "openai",
      model: "other",
      apiKey: "SHOULD-NOT-SURVIVE",
    });
    expect(effective.endpoint).toBe(BUILTIN_NL_BACKEND.endpoint);
    expect(effective.apiKind).toBe("ollama");
    expect(effective.model).toBe(BUILTIN_NL_BACKEND.model);
    expect(effective.apiKey).toBeUndefined();
  });

  it("custom passes the stored fields through", () => {
    const config = {
      ...DEFAULT_NL_CONFIG,
      mode: "custom" as const,
      endpoint: "https://api.openai.com/v1",
      apiKind: "openai" as const,
      model: "gpt-4o-mini",
    };
    expect(effectiveNlConfig(config)).toEqual(config);
  });
});

describe("persisted-state migration (nl-assist-ux FR-4)", () => {
  it("keeps a previously configured endpoint as custom mode", () => {
    const migrated = migrateNlState({
      config: { endpoint: "http://my-box:11434", apiKind: "ollama", model: "phi4" },
    });
    expect(migrated.config?.mode).toBe("custom");
    expect(migrated.config?.endpoint).toBe("http://my-box:11434");
    expect(migrated.config?.model).toBe("phi4");
    // Missing fields fill from defaults.
    expect(migrated.config?.maxRetries).toBe(DEFAULT_NL_CONFIG.maxRetries);
  });

  it("moves unconfigured users to the builtin default", () => {
    expect(migrateNlState({ config: { endpoint: "" } }).config?.mode).toBe("builtin");
    expect(migrateNlState(undefined).config?.mode).toBe("builtin");
  });

  it("never rewrites an explicit mode", () => {
    const migrated = migrateNlState({
      config: { mode: "builtin", endpoint: "http://custom-left-over:1" },
    });
    expect(migrated.config?.mode).toBe("builtin");
  });
});

describe("presets (nl-assist-ux FR-3)", () => {
  it("offers both fine-tuned variants, both stock bases, and hosted prefills", () => {
    const names = NL_PRESETS.map((preset) => preset.name);
    // Both fine-tunes and both stock bases are selectable (feature delegate-model-variants).
    expect(names).toContain("Ollama (fine-tuned phi4-f8-mini)");
    expect(names).toContain("Ollama (fine-tuned phi4-f8 — GPU)");
    expect(names).toContain("Ollama (stock phi4-mini)");
    expect(names).toContain("Ollama (stock phi4 — GPU)");
    expect(names).toContain("OpenAI");
    expect(names).toContain("Anthropic");
    const mini = NL_PRESETS.find((preset) => preset.name === "Ollama (fine-tuned phi4-f8-mini)")!;
    expect(mini).toMatchObject({ endpoint: "http://localhost:11434", apiKind: "ollama", model: "phi4-f8-mini" });
    const big = NL_PRESETS.find((preset) => preset.name === "Ollama (fine-tuned phi4-f8 — GPU)")!;
    expect(big).toMatchObject({ apiKind: "ollama", model: "phi4-f8" });
    // Hosted (non-Ollama) presets ride the OpenAI-compatible transport (the only kind with a
    // key field); all local presets use the Ollama transport.
    for (const hosted of NL_PRESETS.filter((preset) => !preset.name.startsWith("Ollama"))) {
      expect(hosted.apiKind).toBe("openai");
    }
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

describe("transport, stats, and key isolation", () => {
  interface Recorded {
    url: string;
    headers: Record<string, string>;
  }
  let recorded: Recorded[] = [];
  let responseBody: Record<string, unknown>;

  beforeEach(() => {
    recorded = [];
    responseBody = {
      message: { content: "return (v) => true;" },
      choices: [{ message: { content: "return (v) => true;" } }],
      valid: true,
      diagnostics: [],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        recorded.push({
          url,
          headers: (init?.headers as Record<string, string>) ?? {},
        });
        return new Response(JSON.stringify(responseBody), { status: 200 });
      }),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("sends the model key only to the model endpoint, never to Fallen-8 (FR-26.11)", async () => {
    const modelConfig = {
      ...DEFAULT_NL_CONFIG,
      mode: "custom" as const,
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
    const result = await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" },
      [{ role: "user", content: "hi" }],
    );
    expect(recorded[0].url).toBe("http://localhost:11434/api/chat");
    expect(result.content).toBe("return (v) => true;");
  });

  it("uses /v1/chat/completions for openai-compatible endpoints", async () => {
    await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:8080", apiKind: "openai" },
      [{ role: "user", content: "hi" }],
    );
    expect(recorded[0].url).toBe("http://localhost:8080/v1/chat/completions");
  });

  it("normalizes Ollama stats (ns durations, tokens/s) and keeps the raw payload (FR-5)", async () => {
    responseBody = {
      message: { content: "return (v) => true;" },
      total_duration: 3_200_000_000,
      prompt_eval_count: 812,
      eval_count: 24,
      eval_duration: 3_000_000_000,
    };
    const { stats } = await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" },
      [{ role: "user", content: "hi" }],
    );
    expect(stats?.promptTokens).toBe(812);
    expect(stats?.completionTokens).toBe(24);
    expect(stats?.durationMs).toBe(3200);
    expect(stats?.tokensPerSecond).toBe(8);
    expect(stats?.raw).toMatchObject({ eval_count: 24, total_duration: 3_200_000_000 });
    expect(stats?.raw).not.toHaveProperty("message");
  });

  it("normalizes OpenAI-compatible usage stats (FR-5)", async () => {
    responseBody = {
      model: "gpt-4o-mini",
      choices: [{ message: { content: "return (v) => true;" } }],
      usage: { prompt_tokens: 900, completion_tokens: 31, total_tokens: 931 },
    };
    const { stats } = await chatWithModel(
      { ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:8080", apiKind: "openai" },
      [{ role: "user", content: "hi" }],
    );
    expect(stats?.promptTokens).toBe(900);
    expect(stats?.completionTokens).toBe(31);
    expect(stats?.raw).toMatchObject({ model: "gpt-4o-mini" });
  });

  it("probes /api/version for ollama and /v1/models for openai (FR-2)", async () => {
    await probeEndpoint({ ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" });
    await probeEndpoint({
      ...DEFAULT_NL_CONFIG,
      endpoint: "https://api.openai.com/v1",
      apiKind: "openai",
    });
    expect(recorded[0].url).toBe("http://localhost:11434/api/version");
    expect(recorded[1].url).toBe("https://api.openai.com/v1/models");
  });

  it("reports an unreachable endpoint as false instead of throwing (FR-2)", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("Failed to fetch");
      }),
    );
    await expect(
      probeEndpoint({ ...DEFAULT_NL_CONFIG, endpoint: "http://localhost:11434" }),
    ).resolves.toBe(false);
  });
});
