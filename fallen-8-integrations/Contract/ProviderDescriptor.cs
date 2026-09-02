// MIT License
//
// ProviderDescriptor.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   What a provider IS, as data that is true before any run exists. Settings-as-data is what makes
    ///   "adding a provider requires zero Studio code change" true rather than aspirational: a form is
    ///   rendered from <see cref="ProviderSetting.Kind"/>, <see cref="ProviderSetting.Required"/> and
    ///   <see cref="ProviderSetting.Help"/>, so a provider needing its own React component is a contract
    ///   failure and not a UI task - and an agent authoring the fourth integration cannot write one anyway.
    ///
    ///   <para>The descriptor must NOT carry an interval or any other timing (nothing to bind to: timing
    ///   is not this runtime's subject), a summary template expressed as code rather than declaratively
    ///   (it would put provider-authored code on the path that produces embedding text), or a strength
    ///   for its own claim types (a provider able to call its own weak identifier strong makes an address
    ///   resolve, and the run then attaches its data to whichever element last held that address).</para>
    /// </summary>
    public sealed class ProviderDescriptor
    {
        /// <summary>
        ///   The stable provider id. It appears inside every provider-scoped claim key, so it is
        ///   assigned once and never reused: changing it renames every identity that provider ever
        ///   asserted.
        /// </summary>
        [JsonPropertyName("id")]
        public String Id { get; set; } = String.Empty;

        /// <summary>What a person sees in a list.</summary>
        [JsonPropertyName("displayName")]
        public String DisplayName { get; set; } = String.Empty;

        /// <summary>What it reads and from where, in a sentence a user can act on.</summary>
        [JsonPropertyName("description")]
        public String Description { get; set; } = String.Empty;

        /// <summary>
        ///   Optional: where the deep dive on this provider lives, as an ABSOLUTE http or https URL. It
        ///   is what keeps <see cref="Description"/> a sentence: everything a reader needs beyond that
        ///   sentence is one page away instead of crowded into a table cell.
        ///
        ///   <para>It is data on the descriptor rather than a map inside Studio for the same reason the
        ///   settings are: a provider whose docs live on the author's own site brings its own link, and
        ///   the screen needs no per-provider code. The catalog refuses anything but an absolute http or
        ///   https URL at STARTUP, because Studio renders this as a link and a relative or
        ///   <c>javascript:</c> value is either dead or dangerous there.</para>
        /// </summary>
        [JsonPropertyName("docsUrl")]
        public String? DocsUrl { get; set; }

        /// <summary>The settings, as data.</summary>
        [JsonPropertyName("settings")]
        public IReadOnlyList<ProviderSetting> Settings { get; set; } = Array.Empty<ProviderSetting>();

        /// <summary>The kinds it produces, which become element labels.</summary>
        [JsonPropertyName("entityKinds")]
        public IReadOnlyList<String> EntityKinds { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   The identifier types it claims, checked against the vocabulary when the catalog is built:
        ///   a wrongly scoped or unknown identifier then stops the process from starting rather than
        ///   costing one duplicate per run until somebody reads a diagnostic.
        /// </summary>
        [JsonPropertyName("claimTypes")]
        public IReadOnlyList<String> ClaimTypes { get; set; } = Array.Empty<String>();

        /// <summary>The relations it emits, which become edge types.</summary>
        [JsonPropertyName("relationTypes")]
        public IReadOnlyList<String> RelationTypes { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   Whether a run can see the source's whole state. A provider declaring <c>false</c> here and
        ///   then returning a snapshot marked complete is refused rather than trusted.
        /// </summary>
        [JsonPropertyName("canObserveCompleteState")]
        public Boolean CanObserveCompleteState { get; set; }

        /// <summary>Whether it only ever reads from the source.</summary>
        [JsonPropertyName("readOnly")]
        public Boolean ReadOnly { get; set; }

        /// <summary>
        ///   Optional: the text to embed for one entity, as a DECLARATIVE template over the entity's own kind and
        ///   properties, e.g. <c>{kind} {unifi.name}, {unifi.model}</c>.
        ///
        ///   <para>Declaring one is the PROVIDER'S half of the embedding opt-in; a job declares the other half,
        ///   and both are needed because embedding every client on a busy network by default is cost and noise in
        ///   equal measure. It is a template rather than a method because a template expressed as code would put
        ///   provider-authored code on the path that produces embedding text.</para>
        ///
        ///   <para>NO LITERAL WORD MAY SIT BESIDE A HOLE, only punctuation. One template serves every kind a
        ///   provider emits and the kinds fill different holes, so a hole an entity cannot fill collapses along
        ///   with the punctuation around it - but rendering cannot remove a WORD, and <c>state {unifi.state}</c>
        ///   then ends the summary of every kind that has no state with a dangling "state", embedding the shape of
        ///   the template instead of the description of the thing.</para>
        /// </summary>
        [JsonPropertyName("entitySummaryTemplate")]
        public String? EntitySummaryTemplate { get; set; }
    }

    /// <summary>
    ///   One setting, as data. <see cref="Help"/> says where to find the value in the source system,
    ///   which is the difference between a setting a user fills in and one they give up on.
    /// </summary>
    public sealed class ProviderSetting
    {
        /// <summary>The key a job's <c>settings</c> (or <c>credentials</c>) map uses.</summary>
        [JsonPropertyName("key")]
        public String Key { get; set; } = String.Empty;

        /// <summary>The field label a person reads.</summary>
        [JsonPropertyName("label")]
        public String Label { get; set; } = String.Empty;

        /// <summary>What kind of value it is, which is all the form needs to render it.</summary>
        [JsonPropertyName("kind")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SettingKind Kind { get; set; } = SettingKind.Text;

        /// <summary>Whether a run cannot start without it.</summary>
        [JsonPropertyName("required")]
        public Boolean Required { get; set; }

        /// <summary>Where to find the value in the source system.</summary>
        [JsonPropertyName("help")]
        public String Help { get; set; } = String.Empty;

        /// <summary>The value used when a job names none. NEVER a credential and never a file.</summary>
        [JsonPropertyName("defaultValue")]
        public String? DefaultValue { get; set; }

        /// <summary>
        ///   For a <see cref="SettingKind.File"/> setting only: the extensions a file picker should offer,
        ///   as the HTML <c>accept</c> attribute spells them (<c>.csv,.tsv,.txt</c>). It is a HINT and
        ///   nothing more - a browser ignores it for a dropped file, and the runtime never checks it - so
        ///   the wrong file still reaches the provider and fails the run with the provider's own message.
        ///   It exists because the alternative is a form that offers every file on the machine, and
        ///   deriving the extension from <see cref="Help"/> prose would be a per-provider special case
        ///   wearing a disguise.
        /// </summary>
        [JsonPropertyName("accept")]
        public String? Accept { get; set; }

        /// <summary>
        ///   For a <see cref="SettingKind.File"/> setting only: whether the setting takes SEVERAL files
        ///   rather than one.
        ///
        ///   <para>It is a statement about the SOURCE, not a convenience. A vehicle network is handed over
        ///   as one AUTOSAR extract per domain or per bus, and those extracts reference each other by path,
        ///   so the source is the whole set and no single file is a complete description of it. That matters
        ///   because completeness licenses withdrawal: a provider declaring
        ///   <see cref="ProviderDescriptor.CanObserveCompleteState"/> and given one file of a set would
        ///   report a complete snapshot missing everything the other files describe, and reconciliation would
        ///   delete it. Declaring the setting multiple is how a provider says "the files I was given, taken
        ///   together, are the source".</para>
        ///
        ///   <para>Declaring it on any other kind is a descriptor error the catalog refuses at startup: the
        ///   other kinds are scalars a form types, and there is no wire shape for several of them.</para>
        /// </summary>
        [JsonPropertyName("multiple")]
        public Boolean Multiple { get; set; }
    }

    /// <summary>What kind of value a setting takes. The whole vocabulary a settings form needs.</summary>
    public enum SettingKind
    {
        /// <summary>Free text.</summary>
        Text = 0,

        /// <summary>A number.</summary>
        Number = 1,

        /// <summary>A yes or no.</summary>
        Boolean = 2,

        /// <summary>A URL.</summary>
        Url = 3,

        /// <summary>
        ///   A credential. Its value arrives in the job's <c>credentialValues</c> map and NEVER in
        ///   <c>settings</c>: a setting is neither leased nor redacted, so a value there would be logged and
        ///   reported like any other. A form renders this as a password field whose help text says the value
        ///   is used for the run and then forgotten, and a provider reads it from the lease rather than from
        ///   its settings.
        /// </summary>
        Credential = 4,

        /// <summary>
        ///   A FILE THE CALLER SUPPLIES. Its bytes arrive WITH THE JOB and NEVER in <c>settings</c>: the
        ///   runtime opens nothing on disk, so a bare name in <c>settings</c> would name a file nothing can
        ///   read. How they arrive is the transport's business and not a provider's - as raw bytes in their
        ///   own multipart part - and what reaches a provider is the bytes and the name. A form renders this
        ///   as a
        ///   dropzone and a file picker (see <see cref="ProviderSetting.Accept"/>), which is the whole
        ///   reason the kind exists: asking a person to copy a file into a container mount and then type
        ///   its name is not a form anyone can fill in.
        ///
        ///   <para>A file lives exactly as long as the run that needed it, like a credential - but unlike
        ///   a credential it is graph DATA, so it is never redacted out of a log line or a report. Its
        ///   NAME becomes the setting's effective value, so a provider reads it with
        ///   <c>context.Required(key)</c> for its messages and its bytes with
        ///   <c>context.ReadFileAsync(key, ...)</c>, exactly as it did when the file came off a mount. A
        ///   File setting never carries a <see cref="ProviderSetting.DefaultValue"/>: there is no file to
        ///   default to.</para>
        /// </summary>
        File = 5,
    }
}
