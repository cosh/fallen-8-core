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
    ///   What the system extracts of one read described, in the reader's own terms: elements keyed by their
    ///   AUTOSAR reference path, the relations between them, and what they could not tell the run. One of
    ///   these describes the whole set, because the set is one system and a reference crosses between its
    ///   files as freely as within one of them.
    ///
    ///   <para>Deliberately NOT a snapshot. The reader produces this and the provider maps it, so every
    ///   parsing rule is testable without a runtime, and the decisions that are not the provider's to make
    ///   (canonicalising a path, resolving it to an element, withdrawing anything) stay on the far side of
    ///   the contract.</para>
    /// </summary>
    public sealed class ArxmlNetwork
    {
        /// <summary>Everything the files described, in the order they described it, so a run is reproducible.</summary>
        public List<ArxmlElement> Elements { get; } = new List<ArxmlElement>();

        /// <summary>The edges between them, each end named by an element path.</summary>
        public List<ArxmlRelation> Relations { get; } = new List<ArxmlRelation>();

        /// <summary>What the files could not say. Never fatal: each costs one edge or one element.</summary>
        public List<ArxmlDiagnostic> Diagnostics { get; } = new List<ArxmlDiagnostic>();

        /// <summary>
        ///   The bus kinds the set carried that this version does not read, in the order they were first
        ///   seen, each with how many files it appeared in.
        ///
        ///   <para>Not the same as a diagnostic, though it produces one: the provider needs the list itself
        ///   to decide what to SAY when nothing readable was found, so that a set of Ethernet extracts is
        ///   refused with "this version reads FlexRay and CAN, and the set carries Ethernet" rather than
        ///   with a bare "no bus".</para>
        /// </summary>
        public List<UnreadCluster> UnreadClusters { get; } = new List<UnreadCluster>();
    }

    /// <summary>One bus kind a set carried that this version does not read.</summary>
    public sealed class UnreadCluster
    {
        public UnreadCluster(String element, Int32 files)
        {
            Element = element;
            Files = files;
        }

        /// <summary>The AUTOSAR element name, such as <c>ETHERNET-CLUSTER</c>.</summary>
        public String Element { get; }

        /// <summary>How many files of the set declared one.</summary>
        public Int32 Files { get; }
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

        /// <summary>
        ///   The AUTOSAR reference path, unique within one file by construction of the standard and unique
        ///   across a set because the first file to declare one keeps it.
        /// </summary>
        public String Path { get; }

        /// <summary>The entity kind, from <see cref="ArxmlKinds"/>.</summary>
        public String Kind { get; }

        /// <summary>What the file said about it, keyed WITHOUT the provider's prefix.</summary>
        public IDictionary<String, String> Properties { get; } =
            new Dictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        ///   Reads or writes one property. Writing NULL IS A NO-OP, deliberately and not as a convenience,
        ///   which is why every collector here can assign an optional element's text without testing it
        ///   first. It is the presence rule the snapshot contract's <c>EntityDto</c> owns, tested here on
        ///   null alone because everything reaching it has been through <c>Clean</c>, and spelled out rather
        ///   than shared because this model is the READER'S and carries no dependency on that contract.
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

        /// <summary>Two elements of ONE file composing one reference path. The first was kept.</summary>
        DuplicatePath = 1,

        /// <summary>
        ///   A port a triggering names exists but declares no direction this reader understands, so which
        ///   way the flow edge points cannot be decided. Reported rather than guessed: defaulting would
        ///   silently invert a sender and a receiver, which is worse than a missing edge because a wrong
        ///   edge answers a query confidently.
        /// </summary>
        UndecidablePortDirection = 2,

        /// <summary>
        ///   ONE file of a set re-declared paths an earlier file already declared, counted rather than
        ///   listed. Its own kind and not <see cref="DuplicatePath"/>, because the two are different facts:
        ///   a file contradicting itself is a fault worth naming per path, whereas two extracts of one
        ///   system repeating the standard's shared packages is what every multi-extract job looks like,
        ///   and hundreds of per-path entries would bury the diagnostics that mean something. The subject
        ///   is the re-declaring FILE, since that is the only thing a reader can act on.
        /// </summary>
        RedeclaredPaths = 3,

        /// <summary>
        ///   Several files of a set declared one CLUSTER, so their channels were merged into one network.
        ///   Its own kind rather than part of <see cref="RedeclaredPaths"/>: merging a shared catalogue path
        ///   is always right, whereas merging a cluster is right for one bus split across extracts and
        ///   lossy for two buses that share a path, and the reader cannot tell which it has.
        /// </summary>
        RedeclaredCluster = 4,

        /// <summary>
        ///   The set carried a bus of a kind this version does not read, so everything under that cluster
        ///   was skipped while the files' other content was still read. Reported rather than left silent
        ///   because the run is declared COMPLETE over what it did read.
        /// </summary>
        UnreadCluster = 5,
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

        /// <summary>
        ///   One PHYSICAL CHANNEL of a cluster, which is a different thing on each protocol and is why it
        ///   is an element rather than a count.
        ///
        ///   <para>On CAN it is the single channel a cluster has. On FlexRay it is redundancy: A and B carry
        ///   one schedule, which is why they are two channels of ONE network rather than two networks. On
        ///   ETHERNET a channel is a VLAN, so a cluster has as many as the vehicle has broadcast domains,
        ///   and which one an ECU sits on is a question an engineer asks directly.</para>
        ///
        ///   <para>It replaced a <c>channelCount</c> property on the network, which could not answer that
        ///   question and would have collapsed a couple of dozen VLANs into one number meaning something
        ///   else.</para>
        /// </summary>
        public const String Channel = "channel";

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
        /// <summary>
        ///   ECU to network, AND ECU to channel. The connector that joins them is not itself described.
        ///
        ///   <para>Both, deliberately, and they are not one edge written twice. The network one answers "is
        ///   this ECU on this bus" without knowing the protocol; the channel one answers "which broadcast
        ///   domain", which only means anything on Ethernet. On CAN the two coincide, and that is a fact
        ///   about CAN rather than a redundancy in the model.</para>
        /// </summary>
        public const String AttachedTo = "attachedTo";

        /// <summary>
        ///   STRUCTURAL containment: a channel to its network, and on Ethernet an endpoint, socket or
        ///   connection to its channel. One relation for every level rather than one per pair, because
        ///   "what is under this bus" should not need a type per depth.
        /// </summary>
        public const String PartOf = "partOf";

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

        /// <summary>
        ///   The bus protocol: <see cref="FlexRayProtocol"/> or <see cref="CanProtocol"/>. A query that
        ///   filters on it is filtering on which of the protocol-conditional properties below exist.
        /// </summary>
        public const String Protocol = "protocol";

        /// <summary>The value of <see cref="Protocol"/> for a FlexRay cluster.</summary>
        public const String FlexRayProtocol = "flexray";

        /// <summary>The value of <see cref="Protocol"/> for a CAN cluster.</summary>
        public const String CanProtocol = "can";

        /// <summary>
        ///   The value of <see cref="Protocol"/> for an ETHERNET cluster, where it says more than on the
        ///   other two: an Ethernet bus has NO frame layer, so a query that reaches signals through frames
        ///   finds nothing on it and has to go through the PDU instead. Filtering on this is how a traversal
        ///   knows which shape it is standing in.
        /// </summary>
        public const String EthernetProtocol = "ethernet";

        /// <summary>
        ///   An Ethernet channel's VLAN identifier, which is what makes one channel a distinct broadcast
        ///   domain from the next. Absent on CAN and FlexRay, where a channel is not a VLAN at all.
        /// </summary>
        public const String VlanId = "vlanId";

        /// <summary>The VLAN's own name, when the source carries one beside the identifier.</summary>
        public const String VlanName = "vlanName";

        /// <summary>
        ///   The bus's nominal bit rate. Protocol-neutral: the standard carries it on the cluster
        ///   conditional of every protocol.
        /// </summary>
        public const String Baudrate = "baudrate";

        /// <summary>The bus's data-phase bit rate, on a CAN bus that runs CAN FD. Absent otherwise.</summary>
        public const String CanFdBaudrate = "canFdBaudrate";

        /// <summary>The protocol name the file states, which is the vendor's word rather than this
        /// reader's. Kept beside <see cref="Protocol"/> and never instead of it.</summary>
        public const String ProtocolName = "protocolName";

        /// <summary>The protocol version the file states.</summary>
        public const String ProtocolVersion = "protocolVersion";

        /// <summary>A frame's length in bytes.</summary>
        public const String FrameLengthBytes = "frameLengthBytes";

        /// <summary>
        ///   The FlexRay slot a frame is scheduled in. PROTOCOL-CONDITIONAL: absent on a CAN frame, which
        ///   is not scheduled at all, so a query must tolerate its absence rather than assume zero.
        /// </summary>
        public const String SlotId = "slotId";

        /// <summary>The first cycle a frame is scheduled in. Absent on CAN, as <see cref="SlotId"/>.</summary>
        public const String BaseCycle = "baseCycle";

        /// <summary>How often the frame repeats across the cycle counter. Absent on CAN.</summary>
        public const String CycleRepetition = "cycleRepetition";

        /// <summary>
        ///   A CAN frame's identifier, which is what an engineer names a CAN frame by. Absent on FlexRay,
        ///   which addresses by slot instead.
        ///
        ///   <para>Denormalised from the frame's TRIGGERING onto the frame, exactly as the FlexRay schedule
        ///   is, because the triggering is the standard's indirection rather than a thing anybody names. The
        ///   first declaration wins where a frame is triggered more than once.</para>
        /// </summary>
        public const String CanId = "canId";

        /// <summary>
        ///   Whether a CAN frame's identifier is standard or extended, as the standard's own word. Absent on
        ///   FlexRay. It is not derivable from the identifier: an 11-bit value is legal in either mode.
        /// </summary>
        public const String CanAddressingMode = "canAddressingMode";

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
