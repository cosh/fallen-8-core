import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { truncateChars, isTruncated } from "../src/lib/truncate";
import { Truncated } from "../src/components/Truncated";

/**
 * The shared display-cap for arbitrary user-controlled strings (namespace names, labels,
 * property values, paths) so none can blow out a view. The full value stays reachable in
 * the title tooltip.
 */

describe("truncateChars", () => {
  it("leaves text at or under the cap untouched", () => {
    expect(truncateChars("fraud-q3", 32)).toBe("fraud-q3");
    expect(truncateChars("exactly-ten", 11)).toBe("exactly-ten");
  });

  it("end-clips past the cap, ellipsis included in the budget", () => {
    const out = truncateChars("abcdefghijklmnop", 10);
    expect(out).toBe("abcdefghi…");
    expect([...out].length).toBe(10);
  });

  it("middle-clips keeping both ends for path-shaped values", () => {
    const out = truncateChars("/ns/very-long-namespace-name/status", 16, { middle: true });
    expect([...out].length).toBe(16);
    expect(out.startsWith("/ns/")).toBe(true);
    expect(out.endsWith("tus")).toBe(true);
    expect(out).toContain("…");
  });

  it("degrades gracefully at tiny caps", () => {
    expect(truncateChars("anything", 1)).toBe("anything"); // max<=1 is a no-op, never just "…"
    expect(truncateChars("anything", 2)).toBe("a…");
  });

  it("isTruncated reports whether a cap would bite", () => {
    expect(isTruncated("short", 32)).toBe(false);
    expect(isTruncated("a".repeat(64), 32)).toBe(true);
  });
});

describe("Truncated", () => {
  it("char mode clips and exposes the full value via title", () => {
    render(<Truncated text={"a".repeat(50)} max={20} />);
    const el = screen.getByText(/…$/);
    expect([...el.textContent!].length).toBe(20);
    expect(el).toHaveAttribute("title", "a".repeat(50));
  });

  it("char mode adds no title when nothing was clipped", () => {
    render(<Truncated text="flights" max={20} />);
    const el = screen.getByText("flights");
    expect(el).not.toHaveAttribute("title");
  });

  it("CSS mode (no max) applies the truncate class and always offers the full title", () => {
    render(<Truncated text="flights" className="font-semibold" />);
    const el = screen.getByText("flights");
    expect(el.className).toContain("truncate");
    expect(el.className).toContain("font-semibold");
    expect(el).toHaveAttribute("title", "flights");
  });
});
