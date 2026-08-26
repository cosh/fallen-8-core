// MIT License
//
// CsvTable.cs
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

namespace NoSQL.GraphDB.Integrations.Providers.CsvDeviceList
{
    /// <summary>
    ///   A delimited text file, already read, turned into a header-to-index map and rows of cells.
    ///
    ///   <para>Hand-written rather than a dependency because this runs in a container that holds other
    ///   people's credentials, to read a grammar whose whole content is quotes, doubled quotes and a
    ///   separator. It is PURE: no I/O, no provider knowledge, no clock, nothing about claims or the graph,
    ///   so a unit test drives it with a string and asserts on <see cref="Rows"/> and
    ///   <see cref="Columns"/>.</para>
    ///
    ///   <para>Its failure modes are RETURN VALUES rather than exceptions, because the provider is the one
    ///   that has to name them to an operator: only the provider knows the setting the file was named by,
    ///   and only it knows that the answer to a file it cannot read is failing the run rather than
    ///   reporting an empty list.</para>
    /// </summary>
    public sealed class CsvTable
    {
        /// <summary>
        ///   The quote character, fixed rather than configurable: it is half of the grammar this parser
        ///   implements, so a file that quotes with something else is a different format and not a setting.
        /// </summary>
        public const Char QuoteCharacter = '"';

        /// <summary>The byte-order mark a spreadsheet program writes ahead of the header row.</summary>
        private const Char ByteOrderMark = '\uFEFF';

        private readonly IReadOnlyDictionary<String, Int32> _columns;

        private CsvTable(IReadOnlyList<String> header, IReadOnlyDictionary<String, Int32> columns,
            IReadOnlyList<CsvRow> rows)
        {
            Header = header;
            _columns = columns;
            Rows = rows;
        }

        /// <summary>
        ///   The header cells in file order and as they were written, which is what a refusal names so the
        ///   operator can see what was read rather than only what was missing.
        /// </summary>
        public IReadOnlyList<String> Header { get; }

        /// <summary>
        ///   Every row after the header, blank lines dropped. A row is kept whatever its cell count: fewer
        ///   cells than the header is a fact about a hand-edited file, not a reason to lose the row.
        /// </summary>
        public IReadOnlyList<CsvRow> Rows { get; }

        /// <summary>
        ///   Column name to index, matched case-insensitively because a person types the header row and
        ///   <c>MAC</c>, <c>Mac</c> and <c>mac</c> are one column. A blank header cell is not registered,
        ///   since nothing could look it up, and a repeated name keeps its FIRST index so two identically
        ///   named columns read the same way on every run.
        /// </summary>
        public IReadOnlyDictionary<String, Int32> Columns => _columns;

        /// <summary>Looks a column up by name.</summary>
        public Boolean TryGetColumn(String name, out Int32 index)
        {
            return _columns.TryGetValue(name, out index);
        }

        /// <summary>
        ///   Parses a whole file. Returns false only for <see cref="CsvTableFailure.NoHeaderRow"/>: a file
        ///   with a header and no rows parses successfully and is an empty list, which is a legitimate
        ///   answer, while a file with no header row at all is one nothing can be read from.
        /// </summary>
        public static Boolean TryParse(String? text, Char delimiter, out CsvTable? table,
            out CsvTableFailure failure)
        {
            table = null;
            failure = CsvTableFailure.None;

            var lines = SplitLines(text);
            var headerLine = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!String.IsNullOrWhiteSpace(lines[i]))
                {
                    headerLine = i;
                    break;
                }
            }

            if (headerLine < 0)
            {
                failure = CsvTableFailure.NoHeaderRow;
                return false;
            }

