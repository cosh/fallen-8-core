import type * as Monaco from "monaco-editor";
import typeModel from "./type-model.json";
import { KIND_INFO } from "./kinds";
import { snippetCodeFor, snippetsForKind } from "./snippets";
import type { DelegateKind } from "../api/types";

/**
 * Static IntelliSense (FR-22): completions, hovers, and signature help driven entirely by
 * the checked-in type model - the server's validate endpoint stays the only compile
 * authority. Members are scoped to the slot's parameter type: after `v.` a VertexFilter
 * offers AGraphElementModel + VertexModel members; an EdgePropertyFilter's `p.` offers
 * string members only and no graph model.
 */

interface ModelMember {
  name: string;
  kind: string;
  signature: string;
  doc: string;
}

interface ModelType {
  base: string | null;
  members: ModelMember[];
}

const TYPES = (typeModel as { types: Record<string, ModelType> }).types;

export function membersForType(typeName: string): ModelMember[] {
  const collected: ModelMember[] = [];
  let current: string | null = typeName;
  while (current) {
    const type: ModelType | undefined = TYPES[current];
    if (!type) break;
    collected.push(...type.members);
    current = type.base;
  }
  return collected;
}

export function memberByName(name: string): ModelMember | undefined {
  for (const type of Object.values(TYPES)) {
    const member = type.members.find((m) => m.name === name);
    if (member) return member;
  }
  return undefined;
}

function completionKind(
  monaco: typeof Monaco,
  kind: string,
): Monaco.languages.CompletionItemKind {
  switch (kind) {
    case "method":
      return monaco.languages.CompletionItemKind.Method;
    case "property":
      return monaco.languages.CompletionItemKind.Property;
    default:
      return monaco.languages.CompletionItemKind.Field;
  }
}

/** Registers all providers for one editor session; returns a dispose function. */
export function registerDelegateProviders(
  monaco: typeof Monaco,
  delegateKind: DelegateKind,
): () => void {
  const info = KIND_INFO[delegateKind];
  const disposables: Monaco.IDisposable[] = [];

  disposables.push(
    monaco.languages.registerCompletionItemProvider("csharp", {
      triggerCharacters: ["."],
      provideCompletionItems(model, position) {
        const line = model.getLineContent(position.lineNumber);
        const before = line.slice(0, position.column - 1);

        const word = model.getWordUntilPosition(position);
        const range: Monaco.IRange = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        };

        // Member access: `<identifier>.` - members only when the identifier is the
        // slot's parameter (the fragment's one input).
        const memberAccess = /([A-Za-z_][A-Za-z0-9_]*)\.\s*[A-Za-z0-9_]*$/.exec(before);
        if (memberAccess) {
          if (memberAccess[1] !== info.parameterName) {
            return { suggestions: [] };
          }
          return {
            suggestions: membersForType(info.parameterType).map((member) => ({
              label: member.name,
              kind: completionKind(monaco, member.kind),
              detail: member.signature,
              documentation: member.doc,
              insertText:
                member.kind === "method" && member.name.startsWith("TryGet")
                  ? `${member.name}(out $1, "$2")$0`
                  : member.kind === "method"
                    ? `${member.name}($1)$0`
                    : member.name,
              insertTextRules:
                member.kind === "method"
                  ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet
                  : undefined,
              range,
            })),
          };
        }

        // Top level: the parameter identifier + the snippet library for this kind.
        const suggestions: Monaco.languages.CompletionItem[] = [
          {
            label: info.parameterName,
            kind: monaco.languages.CompletionItemKind.Variable,
            detail: `${info.parameterType} ${info.parameterName}`,
            documentation: `The ${delegateKind} parameter.`,
            insertText: info.parameterName,
            range,
          },
          ...snippetsForKind(delegateKind).map((snippet) => ({
            label: snippet.title,
            kind: monaco.languages.CompletionItemKind.Snippet,
            detail: snippet.description,
            insertText: snippetCodeFor(snippet, info.parameterName),
            range,
          })),
        ];
        return { suggestions };
      },
    }),
  );

  disposables.push(
    monaco.languages.registerHoverProvider("csharp", {
      provideHover(model, position) {
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        if (word.word === info.parameterName) {
          return {
            contents: [
              { value: `\`${info.parameterType} ${info.parameterName}\`` },
              { value: `The ${delegateKind} lambda parameter (${info.lambdaShape}).` },
            ],
          };
        }
        const member = memberByName(word.word);
        if (!member) return null;
        return {
          contents: [{ value: `\`${member.signature}\`` }, { value: member.doc }],
        };
      },
    }),
  );

  disposables.push(
    monaco.languages.registerSignatureHelpProvider("csharp", {
      signatureHelpTriggerCharacters: ["(", ","],
      provideSignatureHelp(model, position) {
        const line = model
          .getLineContent(position.lineNumber)
          .slice(0, position.column - 1);
        const call = /([A-Za-z_][A-Za-z0-9_]*)\s*\([^()]*$/.exec(line);
        if (!call) return null;
        const member = memberByName(call[1]);
        if (!member || member.kind !== "method") return null;

        const parameterList = member.signature.replace(/^[^(]*\(/, "").replace(/\)$/, "");
        const parameters = parameterList
          .split(",")
          .map((p) => ({ label: p.trim() }))
          .filter((p) => p.label.length > 0);
        const commas = (line.slice(line.lastIndexOf("(")).match(/,/g) ?? []).length;

        return {
          value: {
            signatures: [
              {
                label: member.signature,
                documentation: member.doc,
                parameters,
              },
            ],
            activeSignature: 0,
            activeParameter: Math.min(commas, Math.max(parameters.length - 1, 0)),
          },
          dispose() {},
        };
      },
    }),
  );

  return () => disposables.forEach((d) => d.dispose());
}
