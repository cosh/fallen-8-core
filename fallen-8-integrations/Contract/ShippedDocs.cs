// MIT License
//
// ShippedDocs.cs
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

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   Where the documentation for the providers THIS deployable ships lives, for their
    ///   <see cref="ProviderDescriptor.DocsUrl"/>. One home for the published origin, so four
    ///   descriptors carry a link each rather than four copies of the URL.
    ///
    ///   <para>Internal on purpose: a provider written elsewhere names its own documentation and has
    ///   no business pointing at this one.</para>
    /// </summary>
    internal static class ShippedDocs
    {
        /// <summary>The integrations page, which is the deep dive for every shipped provider.</summary>
        internal const String IntegrationsPage = "https://docs.fallen-8.com/integrations/";

        /// <summary>A heading on that page, for a provider whose own section is worth linking directly.</summary>
        internal static String IntegrationsSection(String anchor)
            => IntegrationsPage + "#" + anchor;
    }
}
