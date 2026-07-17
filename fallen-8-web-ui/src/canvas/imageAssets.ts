import type { ImageSpec } from "./styleEngine";

/**
 * Emoji/text node textures (FR-5): the Studio rasterizes emoji itself onto a
 * transparent canvas and serves the result as a data URL, so emoticon nodes need no
 * hosting. URL specs pass through untouched. Kept separate from the style engine so
 * the engine stays pure (no DOM) and unit-testable.
 */

const TEXTURE_SIZE = 64;
const cache = new Map<string, string>();

function rasterizeEmoji(text: string): string {
  const canvas = document.createElement("canvas");
  canvas.width = TEXTURE_SIZE;
  canvas.height = TEXTURE_SIZE;
  const ctx = canvas.getContext("2d");
  if (!ctx) return "";
  // Shrink to fit multi-character values; single emoji fill the tile.
  const chars = [...text].length;
  const fontSize = Math.floor(TEXTURE_SIZE * (chars <= 2 ? 0.78 : chars <= 4 ? 0.4 : 0.22));
  ctx.font = `${fontSize}px "Segoe UI Emoji", "Noto Color Emoji", "Apple Color Emoji", sans-serif`;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillStyle = "#cdd6e4";
  ctx.fillText(text, TEXTURE_SIZE / 2, TEXTURE_SIZE / 2 + 2);
  return canvas.toDataURL("image/png");
}

/** Resolves an ImageSpec to something an <img>/texture loader can consume. */
export function imageUrlFor(spec: ImageSpec): string {
  if (spec.kind === "url") return spec.value;
  let url = cache.get(spec.value);
  if (url === undefined) {
    url = rasterizeEmoji(spec.value);
    cache.set(spec.value, url);
  }
  return url;
}
