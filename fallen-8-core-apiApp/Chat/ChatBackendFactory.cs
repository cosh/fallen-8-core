// MIT License
//
// ChatBackendFactory.cs
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
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   Maps <c>Fallen8:Chat:Backend</c> to an <see cref="IChatBackend" /> (feature
    ///   instance-config). Only <c>Ollama</c> is supported in v1; a remote OpenAI-compatible
    ///   backend is the documented extension point (one more case here). Called lazily on first
    ///   use only, so nothing is constructed while the capability is off.
    /// </summary>
    internal static class ChatBackendFactory
    {
        internal static IChatBackend Create(Fallen8ChatOptions options)
        {
            switch (options.Backend)
            {
                case "Ollama":
                    return new OllamaChatBackend(options.Ollama);

                default:
                    throw new InvalidOperationException(String.Format(
                        "'{0}' is not a supported chat backend. Expected Ollama.", options.Backend));
            }
        }
    }
}
