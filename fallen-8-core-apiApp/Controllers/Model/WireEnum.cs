// MIT License
//
// WireEnum.cs
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
using System.Text.Json;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The one wire spelling for a published enum: the camelCase form of the member name
    ///   (<c>NotWritable</c> becomes <c>notWritable</c>). This application installs no string-enum
    ///   converter, so a bare enum would serialize as an integer whose meaning lives only in this
    ///   assembly; DTOs publish strings through this helper instead of each carrying its own switch.
    ///   The exact spellings are pinned by the endpoint tests and the JSON parity samples.
    /// </summary>
    internal static class WireEnum
    {
        internal static String Camel<T>(T value) where T : struct, Enum
        {
            return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
        }
    }
}
