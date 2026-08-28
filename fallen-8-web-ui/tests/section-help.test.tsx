// MIT License
//
// section-help.test.tsx
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

/// <reference types="node" />

import { readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { NAV } from "../src/app/nav";
import { DOCS_BASE, SECTION_HELP, docUrl, sectionHelp } from "../src/lib/sectionHelp";
import { SectionHelp } from "../src/components/SectionHelp";

/**
 * Per-section help (feature studio-section-help). The registry in lib/sectionHelp.ts is the ONE
 * home mapping each nav section to 1-3 docs pages. These tests pin the "max 3" cap, force a
 * mapping for every nav leaf (adding a section forces a decision), and guard against a slug that
 * does not resolve to a real docs page (a dead in-app link).
 */
describe("section help registry", () => {
  it("maps every section to between one and three doc links", () => {
    for (const [key, entry] of Object.entries(SECTION_HELP)) {
      expect(entry.links.length, `SECTION_HELP["${key}"].links`).toBeGreaterThanOrEqual(1);
      expect(entry.links.length, `SECTION_HELP["${key}"].links`).toBeLessThanOrEqual(3);
    }
  });

  it("has a heading and non-empty titles and blurbs for every link", () => {
    for (const [key, entry] of Object.entries(SECTION_HELP)) {
      expect(entry.heading.trim().length, `SECTION_HELP["${key}"].heading`).toBeGreaterThan(4);
      for (const link of entry.links) {
        expect(link.title.trim().length, `${key} -> ${link.slug} title`).toBeGreaterThan(0);
        expect(link.blurb.trim().length, `${key} -> ${link.slug} blurb`).toBeGreaterThan(15);
      }
    }
  });

  it("covers every nav leaf, so adding a section forces a help mapping", () => {
    for (const item of NAV) {
      expect(sectionHelp(item.leaf), `no SECTION_HELP entry for nav leaf "${item.leaf}"`).toBeDefined();
    }
  });

  it("references only slugs that resolve to a real docs page (no dead in-app links)", () => {
    // docs/ is a sibling of fallen-8-web-ui/ at the repo root; this test file lives in
    // fallen-8-web-ui/tests, so go up two levels then into the Starlight content dir.
    const here = dirname(fileURLToPath(import.meta.url));
    const docsDir = resolve(here, "..", "..", "docs", "src", "content", "docs");
    const slugs = new Set(
      readdirSync(docsDir)
        .filter((f) => f.endsWith(".md") || f.endsWith(".mdx"))
        .map((f) => f.replace(/\.mdx?$/, "")),
    );
    const referenced = new Set(
      Object.values(SECTION_HELP).flatMap((e) => e.links.map((l) => l.slug)),
    );
    for (const slug of referenced) {
      expect(slugs.has(slug), `SECTION_HELP references missing docs page "${slug}"`).toBe(true);
    }
  });

  it("builds absolute docs URLs from the shared DOCS_BASE constant", () => {
    expect(DOCS_BASE.endsWith("/")).toBe(true);
    expect(docUrl("path-finding")).toBe(`${DOCS_BASE}path-finding/`);
  });
});

describe("SectionHelp component", () => {
  it("renders nothing for a null key or an unmapped section", () => {
    const { rerender } = render(<SectionHelp sectionKey={null} />);
    expect(screen.queryByTestId("section-help")).not.toBeInTheDocument();
    rerender(<SectionHelp sectionKey="does-not-exist" />);
    expect(screen.queryByTestId("section-help")).not.toBeInTheDocument();
  });

  it("labels the button by the active section and hides the popover until clicked", () => {
    render(<SectionHelp sectionKey="traverse" />);
    const button = screen.getByTestId("section-help");
    // The visible text is the accessible name (WCAG 2.5.3); the section is conveyed by the title.
    expect(button).toHaveTextContent("How does this work?");
    expect(button).toHaveAttribute("title", "How traversal works");
    expect(button).not.toHaveAttribute("aria-label");
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByTestId("section-help-popover")).not.toBeInTheDocument();
  });

  it("opens a popover listing the section's doc links, each opening a new tab safely", async () => {
    const user = userEvent.setup();
    render(<SectionHelp sectionKey="traverse" />);

    await user.click(screen.getByTestId("section-help"));

    const popover = screen.getByTestId("section-help-popover");
    expect(popover).toHaveTextContent("How traversal works");
    const links = screen.getAllByTestId("section-help-link");
    expect(links).toHaveLength(SECTION_HELP.traverse.links.length);

    const first = links[0];
    expect(first).toHaveAttribute("href", `${DOCS_BASE}path-finding/`);
    expect(first).toHaveTextContent("Path finding");
    for (const link of links) {
      expect(link).toHaveAttribute("target", "_blank");
      expect(link.getAttribute("rel")).toContain("noopener");
    }
    expect(screen.getByTestId("section-help")).toHaveAttribute("aria-expanded", "true");
  });

  it("closes the popover on Escape and returns focus to the button", async () => {
    const user = userEvent.setup();
    render(<SectionHelp sectionKey="traverse" />);
    await user.click(screen.getByTestId("section-help"));
    expect(screen.getByTestId("section-help-popover")).toBeInTheDocument();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByTestId("section-help-popover")).not.toBeInTheDocument();
    expect(screen.getByTestId("section-help")).toHaveFocus();
  });

  it("closes the popover on an outside click", async () => {
    const user = userEvent.setup();
    render(
      <div>
        <button data-testid="outside">outside</button>
        <SectionHelp sectionKey="query" />
      </div>,
    );
    await user.click(screen.getByTestId("section-help"));
    expect(screen.getByTestId("section-help-popover")).toBeInTheDocument();

    fireEvent.mouseDown(screen.getByTestId("outside"));
    expect(screen.queryByTestId("section-help-popover")).not.toBeInTheDocument();
    expect(screen.getByTestId("section-help")).toHaveAttribute("aria-expanded", "false");
  });

  it("closes the popover after a link is chosen", async () => {
    const user = userEvent.setup();
    render(<SectionHelp sectionKey="indexes" />);
    await user.click(screen.getByTestId("section-help"));
    const links = screen.getAllByTestId("section-help-link");
    await user.click(links[0]);
    expect(screen.queryByTestId("section-help-popover")).not.toBeInTheDocument();
  });
});