            var header = SplitLine(lines[headerLine], delimiter, out _);
            var columns = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                var name = header[i].Trim();
                if (name.Length > 0 && !columns.ContainsKey(name))
                {
                    columns.Add(name, i);
                }
            }

            var rows = new List<CsvRow>();
            for (var i = headerLine + 1; i < lines.Count; i++)
            {
                if (String.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                var cells = SplitLine(lines[i], delimiter, out var unterminatedQuote);
                rows.Add(new CsvRow(i + 1, cells, unterminatedQuote));
            }

            table = new CsvTable(header, columns, rows);
            return true;
        }

        /// <summary>
        ///   Splits a file into physical lines, dropping a leading byte-order mark and a carriage return
        ///   before every line ending. A file whose only line ending is a bare carriage return is split on
        ///   that instead: it would otherwise arrive as ONE line, which parses as a header row with no rows
        ///   after it, and an empty complete snapshot withdraws every device this identity claimed.
        /// </summary>
        private static IReadOnlyList<String> SplitLines(String? text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return Array.Empty<String>();
            }

            var body = text[0] == ByteOrderMark ? text.Substring(1) : text;
            var lines = body.Split(body.IndexOf('\n') >= 0 ? '\n' : '\r');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd('\r');
            }

            return lines;
        }

        /// <summary>
        ///   Splits one physical line into cells: a quote at the start of a cell opens a quoted field, a
        ///   doubled quote inside one is a literal quote, and the delimiter separates cells everywhere else.
        ///
        ///   <para>A quoted field left open at the end of the line sets
        ///   <paramref name="unterminatedQuote"/> and the line is still returned AS IT LOOKS. A newline
        ///   inside a quoted field is unsupported, and this is the one shape in which that is visible: the
        ///   alternative, joining the next line into the field, silently mis-parses everything after it,
        ///   whereas the row as it looks plus a report of the fact leaves an operator something to fix.</para>
        ///
        ///   <para>An unquoted cell is trimmed, because <c>mac, name</c> is how a person writes a header
        ///   row; a quoted cell is verbatim, because quoting it is how the same person says the spaces are
        ///   part of the value. Everything else is tolerated rather than refused: a quote in the middle of
        ///   an unquoted cell, or text after a closing quote, is content, since refusing a row loses a
        ///   device over punctuation.</para>
        /// </summary>
        private static IReadOnlyList<String> SplitLine(String line, Char delimiter,
            out Boolean unterminatedQuote)
        {
            var cells = new List<String>();
            var cell = new StringBuilder(line.Length);
            var quotedField = false;
            var insideQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var character = line[i];
                if (insideQuotes)
                {
                    if (character != QuoteCharacter)
                    {
                        cell.Append(character);
                    }
                    else if (i + 1 < line.Length && line[i + 1] == QuoteCharacter)
                    {
                        cell.Append(QuoteCharacter);
                        i++;
                    }
                    else
                    {
                        insideQuotes = false;
                    }
                }
                else if (character == delimiter)
                {
                    cells.Add(Close(cell, quotedField));
                    cell.Clear();
                    quotedField = false;
                }
                else if (character == QuoteCharacter && cell.Length == 0 && !quotedField)
                {
                    insideQuotes = true;
                    quotedField = true;
                }
                else
                {
                    cell.Append(character);
                }
            }

            unterminatedQuote = insideQuotes;
            cells.Add(Close(cell, quotedField));
            return cells;
        }

        /// <summary>Finishes one cell: verbatim when it was quoted, trimmed when it was not.</summary>
        private static String Close(StringBuilder cell, Boolean quotedField)
        {
            var text = cell.ToString();
            return quotedField ? text : text.Trim();
        }
    }

    /// <summary>
    ///   One row of a <see cref="CsvTable"/>. It carries its own line number because every diagnostic
    ///   about a row has to name it: "row 7" is something an operator can open the file and look at, and a
    ///   row index counted from the header is not.
    /// </summary>
    public sealed class CsvRow
    {
        /// <param name="lineNumber">The 1-based physical line the row was read from.</param>
        /// <param name="cells">The row's cells, in file order.</param>
        /// <param name="unterminatedQuote">Whether a quoted field was still open at the end of the line,
        /// which means a quoted field contains a newline and was read as the row it looks like.</param>
        public CsvRow(Int32 lineNumber, IReadOnlyList<String> cells, Boolean unterminatedQuote)
        {
            LineNumber = lineNumber;
            Cells = cells;
            UnterminatedQuote = unterminatedQuote;
        }

        /// <summary>The 1-based physical line this row was read from.</summary>
        public Int32 LineNumber { get; }

        /// <summary>The cells, in file order. There may be fewer than the header has, or more.</summary>
        public IReadOnlyList<String> Cells { get; }

        /// <summary>
        ///   Whether a quoted field was still open when the line ended, which is the one visible symptom
        ///   of a newline inside a quoted field. Unsupported, and the reader says so rather than joining
        ///   the lines and mis-parsing the rest of the file.
        /// </summary>
        public Boolean UnterminatedQuote { get; }

        /// <summary>
        ///   The cell at a column index, or null. Null covers a column this row does not reach, a column
        ///   the header does not have (index below zero), and a blank cell, which is absent under the
        ///   presence rule the snapshot contract's <c>EntityDto</c> owns.
        /// </summary>
        public String? Cell(Int32 columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= Cells.Count)
            {
                return null;
            }

            var text = Cells[columnIndex];
            return String.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    /// <summary>
    ///   Why a file could not be turned into a table. One member, because one is all the grammar has: a
    ///   file with no header row is unreadable, and every other shape a hand-edited file arrives in is
    ///   readable with something to report about a row.
    /// </summary>
    public enum CsvTableFailure
    {
        /// <summary>Parsed.</summary>
        None = 0,

        /// <summary>
        ///   The file has no non-blank line, so there is no header row and therefore no column to read a
        ///   MAC address out of. The provider fails the run on this rather than reporting an empty list.
        /// </summary>
        NoHeaderRow = 1,
    }
}
