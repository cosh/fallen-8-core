// MIT License
//
// ChatModelsREST.cs
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
using System.Linq;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.App.Chat;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   What the instance's configured chat backend catalogues (feature chat-model-catalog): the
    ///   running backend and its model names, so a client can offer real names instead of a blank
    ///   field. It is a NEUTRAL read: every catalogued model is returned with whatever the backend
    ///   said about it, and filtering (a chat picker hides embedding models) belongs to the client.
    /// </summary>
    public sealed class ChatModelsREST
    {
        /// <summary>Maps a catalog read to the response. The backend name is passed in rather than
        /// re-derived here, so the spelling stays the chat provider's single answer.</summary>
        public static ChatModelsREST From(String backend, IEnumerable<ChatCatalogModel> models)
        {
            return new ChatModelsREST
            {
                Backend = backend,
                Models = (models ?? Enumerable.Empty<ChatCatalogModel>())
                    .Select(model => new ChatModelREST
                    {
                        Name = model.Name,
                        Capability = model.Capability,
                        Available = model.Available,
                        ModelClass = model.ModelClass
                    })
                    .ToList()
            };
        }

        /// <summary>
        ///   The RUNNING backend, spelled as the selector spells it (the same value
        ///   <c>ChatResultREST.Backend</c> carries). A pending-restart backend switch is not
        ///   previewed: until the restart this still answers for the backend a completion would
        ///   reach.
        /// </summary>
        /// <example>Nahil</example>
        [JsonPropertyName("backend")]
        public String Backend
        {
            get; set;
        }

        /// <summary>The catalogued models, sorted ordinally by name. Empty when the backend
        /// catalogues nothing; never null.</summary>
        [JsonPropertyName("models")]
        public List<ChatModelREST> Models
        {
            get; set;
        }
    }

    /// <summary>ONE catalogued model. Everything but the name is optional, because no backend
    /// publishes all of it.</summary>
    public sealed class ChatModelREST
    {
        /// <summary>The name VERBATIM, as the backend spells it (tag included): the value that goes
        /// back into <c>Fallen8:Chat:&lt;Backend&gt;:Model</c>.</summary>
        /// <example>phi4-f8-mini:latest</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary><c>completion</c>, <c>embedding</c>, or null when the backend does not say
        /// (OpenAI and Anthropic never do, an older Ollama sidecar omits it).</summary>
        /// <example>completion</example>
        [JsonPropertyName("capability")]
        public String Capability
        {
            get; set;
        }

        /// <summary>Whether the backend can serve this model right now: Nahil's per-model
        /// routability, true for a local sidecar's models (they are on disk), null when the backend
        /// reports nothing on the subject.</summary>
        /// <example>true</example>
        [JsonPropertyName("available")]
        public Boolean? Available
        {
            get; set;
        }

        /// <summary>Nahil's model class verbatim, null elsewhere. It carries no published legend
        /// (observed: S1/S2 on completion models, C1/C2 on embedding ones), so treat it as a label
        /// rather than a contract.</summary>
        /// <example>S1</example>
        [JsonPropertyName("class")]
        public String ModelClass
        {
            get; set;
        }
    }
}
