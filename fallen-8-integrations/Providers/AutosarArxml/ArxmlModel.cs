// MIT License
//
// ArxmlModel.cs
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

namespace NoSQL.GraphDB.Integrations.Providers.AutosarArxml
{
    /// <summary>
    ///   What one system extract described, in the reader's own terms: elements keyed by their AUTOSAR
    ///   reference path, the relations between them, and what the file could not tell the run.
    ///
    ///   <para>Deliberately NOT a snapshot. The reader produces this and the provider maps it, so every
    ///   parsing rule is testable without a runtime, and the decisions that are not the provider's to make
    ///   (canonicalising a path, resolving it to an element, withdrawing anything) stay on the far side of
    ///   the contract.</para>
    /// </summary>
    public sealed class ArxmlNetwork
    {
        /// <summary>Everything the file described, in the order it described it, so a run is reproducible.</summary>
        public List<ArxmlElement> Elements { get; } = new List<ArxmlElement>();

        /// <summary>The edges between them, each end named by an element path.</summary>
        public List<ArxmlRelation> Relations { get; } = new List<ArxmlRelation>();

        /// <summary>What the file could not say. Never fatal: each costs one edge or one element.</summary>
        public List<ArxmlDiagnostic> Diagnostics { get; } = new List<ArxmlDiagnostic>();
    }

    /// <summary>
    ///   One thing the extract described. <see cref="Path"/> is its AUTOSAR reference path, which the
    ///   standard makes both its identity and the way every cross-reference addresses it.
    /// </summary>
    public sealed class ArxmlElement
    {
        public ArxmlElement(String path, String kind)
        {
            Path = path;
            Kind = kind;
        }

        /// <summary>The AUTOSAR reference path, unique within one file by construction of the standard.</summary>
        public String Path { get; }

        /// <summary>The entity kind, from <see cref="ArxmlKinds"/>.</summary>
        public String Kind { get; }

        /// <summary>What the file said about it, keyed WITHOUT the provider's prefix.</summary>
        public IDictionary<String, String> Properties { get; } =
            new Dictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        ///   Reads or writes one property. Writing NULL IS A NO-OP, deliberately and not as a convenience:
        ///   an absent value must stay absent, because writing an empty string makes the property exist and
        ///   overwrites what another integration knows about the same element. That is why every collector
        ///   here can assign an optional element's text without testing it first.
        /// </summary>
        public String? this[String key]
        {
            get { return Properties.TryGetValue(key, out var value) ? value : null; }

            set
            {
                if (value != null)
                {
                    Properties[key] = value;
                }
            }
        }
    }

    /// <summary>One edge the extract described, both ends named by element path.</summary>
    public sealed class ArxmlRelation
    {
        public ArxmlRelation(String fromPath, String type, String toPath)
        {
            FromPath = fromPath;
            Type = type;
            ToPath = toPath;
        }

        public String FromPath { get; }

        public String Type { get; }

        public String ToPath { get; }
    }

    /// <summary>Something a reader of the job report needs to know about the file.</summary>
    public sealed class ArxmlDiagnostic
    {
        public ArxmlDiagnostic(ArxmlDiagnosticKind kind, String message, String subject)
        {
            Kind = kind;
            Message = message;
            Subject = subject;
        }

        public ArxmlDiagnosticKind Kind { get; }

        public String Message { get; }

        public String Subject { get; }
    }

    /// <summary>
    ///   What the reader can report. An ENUM rather than a diagnostic code string, so the reader carries no
    ///   dependency on the snapshot contract and the provider owns the mapping to a wire code in one place.
    /// </summary>
    public enum ArxmlDiagnosticKind
    {
        /// <summary>A reference naming a path the file does not define. What pointed at it was dropped.</summary>
        UnresolvedReference = 0,

        /// <summary>Two elements composing one reference path. The first was kept.</summary>
        DuplicatePath = 1,

        /// <summary>
        ///   A port a triggering names exists but declares no direction this reader understands, so which
        ///   way the flow edge points cannot be decided. Reported rather than guessed: defaulting would
        ///   silently invert a sender and a receiver, which is worse than a missing edge because a wrong
        ///   edge answers a query confidently.
        /// </summary>
        UndecidablePortDirection = 2,
    }

    /// <summary>
    ///   "This file is not an AUTOSAR system extract, or not one I will read." A refusal to guess, which the
    ///   provider turns into a failed RUN: a file that could not be read is not a network with nothing in
    ///   it, and describing it as empty would withdraw every element the identity ever claimed.
    /// </summary>
    public sealed class ArxmlFormatException : Exception
    {
        public ArxmlFormatException(String message)
            : base(message)
        {
        }

