// MIT License
//
// McpIdentityOptions.cs
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
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Mcp.Configuration
{
    /// <summary>
    ///   This server's tenant and instance identity, bound from <c>Mcp:Identity</c>. The shape, the
    ///   defaults and the resource attributes are <see cref="AFleetIdentityOptions" />'; the MCP
    ///   fronts exactly one target Fallen-8, so set <c>Mcp:Identity:Instance:Id</c> to the apiApp
    ///   instance id it bridges and the fleet dashboards resolve the MCP panels under the same
    ///   instance.
    /// </summary>
    public sealed class McpIdentityOptions : AFleetIdentityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Mcp:Identity";

        /// <summary>Prefixes an auto-filled instance id with <c>f8-mcp-</c>, so a generated id says
        /// which deployable minted it.</summary>
        public McpIdentityOptions()
            : base("f8-mcp-")
        {
        }
    }
}
