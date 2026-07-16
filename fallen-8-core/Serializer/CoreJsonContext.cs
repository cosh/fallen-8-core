// MIT License
//
// CoreJsonContext.cs
//
// Copyright (c) 2025 Henning Rauch
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

using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.SubGraph;

namespace NoSQL.GraphDB.Core.Serializer
{
    /// <summary>
    /// System.Text.Json source-generation context for the engine's internal JSON payloads:
    /// the persistable subgraph <see cref="SubGraphRecipe"/> and the delegate
    /// <see cref="DelegateDescriptor"/>. Using generated metadata removes the reflection-based
    /// serialization on these paths.
    /// </summary>
    /// <remarks>
    /// The context intentionally declares no <c>JsonSourceGenerationOptions</c>, so it carries the
    /// default options (no indentation, no naming policy, nulls written). Both payload types name
    /// every property explicitly with <c>[JsonPropertyName]</c>, so the property names are fixed
    /// regardless of naming policy. Call sites that require non-default options (for example
    /// <c>DelegateJson</c>, which writes indented JSON and ignores nulls) attach this context as a
    /// <c>TypeInfoResolver</c> to their own pre-existing <c>JsonSerializerOptions</c> rather than
    /// using the generated <c>JsonTypeInfo</c> directly, which keeps the emitted JSON byte-for-byte
    /// identical to the previous reflection-based output.
    /// </remarks>
    [JsonSerializable(typeof(SubGraphRecipe))]
    [JsonSerializable(typeof(SubGraphRecipeManifest))]
    [JsonSerializable(typeof(StoredQueries.StoredQueryDefinition))]
    [JsonSerializable(typeof(StoredQueries.StoredQueryManifest))]
    [JsonSerializable(typeof(DelegateDescriptor))]
    internal sealed partial class CoreJsonContext : JsonSerializerContext
    {
    }
}
