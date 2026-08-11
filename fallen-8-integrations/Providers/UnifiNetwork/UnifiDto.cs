// MIT License
//
// UnifiDto.cs
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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Providers.UnifiNetwork
{
    /// <summary>
    ///   The vendor's paging envelope, declared with the two fields that decide whether a list was
    ///   followed to its end and no others.
    ///
    ///   <para>Provenance: the vendor's OpenAPI 3.1.0 document
    ///   (https://developer.ui.com/network/v10.4.57/openapi.json) declares <c>count</c>, <c>offset</c>,
    ///   <c>limit</c>, <c>totalCount</c> and <c>data</c>, all required.</para>
    ///
    ///   <para>Three of those five are deliberately NOT read. Advancing by anything the envelope SAYS,
    ///   rather than by the items actually in hand, is the paging bug that deletes data: this provider
    ///   declares a complete snapshot, so every absence is a withdrawal and, on the last claim, a
    ///   deletion. The loop therefore advances by the length of <see cref="Data"/> and stops on an empty
    ///   page, and <see cref="TotalCount"/> is kept only to refuse a run that came up short.</para>
    /// </summary>
    /// <typeparam name="TItem">The resource the page carries.</typeparam>
    public sealed class UnifiPage<TItem>
    {
        /// <summary>
        ///   How many items the console says match, which is a PROMISE and never an instruction: it is
        ///   never used to stop the loop, only to refuse a run that ended below it.
        /// </summary>
        [JsonPropertyName("totalCount")]
        public Int32? TotalCount { get; set; }

        /// <summary>The items on this page. Its length, and nothing else, advances the offset.</summary>
        [JsonPropertyName("data")]
        public IList<TItem>? Data { get; set; }
    }

    /// <summary>
    ///   One site of the console. Provenance: the OpenAPI document requires <c>id</c>, <c>name</c> and
    ///   <c>internalReference</c> on a site (spec section 14; the site schema itself was not readable
    ///   through the fetch, so these three names come from the spec's own recorded reading of the whole
    ///   document).
    ///
    ///   <para><c>internalReference</c> is deliberately not read: it is the console's legacy short name,
    ///   nothing in the entity model needs it, and every property landed is one more value whose absence
    ///   overwrites what another integration knows.</para>
    /// </summary>
    public sealed class UnifiSite
    {
        /// <summary>The site UUID, which is this entity's strong, provider-scoped identity.</summary>
        [JsonPropertyName("id")]
        public String? Id { get; set; }

        /// <summary>The site's display name.</summary>
        [JsonPropertyName("name")]
        public String? Name { get; set; }
    }

    /// <summary>
    ///   One adopted device as the LIST serves it. Provenance: schema "Adopted device overview" in the
    ///   OpenAPI document, read directly: <c>features</c>, <c>firmwareUpdatable</c>,
    ///   <c>firmwareVersion</c>, <c>id</c>, <c>interfaces</c>, <c>ipAddress</c>, <c>macAddress</c>,
    ///   <c>model</c>, <c>name</c>, <c>state</c>, <c>supported</c>, with <c>firmwareVersion</c> the one
    ///   field not required.
    ///
    ///   <para><c>firmwareUpdatable</c> and <c>supported</c> are not read: they describe what the console
    ///   could do to the device rather than what the device is.</para>
    /// </summary>
    public sealed class UnifiDevice
    {
        /// <summary>The device UUID: strong, provider-scoped identity.</summary>
        [JsonPropertyName("id")]
        public String? Id { get; set; }

        /// <summary>The device's display name.</summary>
        [JsonPropertyName("name")]
        public String? Name { get; set; }

        /// <summary>The model code the console reports.</summary>
        [JsonPropertyName("model")]
        public String? Model { get; set; }

        /// <summary>The device MAC, the workhorse strong claim that overlaps with every other source.</summary>
        [JsonPropertyName("macAddress")]
        public String? MacAddress { get; set; }

        /// <summary>The management address: a lease, so a weak claim and never an identity.</summary>
        [JsonPropertyName("ipAddress")]
        public String? IpAddress { get; set; }

        /// <summary>
        ///   The device state, read as the SOURCE'S OWN STRING and never mapped. The document declares ten
        ///   values (ONLINE, OFFLINE, PENDING_ADOPTION, UPDATING, GETTING_READY, ADOPTING, DELETING,
        ///   CONNECTION_INTERRUPTED, ISOLATED, U5G_INCORRECT_TOPOLOGY); ISOLATED is not OFFLINE, a mapping
        ///   table would answer a question the vendor already answers, and a typed enum would throw on an
        ///   eleventh value and lose the whole run.
        /// </summary>
        [JsonPropertyName("state")]
        public String? State { get; set; }

        /// <summary>The firmware version, the one device field the document does not require.</summary>
        [JsonPropertyName("firmwareVersion")]
        public String? FirmwareVersion { get; set; }

        /// <summary>
        ///   The device's feature set, read as raw JSON rather than as a typed pair. The document declares
        ///   an object whose members are <c>accessPoint</c> and <c>switching</c>, each a feature-overview
        ///   object present when the device has that feature, so the SET OF MEMBER NAMES is the fact worth
        ///   landing - and reading the object raw means a third feature in a later version lands too
        ///   instead of being invisible until somebody edits a DTO.
        /// </summary>
        [JsonPropertyName("features")]
        public JsonElement? Features { get; set; }

        /// <summary>
        ///   The device's physical interfaces, read as raw JSON for the same reason. The document declares
        ///   an object with the arrays <c>ports</c> (of "Port overview") and <c>radios</c> (of "Wireless
        ///   radio overview"), so which of the two the device actually presents is shape worth landing.
        ///   Nothing from INSIDE a port or a radio lands: those item schemas were not readable through the
        ///   fetch, and port state changes between any two runs, which would make every run a write and the
        ///   zero-mutation invariant unobservable for this provider.
        /// </summary>
        [JsonPropertyName("interfaces")]
        public JsonElement? Interfaces { get; set; }
    }

    /// <summary>
    ///   One adopted device as the DETAILS resource serves it, declaring the one field the list does not
    ///   carry. Provenance: schema "Adopted device details" in the OpenAPI document, read directly; it
    ///   repeats every field of the overview and adds <c>adoptedAt</c>, <c>configurationId</c>,
    ///   <c>provisionedAt</c> and <c>uplink</c>.
    ///
    ///   <para>Only <c>uplink</c> is read. The repeated fields are taken from the list item, because two
    ///   readers for one property is two things to keep in step for no gain, and the timestamps are
    ///   history rather than shape.</para>
    /// </summary>
    public sealed class UnifiDeviceDetails
    {
        /// <summary>Where this device hangs off the topology. Absent for a device with no uplink.</summary>
        [JsonPropertyName("uplink")]
        public UnifiUplink? Uplink { get; set; }
    }

    /// <summary>
    ///   The device's uplink, which is a NESTED object and not a flat <c>uplinkDeviceId</c>: it is
    ///   precisely this shape, on precisely this resource, that costs one extra request per device.
    ///   Provenance: schema "Device uplink interface overview" in the OpenAPI document, read directly.
    /// </summary>
    public sealed class UnifiUplink
    {
        /// <summary>The UUID of the device this one uplinks to, addressed by claim when it becomes an edge.</summary>
        [JsonPropertyName("deviceId")]
        public String? DeviceId { get; set; }
    }

    /// <summary>
    ///   One connected client, read with ONE FLAT TYPE across all four of the vendor's variants.
    ///
    ///   <para>The document models clients as a discriminated union on <c>type</c> (WIRED, WIRELESS, VPN,
    ///   TELEPORT), and a typed union here would throw on an unmapped discriminator, which loses the whole
    ///   run to a firmware release. Flat, a fifth client type finds two fields missing instead: that is
    ///   the whole reason for the shape.</para>
    ///
    ///   <para>Provenance: the base "Client details" schema was read directly (<c>access</c>,
    ///   <c>connectedAt</c>, <c>id</c>, <c>ipAddress</c>, <c>name</c>, <c>type</c>; required
    ///   <c>access</c>, <c>id</c>, <c>name</c>, <c>type</c>). The variant schemas were NOT readable
    ///   through the fetch, so <c>macAddress</c> and <c>uplinkDeviceId</c>, required on WIRED and WIRELESS
    ///   only, come from the spec's own recorded reading of the whole document (section 14).</para>
    ///
    ///   <para><c>access</c> and <c>connectedAt</c> are deliberately not read: <c>access</c> is a nested
    ///   object whose shape was not confirmable, and a connection instant changes on every reconnect, so
    ///   landing it would make every run a write over a source that never changed.</para>
    /// </summary>
    public sealed class UnifiConnectedClient
    {
        /// <summary>The client UUID: strong, provider-scoped identity. Required by the document.</summary>
        [JsonPropertyName("id")]
        public String? Id { get; set; }

        /// <summary>The client's name as the console shows it.</summary>
        [JsonPropertyName("name")]
        public String? Name { get; set; }

        /// <summary>The union's discriminator, landed as the source's own word rather than interpreted.</summary>
        [JsonPropertyName("type")]
        public String? Type { get; set; }

        /// <summary>The client's address: weak, and often the only overlap with another source.</summary>
        [JsonPropertyName("ipAddress")]
        public String? IpAddress { get; set; }

        /// <summary>
        ///   The client's MAC. Absent on a VPN or Teleport connection, and its absence TOGETHER WITH an
        ///   absent uplink device is how those are recognised without asking the discriminator.
        /// </summary>
        [JsonPropertyName("macAddress")]
        public String? MacAddress { get; set; }

        /// <summary>
        ///   The device this client is connected through, flat on the LIST item, which is why a client
        ///   costs no second request where a device does.
        /// </summary>
        [JsonPropertyName("uplinkDeviceId")]
        public String? UplinkDeviceId { get; set; }
    }
}
