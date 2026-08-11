// MIT License
//
// FroniusDto.cs
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
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Providers.FroniusSolar
{
    /// <summary>
    ///   The reply of <c>GetAPIVersion.cgi</c>, and the ONE reply of the three with no
    ///   <c>Head</c>/<c>Body</c> envelope, so there is no status code to check on it.
    ///
    ///   <para><see cref="BaseUrl"/> is why this request exists at all: the root every other request
    ///   hangs off is ASKED rather than configured, because v0 versus v1 is a property of the device and
    ///   a configured guess would be one more setting a user cannot know the answer to.</para>
    /// </summary>
    public sealed class FroniusApiVersionDto
    {
        /// <summary>The API version the device implements, e.g. 1.</summary>
        [JsonPropertyName("APIVersion")]
        public Int32? ApiVersion { get; set; }

        /// <summary>The device's own resource root, e.g. <c>/solar_api/v1/</c>.</summary>
        [JsonPropertyName("BaseURL")]
        public String? BaseUrl { get; set; }

        /// <summary>The version range the device says it stays compatible with. Logged, not recorded:
        /// nothing in the graph is a statement about the vendor's compatibility promise.</summary>
        [JsonPropertyName("CompatibilityRange")]
        public String? CompatibilityRange { get; set; }
    }

    /// <summary>
    ///   The envelope every other Solar API reply arrives in. It is the reason a provider written from a
    ///   summary of this API is wrong: FAILURE ARRIVES WITH HTTP 200, and only
    ///   <see cref="FroniusStatusDto.Code"/> 0 means <see cref="Body"/> is data.
    /// </summary>
    /// <typeparam name="TBody">The body shape of the resource being read.</typeparam>
    public sealed class FroniusEnvelope<TBody>
    {
        /// <summary>Where the device says whether the body is data at all.</summary>
        [JsonPropertyName("Head")]
        public FroniusHeadDto? Head { get; set; }

        /// <summary>The payload, meaningful only when the head says code 0.</summary>
        [JsonPropertyName("Body")]
        public TBody? Body { get; set; }
    }

    /// <summary>
    ///   The head of a Solar API reply. Deliberately only <see cref="Status"/> is read: the head also
    ///   carries a timestamp and the request arguments, and this provider records no timing at all.
    /// </summary>
    public sealed class FroniusHeadDto
    {
        /// <summary>Whether the body is data, in the device's own terms.</summary>
        [JsonPropertyName("Status")]
        public FroniusStatusDto? Status { get; set; }
    }

    /// <summary>The device's verdict on its own reply.</summary>
    public sealed class FroniusStatusDto
    {
        /// <summary>The status code, translated through <see cref="FroniusStatusCodes"/> so a failure
        /// says <c>DeviceNotAvailable</c> rather than "12".</summary>
        [JsonPropertyName("Code")]
        public Int32? Code { get; set; }

        /// <summary>The device's own reason text, when it sends one.</summary>
        [JsonPropertyName("Reason")]
        public String? Reason { get; set; }

        /// <summary>The device's own message for a person, when it sends one.</summary>
        [JsonPropertyName("UserMessage")]
        public String? UserMessage { get; set; }
    }

    /// <summary>
    ///   The body of <c>GetInverterInfo.cgi</c>: <see cref="Data"/> is a MAP keyed by the device id the
    ///   logging device assigned, not a list.
    ///
    ///   <para>Recorded here because whoever adds readings will meet it and this provider never does: in
    ///   <c>Scope=System</c> the realtime resources nest their readings under <c>Values</c> on older
    ///   platforms and under <c>Value</c>, singular, on a GEN24. This provider issues no realtime request,
    ///   so that divergence has no site here yet - the next body DTO added to this file is where it lands.</para>
    /// </summary>
    public sealed class FroniusInverterInfoBodyDto
    {
        /// <summary>Every inverter the device has seen in the last 24 hours, keyed by its device id.</summary>
        [JsonPropertyName("Data")]
        public Dictionary<String, FroniusInverterDto>? Data { get; set; }
    }

    /// <summary>
    ///   One inverter as <c>GetInverterInfo.cgi</c> describes it. Three fields are read as RAW elements
    ///   rather than as the types the vendor's document declares, each for a reason that has been observed
    ///   in the field: the document's declared type is not what every platform sends, and a typed read
    ///   throws there and loses the whole run.
    /// </summary>
    public sealed class FroniusInverterDto
    {
        /// <summary>
        ///   The inverter's own id, which is this provider's only durable identity. Declared a string by
        ///   the document and read as a raw element so a device answering with a bare number is still
        ///   read: losing the run over the one field the identity depends on is the worst outcome
        ///   available, and a number's raw text is exactly the digits the device sent.
        /// </summary>
        [JsonPropertyName("UniqueID")]
        public JsonElement? UniqueId { get; set; }

        /// <summary>
        ///   The name somebody gave the inverter. It arrives as HTML ENTITIES on a Datamanager or a Symo
        ///   Hybrid and as plain text on a GEN24, so it is decoded before it is recorded.
        /// </summary>
        [JsonPropertyName("CustomName")]
        public String? CustomName { get; set; }

        /// <summary>
        ///   The device type NUMBER, recorded as a number and never mapped to a model name: the
        ///   document's table has more than 250 entries, exists only in a PDF, and is wrong anyway on the
        ///   newest platforms, which the document says always report device type 1.
        /// </summary>
        [JsonPropertyName("DT")]
        public Int32? DeviceType { get; set; }

        /// <summary>The current error number, where -1 is ABSENCE per the document rather than an error
        /// numbered minus one (<see cref="FroniusInverterStatus.AbsentErrorCode"/>).</summary>
        [JsonPropertyName("ErrorCode")]
        public Int32? ErrorCode { get; set; }

        /// <summary>
        ///   The PV peak power connected to this inverter, in watts. It is the CONFIGURED nameplate
        ///   figure and not a reading, which is why recording it does not make every run a write; no
        ///   power, current, voltage or energy counter is read anywhere in this provider.
        /// </summary>
        [JsonPropertyName("PVPower")]
        public Double? PvPower { get; set; }

        /// <summary>
        ///   Whether the device asks to be shown in visualizations. RECORDED, NOT OBEYED: that is a
        ///   dashboard preference, and dropping the inverter would withdraw it from the graph the moment
        ///   somebody set it. Read raw because the document declares an integer and 0/1 versus true/false
        ///   is exactly the divergence <see cref="StatusCode"/> proves this API has.
        /// </summary>
        [JsonPropertyName("Show")]
        public JsonElement? Show { get; set; }

        /// <summary>
        ///   The inverter's coarse operational state. DECLARED AN INTEGER by the document and the string
        ///   <c>"Running"</c> on a GEN24, so it is read as a raw element: a typed integer throws there and
        ///   loses the run. <see cref="FroniusInverterStatus.Describe"/> translates a number through the
        ///   document's own table and passes a string through as the device's own word.
        /// </summary>
        [JsonPropertyName("StatusCode")]
        public JsonElement? StatusCode { get; set; }
    }

    /// <summary>The body of <c>GetLoggerInfo.cgi</c>.</summary>
    public sealed class FroniusLoggerInfoBodyDto
    {
        /// <summary>The logging device that fronts the Solar API, when one does.</summary>
        [JsonPropertyName("LoggerInfo")]
        public FroniusLoggerDto? LoggerInfo { get; set; }
    }

    /// <summary>
    ///   The device that serves the Solar API on behalf of the inverters: a datamanager card, a
    ///   hybridmanager, or whatever else fronts it.
    /// </summary>
    public sealed class FroniusLoggerDto
    {
        /// <summary>
        ///   The logging device's own id, which CONTAINS A DOT on the vendor's own example
        ///   (<c>240.107620</c>). That is why it claims under <c>fronius-logger-id</c> and not under
        ///   <c>fronius-unique-id</c>, whose accept pattern rejects the dot. Raw for the same reason as
        ///   the inverter's id.
        /// </summary>
        [JsonPropertyName("UniqueID")]
        public JsonElement? UniqueId { get; set; }

        /// <summary>The product the device reports itself as.</summary>
        [JsonPropertyName("ProductID")]
        public String? ProductId { get; set; }

        /// <summary>The platform the device reports itself as.</summary>
        [JsonPropertyName("PlatformID")]
        public String? PlatformId { get; set; }

        /// <summary>The hardware version.</summary>
        [JsonPropertyName("HWVersion")]
        public String? HardwareVersion { get; set; }

        /// <summary>The software version.</summary>
        [JsonPropertyName("SWVersion")]
        public String? SoftwareVersion { get; set; }

        /// <summary>The timezone location the device is configured for, e.g. <c>Vienna</c>.</summary>
        [JsonPropertyName("TimezoneLocation")]
        public String? TimezoneLocation { get; set; }
    }

    /// <summary>
    ///   One entry of the inverter MAP: the device id the logging device keyed it by, plus what it said
    ///   about it. The key is part of the datum, because it is what a diagnostic can name when the
    ///   inverter carries no id of its own.
    /// </summary>
    public sealed class FroniusInverterEntry
    {
        /// <param name="deviceId">The map key, e.g. <c>1</c>.</param>
        /// <param name="device">What the device said about it.</param>
        public FroniusInverterEntry(String deviceId, FroniusInverterDto device)
        {
            DeviceId = deviceId;
            Device = device;
        }

        /// <summary>The device id the logging device assigned. Instance-local and reassignable, which is
        /// why it is never claimed as an identity: it names a slot, not a box.</summary>
        public String DeviceId { get; }

        /// <summary>What the device said about this inverter.</summary>
        public FroniusInverterDto Device { get; }
    }

    /// <summary>
    ///   The Solar API's OWN status codes, transcribed from the vendor's document (42,0410,2012): OKAY
    ///   plus thirteen failures.
    ///
    ///   <para>It is transcribed rather than summarised because every reply carries one and FAILURE
    ///   ARRIVES WITH HTTP 200: a provider checking only the HTTP status reads a 200 carrying code 12 as
    ///   an empty installation, and an empty complete snapshot withdraws everything this identity ever
    ///   claimed. Having the table means such a run fails saying <c>DeviceNotAvailable</c> rather than
    ///   "12", which is the difference between a reader who acts and a reader who guesses.</para>
    /// </summary>
    public static class FroniusStatusCodes
    {
        /// <summary>The only code that means the body is data.</summary>
        public const Int32 Okay = 0;

        /// <summary>The device saying it could not read the inverter. Named because it is the code that
        /// arrives with HTTP 200 and would otherwise be read as "there is nothing there".</summary>
        public const Int32 DeviceNotAvailable = 12;

        /// <summary>
        ///   The document's own word for a code, or <c>code N</c> for one it does not list, so a value
        ///   the vendor adds later is reported rather than renamed.
        /// </summary>
        public static String Describe(Int32 code)
        {
            switch (code)
            {
                case 0:
                    return "OKAY";
                case 1:
                    return "NotImplemented";
                case 2:
                    // The document's own spelling, kept as printed rather than corrected: a transcribed
                    // table that quietly improves the vendor's words is no longer a transcription.
                    return "Unintialized";
                case 3:
                    return "Initialized";
                case 4:
                    return "Running";
                case 5:
                    return "Timeout";
                case 6:
                    return "Argument_Error";
                case 7:
                    return "LNRequest_Error";
                case 8:
                    return "LNRequest_Timeout";
                case 9:
                    return "LNParse_Error";
                case 10:
                    return "Config_IO_Error";
                case 11:
                    return "Not_Supported";
                case DeviceNotAvailable:
                    return "DeviceNotAvailable";
                case 255:
                    return "UnknownError";
                default:
                    return "code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    ///   The inverter state table, also the document's own: the seven startup phases it collapses into
    ///   one word, then the running states, and the three a GEN24 adds.
    /// </summary>
    public static class FroniusInverterStatus
    {
        /// <summary>The error code that means ABSENCE per the document, not an error numbered minus
        /// one. A property is not written for it, because an absent value is absent.</summary>
        public const Int32 AbsentErrorCode = -1;

        /// <summary>
        ///   The state as a word, from a value the vendor declares an integer and a GEN24 answers as a
        ///   string. A string is passed through as the DEVICE'S OWN WORD rather than reverse-mapped:
        ///   the device already answered the question the table exists for. A number is translated, and
        ///   a number the table does not list is recorded as itself rather than as an invented word.
        /// </summary>
        /// <returns>Null when the device said nothing, because an absent value is absent.</returns>
        public static String? Describe(JsonElement? statusCode)
        {
            if (statusCode == null)
            {
                return null;
            }

            var element = statusCode.Value;
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var word = element.GetString();
                    return String.IsNullOrWhiteSpace(word) ? null : word!.Trim();

                case JsonValueKind.Number:
                    return element.TryGetInt32(out var code)
                        ? Word(code)
                        : element.GetRawText();

                default:
                    return null;
            }
        }

        /// <summary>The document's word for a numeric state, or the number itself for one it does not
        /// list.</summary>
        public static String Word(Int32 code)
        {
            if (code >= 0 && code <= 6)
            {
                // The document gives all seven startup phases one name.
                return "Startup";
            }

            switch (code)
            {
                case 7:
                    return "Running";
                case 8:
                    return "Standby";
                case 9:
                    return "Bootloading";
                case 10:
                    return "Error";

                // The document marks these three as GEN24 only.
                case 11:
                    return "Idle";
                case 12:
                    return "Ready";
                case 13:
                    return "Sleeping";

                default:
                    return code.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    ///   Reading the two values this API sends with a type its own document does not promise. One code
    ///   path per value, so the Datamanager platform and the GEN24 platform are read by the same code
    ///   rather than by a platform switch nothing could keep correct.
    /// </summary>
    public static class FroniusValues
    {
        /// <summary>
        ///   The text of a value the document declares a string. A number is taken as its RAW TEXT
        ///   rather than reparsed, because a GEN24 id is seventeen digits and reparsing one only risks
        ///   losing a digit the device already spelled out.
        /// </summary>
        /// <returns>Null for absent, blank, or a shape that is neither a string nor a number, because an
        /// absent value is absent rather than an empty string.</returns>
        public static String? Text(JsonElement? value)
        {
            if (value == null)
            {
                return null;
            }

            var element = value.Value;
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    return String.IsNullOrWhiteSpace(text) ? null : text!.Trim();

                case JsonValueKind.Number:
                    return element.GetRawText();

                default:
                    return null;
            }
        }

        /// <summary>
        ///   A yes-or-no the document declares an integer, read from whichever of the three plausible
        ///   shapes the device sent.
        /// </summary>
        /// <returns>Null when the device said nothing readable, so no property is written for it.</returns>
        public static Boolean? Flag(JsonElement? value)
        {
            if (value == null)
            {
                return null;
            }

            var element = value.Value;
            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Number:
                    return element.TryGetDouble(out var number) ? number != 0d : (Boolean?)null;

                case JsonValueKind.String:
                    var text = element.GetString();
                    if (Boolean.TryParse(text, out var parsed))
                    {
                        return parsed;
                    }

                    return Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var digits) ? digits != 0 : (Boolean?)null;

                default:
                    return null;
            }
        }
    }
}