        public ArxmlFormatException(String message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>The entity kinds this provider produces, which become element labels.</summary>
    public static class ArxmlKinds
    {
        /// <summary>A communication cluster: the bus itself.</summary>
        public const String Network = "network";

        /// <summary>An ECU instance: a controller on the bus.</summary>
        public const String Ecu = "ecu";

        /// <summary>A frame: what is actually transmitted in a slot.</summary>
        public const String Frame = "frame";

        /// <summary>A PDU of any flavour, the flavour being a property.</summary>
        public const String Pdu = "pdu";

        /// <summary>An I-SIGNAL: one value as it travels on the wire.</summary>
        public const String Signal = "signal";

        /// <summary>A SYSTEM-SIGNAL: the network-wide meaning an I-SIGNAL implements.</summary>
        public const String SystemSignal = "system-signal";

        /// <summary>A COMPU-METHOD: how raw bits become a physical quantity.</summary>
        public const String CompuMethod = "compu-method";
    }

    /// <summary>The relation types this provider emits, which become edge types.</summary>
    public static class ArxmlRelations
    {
        /// <summary>ECU to network. The connector that joins them is not itself described.</summary>
        public const String AttachedTo = "attachedTo";

        /// <summary>ECU to frame or signal, from a port whose direction is OUT.</summary>
        public const String Sends = "sends";

        /// <summary>Frame or signal to ECU, from a port whose direction is IN.</summary>
        public const String DeliversTo = "deliversTo";

        /// <summary>Frame to PDU, or PDU to signal.</summary>
        public const String Contains = "contains";

        /// <summary>Container PDU to a PDU it carries.</summary>
        public const String Carries = "carries";

        /// <summary>Secured PDU to the PDU whose payload it protects.</summary>
        public const String Secures = "secures";

        /// <summary>Signal to the system signal it implements.</summary>
        public const String Implements = "implements";

        /// <summary>System signal to the compu method that scales it.</summary>
        public const String ScaledBy = "scaledBy";
    }

    /// <summary>
    ///   The property keys the reader produces, WITHOUT the provider's prefix: the prefix belongs to the
    ///   provider and is applied in exactly one place, so a key never exists in two spellings.
    /// </summary>
    public static class ArxmlProperties
    {
        /// <summary>The element's own short name.</summary>
        public const String Name = "name";

        /// <summary>The bus protocol. Its only value today is <see cref="FlexRayProtocol"/>.</summary>
        public const String Protocol = "protocol";

        /// <summary>The value of <see cref="Protocol"/> for a FlexRay cluster.</summary>
        public const String FlexRayProtocol = "flexray";

        /// <summary>How many physical channels the cluster carries, which for FlexRay is redundancy.</summary>
        public const String ChannelCount = "channelCount";

        /// <summary>A frame's length in bytes.</summary>
        public const String FrameLengthBytes = "frameLengthBytes";

        /// <summary>The FlexRay slot a frame is scheduled in.</summary>
        public const String SlotId = "slotId";

        /// <summary>The first cycle a frame is scheduled in.</summary>
        public const String BaseCycle = "baseCycle";

        /// <summary>How often the frame repeats across the cycle counter.</summary>
        public const String CycleRepetition = "cycleRepetition";

        /// <summary>Which flavour of PDU this is, as the AUTOSAR element name.</summary>
        public const String PduKind = "pduKind";

        /// <summary>A PDU's length in bytes.</summary>
        public const String LengthBytes = "lengthBytes";

        /// <summary>A signal's width in bits.</summary>
        public const String LengthBits = "lengthBits";

        /// <summary>A signal's initial value.</summary>
        public const String InitValue = "initValue";

        /// <summary>A signal's platform base type, such as uint8.</summary>
        public const String BaseType = "baseType";

        /// <summary>The German description, when the file carries one.</summary>
        public const String DescriptionDe = "descDe";

        /// <summary>The English description, when the file carries one.</summary>
        public const String DescriptionEn = "descEn";

        /// <summary>A compu method's category, such as LINEAR.</summary>
        public const String Category = "category";

        /// <summary>
        ///   The physical unit, as the unit's DISPLAY NAME ("km") rather than its short name ("UNIT_KM").
        ///   Carried by a compu method and DENORMALISED onto the signal two hops down its own chain, which
        ///   is what lets a semantic query for a distance in kilometers reach an odometer whose description
        ///   only ever says "accumulated distance".
        /// </summary>
        public const String Unit = "unit";
    }
}
