// MIT License
//
// DocumentChunker.cs
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

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   Deterministic document-to-chunk splitting (spec unstructured-ingestion FR-6).
    ///   Primary path: the structured DoclingDocument (heading hierarchy, intact tables, page
    ///   provenance). Fallback: markdown heading-split (txt/md ingest, or a conversion without
    ///   <c>json_content</c>). Both share one assembly: sections along headings, merge below
    ///   <c>ChunkMinChars</c>, split above <c>ChunkMaxChars</c> at paragraph (or table
    ///   row-window) boundaries, identifiers extracted per final chunk.
    /// </summary>
    public static class DocumentChunker
    {
        private static readonly Regex MarkdownHeading =
            new Regex(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

        #region content items (the shared intermediate)

        private enum ItemKind : byte
        {
            Heading = 0,
            Paragraph = 1,
            Table = 2
        }

        private sealed class ContentItem
        {
            public ItemKind Kind;
            public Int32 Level;                     // headings only
            public String Text;                     // heading title / paragraph text
            public List<List<String>> Grid;         // tables only (cell texts)
            public Int32? PageFrom;
            public Int32? PageTo;
        }

        #endregion

        #region public entry points

        /// <summary>Chunks a structured DoclingDocument (the primary path).</summary>
        public static List<DocumentChunk> ChunkStructured(DoclingDocumentModel document, Fallen8IngestionOptions options)
        {
            return Assemble(WalkStructured(document), options);
        }

        /// <summary>Chunks markdown by heading structure (the fallback path).</summary>
        public static List<DocumentChunk> ChunkMarkdown(String markdown, Fallen8IngestionOptions options)
        {
            return Assemble(ParseMarkdown(markdown), options);
        }

        /// <summary>Chunks plain text as one section (bounds still apply).</summary>
        public static List<DocumentChunk> ChunkPlainText(String text, Fallen8IngestionOptions options)
        {
            var items = new List<ContentItem>();
            if (!String.IsNullOrWhiteSpace(text))
            {
                items.Add(new ContentItem { Kind = ItemKind.Paragraph, Text = text.Trim() });
            }

            return Assemble(items, options);
        }

        #endregion

        #region structured walk

        /// <summary>Flattens the DoclingDocument into reading-order content items: the body's
        /// children, groups resolved recursively (cycle-guarded), text items classified by
        /// label, tables carried as grids.</summary>
        private static List<ContentItem> WalkStructured(DoclingDocumentModel document)
        {
            var items = new List<ContentItem>();
            if (document?.Body?.Children == null)
            {
                return items;
            }

            var texts = new Dictionary<String, DoclingTextItem>(StringComparer.Ordinal);
            foreach (var text in document.Texts ?? new List<DoclingTextItem>())
            {
                if (text?.SelfRef != null)
                {
                    texts[text.SelfRef] = text;
                }
            }

            var tables = new Dictionary<String, DoclingTableItem>(StringComparer.Ordinal);
            foreach (var table in document.Tables ?? new List<DoclingTableItem>())
            {
                if (table?.SelfRef != null)
                {
                    tables[table.SelfRef] = table;
                }
            }

            var groups = new Dictionary<String, DoclingGroupItem>(StringComparer.Ordinal);
            foreach (var group in document.Groups ?? new List<DoclingGroupItem>())
            {
                if (group?.SelfRef != null)
                {
                    groups[group.SelfRef] = group;
                }
            }

            var visited = new HashSet<String>(StringComparer.Ordinal);
            WalkChildren(document.Body.Children, texts, tables, groups, visited, items);
            return items;
        }

        private static void WalkChildren(List<DoclingRef> children,
            Dictionary<String, DoclingTextItem> texts,
            Dictionary<String, DoclingTableItem> tables,
            Dictionary<String, DoclingGroupItem> groups,
            HashSet<String> visited, List<ContentItem> items)
        {
            foreach (var child in children ?? new List<DoclingRef>())
            {
                var reference = child?.Ref;
                if (reference == null || !visited.Add(reference))
                {
                    continue;
                }

                if (groups.TryGetValue(reference, out var group))
                {
                    WalkChildren(group.Children, texts, tables, groups, visited, items);
                    continue;
                }

                if (tables.TryGetValue(reference, out var table))
                {
                    var grid = ExtractGrid(table);
                    if (grid.Count > 0)
                    {
                        var (pageFrom, pageTo) = PageSpan(table.Prov);
                        items.Add(new ContentItem { Kind = ItemKind.Table, Grid = grid, PageFrom = pageFrom, PageTo = pageTo });
                    }

                    continue;
                }

                if (!texts.TryGetValue(reference, out var text) || String.IsNullOrWhiteSpace(text.Text))
                {
                    continue;
                }

                var label = text.Label ?? String.Empty;
                if (String.Equals(label, "page_header", StringComparison.Ordinal) ||
                    String.Equals(label, "page_footer", StringComparison.Ordinal))
                {
                    continue;
                }

                var (from, to) = PageSpan(text.Prov);
                if (String.Equals(label, "title", StringComparison.Ordinal))
                {
                    items.Add(new ContentItem { Kind = ItemKind.Heading, Level = 0, Text = text.Text.Trim(), PageFrom = from, PageTo = to });
                }
                else if (String.Equals(label, "section_header", StringComparison.Ordinal))
                {
                    var level = Math.Max(1, text.Level ?? 1);
                    items.Add(new ContentItem { Kind = ItemKind.Heading, Level = level, Text = text.Text.Trim(), PageFrom = from, PageTo = to });
                }
                else
                {
                    items.Add(new ContentItem { Kind = ItemKind.Paragraph, Text = text.Text.Trim(), PageFrom = from, PageTo = to });
                }
            }
        }

        private static List<List<String>> ExtractGrid(DoclingTableItem table)
        {
            var grid = new List<List<String>>();
            foreach (var row in table?.Data?.Grid ?? new List<List<DoclingTableCell>>())
            {
                if (row == null)
                {
                    continue;
                }

                var cells = new List<String>(row.Count);
                foreach (var cell in row)
                {
                    cells.Add(EscapeTableCell(cell?.Text));
                }

                grid.Add(cells);
            }

            return grid;
        }

        private static (Int32?, Int32?) PageSpan(List<DoclingProvenance> prov)
        {
            Int32? from = null, to = null;
            foreach (var entry in prov ?? new List<DoclingProvenance>())
            {
                if (entry?.PageNo == null)
                {
                    continue;
                }

                var page = entry.PageNo.Value;
                from = from == null || page < from ? page : from;
                to = to == null || page > to ? page : to;
            }

            return (from, to);
        }

        #endregion

        #region markdown fallback

        private static List<ContentItem> ParseMarkdown(String markdown)
        {
            var items = new List<ContentItem>();
            if (String.IsNullOrWhiteSpace(markdown))
            {
                return items;
            }

            var matches = MarkdownHeading.Matches(markdown);
            if (matches.Count == 0)
            {
                items.Add(new ContentItem { Kind = ItemKind.Paragraph, Text = markdown.Trim() });
                return items;
            }

            var preContent = markdown.Substring(0, matches[0].Index).Trim();
            if (preContent.Length > 0)
            {
                items.Add(new ContentItem { Kind = ItemKind.Paragraph, Text = preContent });
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                items.Add(new ContentItem
                {
                    Kind = ItemKind.Heading,
                    Level = match.Groups[1].Value.Length,
                    Text = match.Groups[2].Value.Trim()
                });

                var start = match.Index + match.Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
                var content = markdown.Substring(start, end - start).Trim();
                if (content.Length > 0)
                {
                    items.Add(new ContentItem { Kind = ItemKind.Paragraph, Text = content });
                }
            }

            return items;
        }

        #endregion

        #region assembly (sections, merge, split)

        private sealed class RawChunk
        {
            public String Kind;
            public String Text;
            public String HeadingPath;
            public Int32? PageFrom;
            public Int32? PageTo;
        }

        private static List<DocumentChunk> Assemble(List<ContentItem> items, Fallen8IngestionOptions options)
        {
            var raw = BuildSections(items, options.ChunkMaxChars);
            raw = MergeShort(raw, options.ChunkMinChars);
            raw = SplitLong(raw, options.ChunkMaxChars);

            var chunks = new List<DocumentChunk>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                chunks.Add(new DocumentChunk
                {
                    Text = raw[i].Text,
                    Order = i,
                    Kind = raw[i].Kind,
                    HeadingPath = raw[i].HeadingPath,
                    PageFrom = raw[i].PageFrom,
                    PageTo = raw[i].PageTo,
                    Identifiers = IdentifierExtractor.Extract(raw[i].Text, options.MaxIdentifiersPerChunk)
                });
            }

            return chunks;
        }

        private static List<RawChunk> BuildSections(List<ContentItem> items, Int32 maxChars)
        {
            var raw = new List<RawChunk>();
            var headingStack = new List<(Int32 Level, String Title)>();
            var buffer = new List<String>();
            String bufferHeadingPath = null;
            Int32? bufferPageFrom = null, bufferPageTo = null;

            void Flush()
            {
                if (buffer.Count == 0)
                {
                    return;
                }

                raw.Add(new RawChunk
                {
                    Kind = DocumentChunk.TextKind,
                    Text = String.Join("\n\n", buffer),
                    HeadingPath = bufferHeadingPath,
                    PageFrom = bufferPageFrom,
                    PageTo = bufferPageTo
                });
                buffer.Clear();
                bufferPageFrom = null;
                bufferPageTo = null;
            }

            void MergePages(Int32? from, Int32? to)
            {
                bufferPageFrom = Min(bufferPageFrom, from);
                bufferPageTo = Max(bufferPageTo, to);
            }

            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case ItemKind.Heading:
                        Flush();
                        headingStack.RemoveAll(entry => entry.Level >= item.Level);
                        headingStack.Add((item.Level, item.Text));
                        bufferHeadingPath = HeadingPath(headingStack);
                        buffer.Add(item.Text);
                        MergePages(item.PageFrom, item.PageTo);
                        break;

                    case ItemKind.Paragraph:
                        if (buffer.Count == 0)
                        {
                            bufferHeadingPath = headingStack.Count > 0 ? HeadingPath(headingStack) : null;
                        }

                        buffer.Add(item.Text);
                        MergePages(item.PageFrom, item.PageTo);
                        break;

                    case ItemKind.Table:
                        Flush();
                        var currentPath = headingStack.Count > 0 ? HeadingPath(headingStack) : null;
                        foreach (var window in SerializeTableWindows(item.Grid, maxChars))
                        {
                            raw.Add(new RawChunk
                            {
                                Kind = DocumentChunk.TableKind,
                                Text = window,
                                HeadingPath = currentPath,
                                PageFrom = item.PageFrom,
                                PageTo = item.PageTo
                            });
                        }

                        break;
                }
            }

            Flush();
            return raw;
        }

        /// <summary>Merges an under-min chunk into its following neighbour - text chunks only,
        /// the merged chunk keeps the FIRST chunk's heading path (FR-6). Tables never merge.</summary>
        private static List<RawChunk> MergeShort(List<RawChunk> chunks, Int32 minChars)
        {
            if (chunks.Count == 0)
            {
                return chunks;
            }

            var merged = new List<RawChunk>();
            var current = chunks[0];
            for (var i = 1; i < chunks.Count; i++)
            {
                var next = chunks[i];
                var bothText = current.Kind == DocumentChunk.TextKind && next.Kind == DocumentChunk.TextKind;
                if (bothText && current.Text.Length < minChars)
                {
                    current = new RawChunk
                    {
                        Kind = DocumentChunk.TextKind,
                        Text = current.Text + "\n\n" + next.Text,
                        HeadingPath = current.HeadingPath,
                        PageFrom = Min(current.PageFrom, next.PageFrom),
                        PageTo = Max(current.PageTo, next.PageTo)
                    };
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        /// <summary>Splits over-max TEXT chunks at paragraph boundaries; an over-max single
        /// paragraph splits at the last whitespace before the bound (hard cut when none).
        /// Tables were already row-windowed at emission.</summary>
        private static List<RawChunk> SplitLong(List<RawChunk> chunks, Int32 maxChars)
        {
            var result = new List<RawChunk>();
            foreach (var chunk in chunks)
            {
                if (chunk.Kind != DocumentChunk.TextKind || chunk.Text.Length <= maxChars)
                {
                    result.Add(chunk);
                    continue;
                }

                var pieces = new List<String>();
                foreach (var paragraph in chunk.Text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (paragraph.Length <= maxChars)
                    {
                        pieces.Add(paragraph);
                        continue;
                    }

                    var rest = paragraph;
                    while (rest.Length > maxChars)
                    {
                        var cut = rest.LastIndexOfAny(new[] { ' ', '\n', '\t' }, maxChars - 1);
                        if (cut <= 0)
                        {
                            cut = maxChars;
                        }

                        pieces.Add(rest.Substring(0, cut).TrimEnd());
                        rest = rest.Substring(cut).TrimStart();
                    }

                    if (rest.Length > 0)
                    {
                        pieces.Add(rest);
                    }
                }

                var builder = new StringBuilder();
                foreach (var piece in pieces)
                {
                    if (builder.Length > 0 && builder.Length + piece.Length + 2 > maxChars)
                    {
                        result.Add(CopyWithText(chunk, builder.ToString()));
                        builder.Clear();
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append("\n\n");
                    }

                    builder.Append(piece);
                }

                if (builder.Length > 0)
                {
                    result.Add(CopyWithText(chunk, builder.ToString()));
                }
            }

            return result;
        }

        private static RawChunk CopyWithText(RawChunk source, String text)
        {
            return new RawChunk
            {
                Kind = source.Kind,
                Text = text,
                HeadingPath = source.HeadingPath,
                PageFrom = source.PageFrom,
                PageTo = source.PageTo
            };
        }

        private static String HeadingPath(List<(Int32 Level, String Title)> stack)
        {
            var titles = new List<String>(stack.Count);
            foreach (var entry in stack)
            {
                titles.Add(entry.Title);
            }

            return String.Join(" > ", titles);
        }

        private static Int32? Min(Int32? a, Int32? b) => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);

        private static Int32? Max(Int32? a, Int32? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);

        #endregion

        #region table serialization

        /// <summary>Serializes a grid to markdown pipe-table windows: every window repeats the
        /// header row, windows respect <paramref name="maxChars"/> with at least one body row
        /// each (FR-6). A header-only table is one window.</summary>
        private static List<String> SerializeTableWindows(List<List<String>> grid, Int32 maxChars)
        {
            var windows = new List<String>();
            if (grid == null || grid.Count == 0)
            {
                return windows;
            }

            var headerLine = RenderRow(grid[0]);
            var separator = RenderSeparator(grid[0].Count);
            var prefix = headerLine + "\n" + separator;

            if (grid.Count == 1)
            {
                windows.Add(prefix);
                return windows;
            }

            var builder = new StringBuilder(prefix);
            var bodyRowsInWindow = 0;
            for (var i = 1; i < grid.Count; i++)
            {
                var rowLine = RenderRow(grid[i]);
                if (bodyRowsInWindow > 0 && builder.Length + rowLine.Length + 1 > maxChars)
                {
                    windows.Add(builder.ToString());
                    builder = new StringBuilder(prefix);
                    bodyRowsInWindow = 0;
                }

                builder.Append('\n').Append(rowLine);
                bodyRowsInWindow++;
            }

            windows.Add(builder.ToString());
            return windows;
        }

        private static String RenderRow(List<String> cells)
        {
            return "| " + String.Join(" | ", cells) + " |";
        }

        private static String RenderSeparator(Int32 columnCount)
        {
            var builder = new StringBuilder("|");
            for (var i = 0; i < Math.Max(1, columnCount); i++)
            {
                builder.Append(" --- |");
            }

            return builder.ToString();
        }

        private static String EscapeTableCell(String text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return String.Empty;
            }

            return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        #endregion
    }
}
