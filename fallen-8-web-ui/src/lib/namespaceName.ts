/**
 * Client mirror of the server's namespace-name rule (Fallen8Namespaces.IsValidName). Names
 * are permissive — any case, spaces, punctuation, Unicode — because on-disk storage is keyed
 * by an internal id, not the name; a name is only a display label, a map key, and a URL PATH
 * SEGMENT (/ns/{name}/…, /q/{name}/…). That last role fixes the only hard limits: no "/" or
 * "\" (an encoded slash can't round-trip through the server), no control characters, not
 * "."/".." (path-traversal tokens), no leading/trailing whitespace, and a length cap.
 */
export const NAMESPACE_NAME_MAX = 63;

export function isValidNamespaceName(name: string): boolean {
  if (name.length === 0 || name.length > NAMESPACE_NAME_MAX) return false;
  if (name.trim().length === 0 || name !== name.trim()) return false;
  if (name === "." || name === "..") return false;
  for (const ch of name) {
    const code = ch.codePointAt(0)!;
    if (ch === "/" || ch === "\\" || code < 0x20 || code === 0x7f) return false;
  }
  return true;
}
