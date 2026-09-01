// MIT License
//
// ArxmlReader.cs
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
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NoSQL.GraphDB.Integrations.Providers.AutosarArxml
{
    /// <summary>
    ///   Reads the AUTOSAR classic-platform system extracts of ONE system and describes the FlexRay
    ///   communication matrix they carry. It knows nothing about snapshots, claims or the graph: it produces
    ///   <see cref="ArxmlNetwork"/>, which the provider maps. That split is what lets every parsing rule be
    ///   tested without a runtime, and it is why the rules that decide identity live on the other side.
    ///
    ///   <para>Two properties of the AUTOSAR standard carry the whole design. An element's identity is its
    ///   REFERENCE PATH, the slash-separated chain of short-names from the package root, and every
    ///   cross-reference in the file is written as that same path. So the reader rebuilds paths from a
    ///   short-name stack while streaming, and resolution afterwards is exact string equality on a
    ///   dictionary. Nothing is matched by name, position or similarity.</para>
    ///
    ///   <para>SEVERAL DOCUMENTS MAKE ONE DESCRIPTION. A vehicle network is handed over as one extract per
    ///   domain or per bus, and those extracts reference each other by path, so every document
    ///   <see cref="Add(String, Stream)"/>ed streams into ONE table and <see cref="Complete"/> resolves once
    ///   over their union: that, and nothing else, is what makes a reference from one extract into another resolve
    ///   exactly like a reference within one file. Order is part of the meaning - where two documents declare
    ///   one path, the earlier one owns it - so the caller's order is kept rather than sorted.</para>
    ///
    ///   <para>It streams. A system extract is routinely tens of megabytes of which the communication matrix
    ///   is a small fraction, so only the elements in the interest set are materialised as subtrees and
    ///   everything else advances the reader without allocating. One document at a time, too: a caller hands
    ///   over one file per <see cref="Add(String, Stream)"/> and nothing here keeps it, which is what stops a
    ///   set of tens-of-megabytes extracts from being held all at once. Bytes rather than text, for the
    ///   reason given on that overload.</para>
    /// </summary>
    public sealed class ArxmlReader
    {
        /// <summary>The one schema namespace this reader understands. It covers every AUTOSAR 4.x release.</summary>
        public const String Namespace = "http://autosar.org/schema/r4.0";

        /// <summary>The document element every system extract carries.</summary>
        public const String RootElement = "AUTOSAR";

        private static readonly XNamespace Ar = Namespace;

        private const String ShortNameElement = "SHORT-NAME";

        /// <summary>
        ///   The XML Schema instance namespace, which is where <c>schemaLocation</c> lives. Its value is the
        ///   only statement of the AUTOSAR REVISION a document was written against: the AUTOSAR namespace is
        ///   the same across revisions of the classic platform.
        /// </summary>
        private const String SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

        // The PDU kinds worth describing. A PDU is a PDU whatever its flavour, so the flavour becomes a
        // property rather than a kind: a query for "what does this frame carry" must not have to enumerate
        // one label per flavour, and the next flavour the standard adds must not become another kind.
        private static readonly HashSet<String> PduElements = new HashSet<String>(StringComparer.Ordinal)
        {
            "I-SIGNAL-I-PDU",
            "NM-PDU",
            "N-PDU",
            "DCM-I-PDU",
            "SECURED-I-PDU",
            "CONTAINER-I-PDU",
            "GENERAL-PURPOSE-PDU",
            "GENERAL-PURPOSE-I-PDU",
            "USER-DEFINED-I-PDU",
            "USER-DEFINED-PDU",
            "MULTIPLEXED-I-PDU",
            "XCP-PDU",
        };

        // Read as whole subtrees. Everything else streams past.
        private static readonly HashSet<String> Interesting;

        private static readonly Dictionary<String, BusProtocol> ClusterElements;

        private static readonly Dictionary<String, BusProtocol> FrameElements;

        /// <summary>The unread bus kinds as a set, for the membership test in <c>Collect</c>.</summary>
        private static readonly HashSet<String> UnreadClusters;

        /// <summary>
        ///   Every element name that IS a socket connection, across AUTOSAR revisions. Both are here rather
        ///   than one being chosen by a detected revision: the spellings do not overlap, so a document can
        ///   only be using one, and reading for both is unambiguous. See <c>CollectSocketLayer</c>.
        /// </summary>
        private static readonly HashSet<String> ConnectionElements = new HashSet<String>(StringComparer.Ordinal)
        {
            "SOCKET-CONNECTION-BUNDLE",
            "STATIC-SOCKET-CONNECTION",
        };

        /// <summary>
        ///   Every element name that identifies a PDU inside a connection, across revisions. Each carries a
        ///   PDU triggering reference and a header id.
        /// </summary>
        private static readonly HashSet<String> PduIdentifierElements = new HashSet<String>(StringComparer.Ordinal)
        {
            "SOCKET-CONNECTION-IPDU-IDENTIFIER",
            "SO-CON-I-PDU-IDENTIFIER",
        };

        /// <summary>
        ///   Every reference element that names one END of a connection. A socket address reference is here
        ///   as well as the port spellings, because the newer form names remote ADDRESSES rather than ports.
        /// </summary>
        private static readonly HashSet<String> PortReferenceElements = new HashSet<String>(StringComparer.Ordinal)
        {
            "SERVER-PORT-REF",
            "CLIENT-PORT-REF",
            "SOCKET-ADDRESS-REF",
        };

        /// <summary>
        ///   EVERY DERIVED STATIC IS BUILT HERE, IN ORDER, and none of them is a field initialiser.
        ///
        ///   <para>Not a style choice. A field initialiser runs in declaration order, so a field that reads
        ///   another one declared further down the file binds null - and where the read happens inside a
        ///   helper method the compiler cannot see it, so it is a NullReferenceException from a type
        ///   initialiser at first use rather than a build error. That is exactly what adding the protocol
        ///   table caused: the interest set is derived from it and was declared above it. A static
        ///   constructor makes the order explicit and stops a later reordering from reintroducing it.</para>
        /// </summary>
        static ArxmlReader()
        {
            ClusterElements = BuildClusterElements();
            FrameElements = BuildFrameElements();
            UnreadClusters = new HashSet<String>(UnreadClusterElements, StringComparer.Ordinal);
            Interesting = BuildInterestSet();
        }

        /// <summary>Where every document's elements and references land, and the only one there is.</summary>
        private readonly Collected _collected = new Collected();

        private Boolean _completed;

        /// <summary>
        ///   Reads ONE document that has no name to give: the whole read, in one call, for every caller that
        ///   composes nothing. Expressed in terms of the multi-document path so there is ONE code path and
        ///   not two that can drift.
        /// </summary>
        /// <param name="xml">The document text.</param>
        /// <exception cref="ArxmlFormatException">The text is not XML this reader will read: a DTD, a root
        /// element or namespace that is not an AUTOSAR r4.0 extract, or malformed XML. Each is a refusal to
        /// GUESS, and the provider turns it into a failed run rather than an empty description, because a
        /// file that could not be read is not a network with nothing in it.</exception>
        public static ArxmlNetwork Read(String xml)
        {
            var reader = new ArxmlReader();
            reader.Add(String.Empty, xml);
            return reader.Complete();
        }

        /// <summary>
        ///   Reads ONE document from its bytes, for a caller that composes nothing. The shape to prefer, for
        ///   the reasons on <see cref="Add(String, Stream)" />.
        /// </summary>
        /// <param name="xml">The document's bytes.</param>
        /// <exception cref="ArxmlFormatException">The bytes are not an AUTOSAR r4.0 extract this reader will
        /// read, as on the text overload.</exception>
        public static ArxmlNetwork Read(Stream xml)
        {
            var reader = new ArxmlReader();
            reader.Add(String.Empty, xml);
            return reader.Complete();
        }

        /// <summary>
        ///   Streams one more document into this read, after every document already added.
        ///
        ///   <para>The NAME is not decoration. Every refusal below names the document it is about, and "the
        ///   extract is not readable AUTOSAR" is not actionable when four of them arrived in one job; the same
        ///   name is what the re-declaration diagnostic points at.</para>
        /// </summary>
        /// <param name="fileName">What the document is called, as a name and never a path.</param>
        /// <param name="xml">The document text. It is not retained.</param>
        /// <exception cref="ArxmlFormatException">This document is not readable as an AUTOSAR r4.0 extract,
        /// which fails the WHOLE read: a set of extracts one of which could not be read is not a network
        /// missing a domain, and describing it as one would withdraw everything that domain claimed.</exception>
        public void Add(String fileName, String xml)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }

            using (var text = new StringReader(xml))
            {
                Consume(fileName, settings => XmlReader.Create(text, settings));
            }
        }

        /// <summary>
        ///   Streams one more document into this read from its BYTES, which is the shape a run actually
        ///   has and the one to prefer.
        ///
        ///   <para>Not a convenience over the string overload: it is a document less held. This reader has
        ///   always driven an <c>XmlReader</c> and materialises only the subtrees it collects, so handing it
        ///   text meant decoding a whole extract to UTF-16 - two bytes per character, held for the parse, on
        ///   top of the bytes the run holds anyway - for a reader that never wanted a string. From bytes,
        ///   peak memory stops tracking the largest file.</para>
        ///
        ///   <para>It also reads MORE documents correctly. Decoding to text detects a byte-order mark and
        ///   otherwise assumes UTF-8, whereas an <c>XmlReader</c> over the bytes honours the document's own
        ///   declaration, so an extract written in another encoding without a mark stops arriving with
        ///   mojibake in its short names - which are identities here, not display text.</para>
        ///
        ///   <para>The stream is the CALLER's to dispose, and is read forwards once.</para>
        /// </summary>
        /// <param name="fileName">What the document is called, as a name and never a path.</param>
        /// <param name="xml">The document's bytes. They are not retained.</param>
        /// <exception cref="ArxmlFormatException">This document is not readable as an AUTOSAR r4.0 extract,
        /// which fails the WHOLE read, for the reason given on the text overload.</exception>
        public void Add(String fileName, Stream xml)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }

            Consume(fileName, settings => XmlReader.Create(xml, settings));
        }

        /// <summary>
        ///   Resolves ONCE over the union of every document added, and describes it.
        /// </summary>
        /// <exception cref="ArxmlFormatException">No document was added at all.</exception>
        public ArxmlNetwork Complete()
        {
            if (_collected.Documents == 0)
            {
                throw new ArxmlFormatException(
                    "No document was given to read, so there is nothing to describe. An empty set reaching " +
                    "here is a miscount by the caller rather than a fact about a source, and reporting it " +
                    "as an empty network would withdraw everything the identity ever claimed.");
            }

            _completed = true;
            return Resolve(_collected);
        }

        /// <summary>
        ///   One document, streamed into the shared table under its own root gate.
        ///
        ///   <para>Takes HOW to open the reader rather than the document, so text and bytes share one code
        ///   path: the hardening below, the root gate, the "no elements at all" refusal and the way a
        ///   refusal names its file are decided once and cannot differ by input shape.</para>
        /// </summary>
        private void Consume(String fileName, Func<XmlReaderSettings, XmlReader> open)
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "This read is finished, so a document added now would be described by nothing: " +
                    "resolution has already run over the union of what was there when it ran.");
            }

            // Hardened deliberately, and more load-bearing since the bytes arrive in a REQUEST BODY rather
            // than off a mount an operator prepared: whoever can reach the API now chooses this document. A
            // DTD is refused outright, because entity expansion and external-entity resolution are the two
            // ways an XML reader turns a data file into a fetch or a memory exhaustion, and an AUTOSAR
            // extract has no legitimate use for either.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            _collected.BeginDocument(fileName);

            Boolean sawRoot;
            try
            {
                using (var reader = open(settings))
                {
                    sawRoot = Stream(reader, _collected, fileName);
                }
            }
            catch (XmlException failure)
            {
                throw new ArxmlFormatException(
                    Subject(fileName) + " is not readable as XML: " + failure.Message +
                    " A DTD is refused outright, so a document carrying one arrives here too.", failure);
            }

            if (!sawRoot)
            {
                throw new ArxmlFormatException(Subject(fileName) +
                    " carries no elements at all, so it is not an AUTOSAR system extract.");
            }

            _collected.EndDocument();
        }

        /// <summary>
        ///   How a refusal names the document it is about. One phrasing rather than two, with the file named
        ///   whenever there is a name: the single-document entry point has none to give and says so instead of
        ///   inventing one.
        /// </summary>
        private static String Subject(String fileName)
        {
            return fileName.Length == 0 ? "The document" : "The file '" + fileName + "'";
        }

        /// <summary>
        ///   The streaming pass over one document. It maintains the short-name stack that IS the path of
        ///   whatever the reader is inside, and hands each interesting element to <see cref="Collect"/> as a
        ///   materialised subtree. Returns whether the document had a document element at all.
        /// </summary>
        private static Boolean Stream(XmlReader reader, Collected collected, String fileName)
        {
            // (element name, short name once seen). An entry whose short name stays null is a structural
            // wrapper such as AR-PACKAGES or ELEMENTS, and contributes nothing to a path, which is exactly
            // the standard's own path semantics.
            var stack = new List<Frame>();
            var advanced = true;

            // PER DOCUMENT, not per read: every extract of a set has to pass the root gate on its own, so a
            // job that slipped one CAN bus log in among four extracts is refused rather than half-read.
            var sawRoot = false;

            while (true)
            {
                if (advanced && !reader.Read())
                {
                    break;
                }

                advanced = true;

                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.LocalName;

                    if (!sawRoot)
                    {
                        Gate(reader, name, fileName);

                        // The document element is also the only place the AUTOSAR REVISION is stated, and
                        // it is stated in xsi:schemaLocation rather than in the namespace: every revision of
                        // the classic platform uses the same namespace, so the namespace says nothing about
                        // which one this is. Read here because the reader is standing on the root exactly
                        // once. Nothing branches on it - see CollectSocketLayer - it makes a diagnostic
                        // actionable.
                        collected.DeclaresSchema(reader.GetAttribute("schemaLocation", SchemaInstance));
                        sawRoot = true;
                    }

                    // A short name names the element the reader is currently inside, which is the frame on
                    // top of the stack. Reading its content advances the reader past the end tag, so the
                    // outer loop must not read again.
                    if (String.Equals(name, ShortNameElement, StringComparison.Ordinal) &&
                        stack.Count > 0 && stack[stack.Count - 1].ShortName == null)
                    {
                        // Trimmed, and a blank one leaves the frame UNNAMED rather than naming it the
                        // empty string: an empty segment would compose a path with a double slash that
                        // no reference in the file can match, so every element below it would silently
                        // lose its identity.
                        stack[stack.Count - 1].ShortName = Clean(reader.ReadElementContentAsString());
                        advanced = false;
                        continue;
                    }

                    if (Interesting.Contains(name))
                    {
                        var prefix = PathOf(stack);
                        var element = (XElement)XNode.ReadFrom(reader);
                        Collect(name, prefix, element, collected);
                        advanced = false;
                        continue;
                    }

                    if (!reader.IsEmptyElement)
                    {
                        stack.Add(new Frame(name));
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }

            return sawRoot;
        }

        /// <summary>
        ///   Refuses a document that is not an AUTOSAR r4.0 extract, naming which document and what was found.
        ///   A reader that carried on would describe an empty network, and an empty COMPLETE description
        ///   withdraws everything the identity ever claimed.
        /// </summary>
        private static void Gate(XmlReader reader, String name, String fileName)
        {
            if (!String.Equals(name, RootElement, StringComparison.Ordinal) ||
                !String.Equals(reader.NamespaceURI, Namespace, StringComparison.Ordinal))
            {
                throw new ArxmlFormatException(String.Format(CultureInfo.InvariantCulture,
                    "{0} has document element '{1}' in namespace '{2}', and an AUTOSAR system extract is " +
                    "'{3}' in namespace '{4}'. It was not read further rather than being read as an " +
                    "extract with nothing in it.", Subject(fileName), name, reader.NamespaceURI, RootElement,
                    Namespace));
            }
        }

        /// <summary>
        ///   WHAT DIFFERS BETWEEN ONE BUS PROTOCOL AND THE NEXT, which is less than it looks.
        ///
        ///   <para>The walk is identical on every protocol: cluster, physical channel, frame triggering,
        ///   frame, PDU, signal. Only the element NAMES change, plus what the triggering carries: FlexRay
        ///   nests a schedule, CAN puts an identifier directly on the triggering. So this is a table rather
        ///   than a reader per protocol.</para>
        ///
        ///   <para>A table and not a copied method, for a reason that is not visible from here: an Ethernet
        ///   cluster has NO frame layer at all - no frame element, no frame triggering, no
        ///   PDU-to-frame mapping - so a third entry has to be able to say "there is no frame here", and
        ///   three copies of the cluster walk is how that becomes unaffordable.</para>
        /// </summary>
        private sealed class BusProtocol
        {
            public BusProtocol(String cluster, String channel, String? frame, String? frameTriggering,
                String protocol, Boolean socketLayer)
            {
                ClusterElement = cluster;
                ChannelElement = channel;
                FrameElement = frame;
                FrameTriggeringElement = frameTriggering;
                Protocol = protocol;
                HasSocketLayer = socketLayer;
            }

            public String ClusterElement { get; }

            public String ChannelElement { get; }

            /// <summary>The frame element's name, or NULL on a protocol with no frame layer.</summary>
            public String? FrameElement { get; }

            /// <summary>The frame triggering's name, or NULL on a protocol with no frame layer.</summary>
            public String? FrameTriggeringElement { get; }

            /// <summary>Whether this protocol has a frame layer at all. Ethernet does not.</summary>
            public Boolean HasFrames => FrameElement != null;

            /// <summary>
            ///   Whether this protocol has a SOCKET LAYER to read. Declared rather than derived from
            ///   <see cref="HasFrames" />: "no frames" and "has sockets" are two different facts about a
            ///   protocol, and deriving one from the other would make the next protocol without frames
            ///   silently searched for endpoints it does not have. It also keeps the scan off the buses that
            ///   have none, which matters because a CAN channel's materialised subtree is large.
            /// </summary>
            public Boolean HasSocketLayer { get; }

            /// <summary>The value written to <see cref="ArxmlProperties.Protocol" />.</summary>
            public String Protocol { get; }
        }

        /// <summary>
        ///   What happened when a collector offered an element for a reference path.
        ///
        ///   <para>It exists because CONTAINERS and LEAVES want different things from the same answer. A
        ///   leaf whose path is taken must stop, because the work after it records that element's own
        ///   references under the same path and would hand them to the surviving twin. A container whose
        ///   path is taken must CARRY ON, because its children are its own: an ECU's connectors are
        ///   bus-local, so each extract carries only the ones for its bus, and one real vehicle has three
        ///   extracts declaring one cluster path with different channels in each.</para>
        /// </summary>
        private enum PathClaim
        {
            /// <summary>The path was free. This element is now its owner.</summary>
            Recorded,

            /// <summary>
            ///   An EARLIER FILE owns the element. Ordinary rather than a fault - every extract of one
            ///   system repeats the standard's shared packages - and the caller's children still belong to
            ///   the caller.
            /// </summary>
            SharedWithEarlierFile,

            /// <summary>
            ///   THIS file already declared the path, which contradicts the standard's own terms: one path
            ///   is one thing. Every caller stops.
            /// </summary>
            DuplicateInThisFile,
        }

        /// <summary>Every bus protocol this version reads. Adding one is adding an entry here.</summary>
        private static readonly BusProtocol[] BusProtocols =
        {
            new BusProtocol("FLEXRAY-CLUSTER", "FLEXRAY-PHYSICAL-CHANNEL", "FLEXRAY-FRAME",
                "FLEXRAY-FRAME-TRIGGERING", ArxmlProperties.FlexRayProtocol, socketLayer: false),
            new BusProtocol("CAN-CLUSTER", "CAN-PHYSICAL-CHANNEL", "CAN-FRAME",
                "CAN-FRAME-TRIGGERING", ArxmlProperties.CanProtocol, socketLayer: false),

            // NO FRAME LAYER, which is the whole reason this is a table. An Ethernet channel carries
            // PDU-TRIGGERING directly: the socket layer does what a frame does elsewhere, so there is no
            // frame element, no frame triggering and no PDU-to-frame mapping to look for. Nulls rather than
            // empty strings, so a missing name is a case the code has to handle rather than a search for an
            // element called "".
            new BusProtocol("ETHERNET-CLUSTER", "ETHERNET-PHYSICAL-CHANNEL", null, null,
                ArxmlProperties.EthernetProtocol, socketLayer: true),
        };

        /// <summary>
        ///   Every cluster element name this version does NOT read, so a run can say which bus it skipped
        ///   rather than leaving an operator to infer it from a missing network.
        ///
        ///   <para>Enumerated rather than derived, because "a cluster kind we do not read" cannot be
        ///   detected by absence: the reader only materialises its interest set, so an unread cluster is
        ///   invisible unless it is named here. The cost is that a protocol nobody listed stays silent,
        ///   which is why the list is a superset of what the standard defines for the classic platform.</para>
        /// </summary>
        private static readonly String[] UnreadClusterElements =
        {
            "LIN-CLUSTER",
            "TTCAN-CLUSTER",
            "J1939-CLUSTER",
        };

        /// <summary>Dispatches one materialised element of the interest set.</summary>
        private static void Collect(String name, String prefix, XElement element, Collected collected)
        {
            var shortName = Text(element.Element(Ar + ShortNameElement));
            if (shortName == null)
            {
                // An element of the interest set with no short name has no identity in the standard's own
                // terms, so there is nothing to key it by and nothing to point at it.
                return;
            }

            var path = prefix + "/" + shortName;

            if (PduElements.Contains(name))
            {
                CollectPdu(name, path, shortName, element, collected);
                return;
            }

            if (ClusterElements.TryGetValue(name, out var bus))
            {
                CollectCluster(path, shortName, element, collected, bus);
                return;
            }

            if (UnreadClusters.Contains(name))
            {
                // A bus this version does not read. NOTED rather than skipped, because the alternative is
                // silence: the reader materialises only its interest set, so an unread bus leaves no trace
                // at all and an operator is left inferring it from a network that never appeared. Every
                // element below it stays unread, so the cost is one short-name read per cluster.
                collected.UnreadCluster(name);
                return;
            }

            if (FrameElements.TryGetValue(name, out var frameBus))
            {
                CollectFrame(path, shortName, element, collected, frameBus);
                return;
            }

            switch (name)
            {
                case "ECU-INSTANCE":
                    CollectEcu(path, shortName, element, collected);
                    break;
                case "I-SIGNAL":
                    CollectSignal(path, shortName, element, collected);
                    break;
                case "SYSTEM-SIGNAL":
                    CollectSystemSignal(path, shortName, element, collected);
                    break;
                case "COMPU-METHOD":
                    CollectCompuMethod(path, shortName, element, collected);
                    break;
                case "UNIT":
                    // Not an entity: a unit exists so a signal can say "km" rather than "UNIT_KM", which is
                    // the difference between a semantic query for a distance finding the odometer and not.
                    // First wins here too, for the same reason a duplicate element path does.
                    if (!collected.UnitDisplayNames.ContainsKey(path))
                    {
                        collected.UnitDisplayNames[path] =
                            Text(element.Element(Ar + "DISPLAY-NAME")) ?? shortName;
                    }

                    break;
            }
        }

        /// <summary>
        ///   An ECU, and the connectors and ports through which it reaches a bus.
        ///
        ///   <para>A CONTAINER, so it carries on into its children even when an earlier file already owns
        ///   the element. That matters here more than anywhere: an ECU's declaration is BUS-LOCAL. A gateway
        ///   sits on several buses and each extract carries only its own bus's connector for it, so the old
        ///   rule - an already-owned path means skip the subtree - attached such an ECU to whichever bus
        ///   happened to be read first and to none of the others. Measured on one real vehicle: 10 of the 17
        ///   ECUs in a three-extract collision ended up attached to nothing at all.</para>
        /// </summary>
        private static void CollectEcu(String path, String shortName, XElement element, Collected collected)
        {
            if (collected.Claim(new ArxmlElement(path, ArxmlKinds.Ecu)
                    { [ArxmlProperties.Name] = shortName }, collected.Ecus) == PathClaim.DuplicateInThisFile)
            {
                return;
            }

            foreach (var connector in Descendants(element, n => n.EndsWith("COMMUNICATION-CONNECTOR",
                         StringComparison.Ordinal)))
            {
                var connectorName = Text(connector.Element(Ar + ShortNameElement));
                if (connectorName == null)
                {
                    continue;
                }

                var connectorPath = path + "/" + connectorName;

                // FIRST declaration wins, on this and every side table below. They are plain dictionary
                // writes, so under the union rule an unguarded write would be LAST-file-wins, and the graph
                // would then depend on the order the caller happened to list the files: a determinism
                // failure the conformance suite checks for, and a source of rewrite-and-re-embed churn on
                // every run. Guarding here rather than at the read site keeps the rule in one place.
                if (!collected.ConnectorToEcu.ContainsKey(connectorPath))
                {
                    collected.ConnectorToEcu[connectorPath] = path;
                }

                foreach (var port in Descendants(connector,
                             n => n == "FRAME-PORT" || n == "I-PDU-PORT" || n == "I-SIGNAL-PORT"))
                {
                    var portName = Text(port.Element(Ar + ShortNameElement));
                    if (portName == null)
                    {
                        continue;
                    }

                    // The RAW direction word is kept, not a boolean. A port with no direction, or one
                    // carrying a word this reader does not know, is registered anyway so that resolution
                    // can tell "the file never defined this port" from "the port is there and its
                    // direction is unusable". Collapsing both into a boolean made every unrecognised
                    // word mean IN, which silently inverts a sender and a receiver.
                    var portPath = connectorPath + "/" + portName;
                    if (!collected.Ports.ContainsKey(portPath))
                    {
                        collected.Ports[portPath] =
                            Text(port.Element(Ar + "COMMUNICATION-DIRECTION")) ?? String.Empty;
                    }
                }
            }
        }

        /// <summary>
        ///   One bus, and everything the standard hangs inside its cluster.
        ///
        ///   <para>A CONTAINER, so it carries on into its children even when an earlier file already owns
        ///   the element. A vehicle's extracts commonly declare one cluster path with different channel
        ///   contents in each; under the old skip-the-subtree rule every declaration after the first
        ///   contributed nothing, losing most of the cluster's triggerings. Where that happens the element stays the first
        ///   file's and its properties are the first file's, which is why the collision is REPORTED
        ///   (<see cref="ArxmlDiagnosticKind.RedeclaredCluster" />): merging two extracts' channels into one
        ///   network node is right when they describe one bus and lossy when they describe two, and this
        ///   reader cannot tell which.</para>
        /// </summary>
        private static void CollectCluster(String path, String shortName, XElement element,
            Collected collected, BusProtocol bus)
        {
            // The NETWORK is the CLUSTER and never the channel, still: a FlexRay cluster's channels A and
            // B are physical redundancy of one bus carrying one schedule, so a network per channel would
            // split a single bus into two that no ECU on it experiences as separate, and would double every
            // frame. What changed is that the channel is now an ELEMENT UNDER the network rather than
            // something walked through - because on Ethernet a channel is a VLAN, and which one an ECU sits
            // on is a question the graph has to be able to answer.
            var channels = new List<XElement>();
            foreach (var channel in Descendants(element, n => n == bus.ChannelElement))
            {
                channels.Add(channel);
            }

            var network = new ArxmlElement(path, ArxmlKinds.Network)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.Protocol] = bus.Protocol,
                // Protocol-NEUTRAL: the standard carries these on the cluster conditional of every
                // protocol, so they are read once here rather than per protocol.
                [ArxmlProperties.Baudrate] = First(element, "BAUDRATE"),
                [ArxmlProperties.ProtocolName] = First(element, "PROTOCOL-NAME"),
                [ArxmlProperties.ProtocolVersion] = First(element, "PROTOCOL-VERSION"),
            };

            if (String.Equals(bus.Protocol, ArxmlProperties.CanProtocol, StringComparison.Ordinal))
            {
                // Present on a CAN bus that runs CAN FD and absent otherwise, which is the fact rather
                // than a default: a bus with no FD baudrate is a classic CAN bus.
                network[ArxmlProperties.CanFdBaudrate] = First(element, "CAN-FD-BAUDRATE");
            }

            var claim = collected.Claim(network, collected.Networks);
            if (claim == PathClaim.DuplicateInThisFile)
            {
                return;
            }

            if (claim == PathClaim.SharedWithEarlierFile)
            {
                collected.ClusterRedeclared(path);
            }

            foreach (var channel in channels)
            {
                var channelName = Text(channel.Element(Ar + ShortNameElement));
                if (channelName == null)
                {
                    continue;
                }

                var channelPath = path + "/" + channelName;

                // One element per channel, claimed like any other: a cluster's VARIANTS each repeat the
                // same physical channels, and the claim is what makes the second declaration of one channel
                // the same element rather than a duplicate. (This is what the removed channelCount had to
                // count by distinct short name to get right.)
                var channelElement = new ArxmlElement(channelPath, ArxmlKinds.Channel)
                {
                    [ArxmlProperties.Name] = channelName,
                    // Denormalised from the network so a channel can be filtered on its own. A query for
                    // "every VLAN in this vehicle" should not have to join to the cluster to find out that
                    // these channels are the Ethernet ones.
                    [ArxmlProperties.Protocol] = bus.Protocol,
                    [ArxmlProperties.VlanId] = First(channel, "VLAN-IDENTIFIER"),
                    [ArxmlProperties.VlanName] = VlanNameOf(channel),
                };

                if (collected.Claim(channelElement, collected.Channels) == PathClaim.Recorded)
                {
                    collected.Pending.Add(new Pending(channelPath, ArxmlRelations.PartOf, path));
                }

                foreach (var reference in Descendants(channel, n => n == "COMMUNICATION-CONNECTOR-REF"))
                {
                    var connector = Clean(reference.Value);
                    if (connector != null)
                    {
                        // BOTH, and see ArxmlRelations.AttachedTo for why: the network edge is the
                        // protocol-neutral "on this bus", the channel edge is the broadcast domain.
                        collected.AttachConnector(path, connector);
                        collected.AttachConnector(channelPath, connector);
                    }
                }

                if (bus.HasFrames)
                {
                    foreach (var triggering in Descendants(channel,
                                 n => n == bus.FrameTriggeringElement))
                    {
                        CollectFrameTriggering(triggering, collected, bus);
                    }
                }

                if (bus.HasSocketLayer)
                {
                    CollectSocketLayer(channelPath, channel, collected);
                }

                foreach (var triggering in Descendants(channel, n => n == "I-SIGNAL-TRIGGERING"))
                {
                    var signalRef = Text(triggering.Element(Ar + "I-SIGNAL-REF"));
                    if (signalRef == null)
                    {
                        continue;
                    }

                    foreach (var portRef in Descendants(triggering, n => n == "I-SIGNAL-PORT-REF"))
                    {
                        var port = Clean(portRef.Value);
                        if (port != null)
                        {
                            collected.Flow(signalRef, port);
                        }
                    }
                }

                foreach (var triggering in Descendants(channel, n => n == "PDU-TRIGGERING"))
                {
                    var triggeringName = Text(triggering.Element(Ar + ShortNameElement));
                    var pduRef = Text(triggering.Element(Ar + "I-PDU-REF"));
                    if (triggeringName == null || pduRef == null)
                    {
                        continue;
                    }

                    var triggeringPath = channelPath + "/" + triggeringName;
                    if (!collected.PduTriggerings.ContainsKey(triggeringPath))
                    {
                        collected.PduTriggerings[triggeringPath] = pduRef;
                    }

                    // The PDU's own flow, which neither protocol read until now: a PDU triggering names
                    // the ports it crosses exactly as a frame triggering does. It is the only flow an
                    // Ethernet cluster will ever have, since Ethernet carries no frame layer at all.
                    foreach (var portRef in Descendants(triggering, n => n == "I-PDU-PORT-REF"))
                    {
                        var port = Clean(portRef.Value);
                        if (port != null)
                        {
                            collected.Flow(pduRef, port);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///   THE ETHERNET SOCKET LAYER: what addresses a PDU where the other protocols use a frame.
        ///
        ///   <para>Three kinds come out of it - an <c>endpoint</c> (an address on this channel), a
        ///   <c>socket</c> (a port over UDP or TCP, bound to one) and a <c>connection</c> (the pairing that
        ///   carries PDUs). NORMALISED onto those three, which is the one place this reader deliberately does
        ///   not mirror the standard: the socket layer is spelled differently by different AUTOSAR revisions
        ///   with NO overlap, so a faithful mirror of each would make the graph's shape a function of which
        ///   revision an extract was written against. Each element keeps the source's own element name in
        ///   <see cref="ArxmlProperties.SourceSpelling" />, so nothing is hidden.</para>
        ///
        ///   <para>Both spellings are read UNCONDITIONALLY, and no revision detection decides between them.
        ///   That follows from the no-overlap fact: a document can only be using one of them, so reading for
        ///   both is unambiguous, and a detector would be a second thing to get wrong for no gain. What the
        ///   file DECLARED is still captured, and appears in the diagnostic below where it is actionable.</para>
        ///
        ///   <para>Paths are composed from each element's OWN ancestors rather than from the channel
        ///   (<see cref="PathWithin" />), because the revisions nest these at different depths - one puts a
        ///   connection under the channel, another under a socket address - and a path composed from the
        ///   wrong depth is a wrong IDENTITY, which is the one error here that cannot be noticed downstream.</para>
        /// </summary>
        private static void CollectSocketLayer(String channelPath, XElement channel, Collected collected)
        {
            var found = 0;

            foreach (var endpoint in Descendants(channel, n => n == "NETWORK-ENDPOINT"))
            {
                var path = PathWithin(channel, channelPath, endpoint);
                if (path == null)
                {
                    continue;
                }

                found++;
                var element = new ArxmlElement(path, ArxmlKinds.Endpoint)
                {
                    [ArxmlProperties.Name] = Text(endpoint.Element(Ar + ShortNameElement)),
                };
                AddressOf(endpoint, element);
                Describe(element, endpoint);

                if (collected.Claim(element, collected.Endpoints) == PathClaim.Recorded)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.PartOf, channelPath));
                }
            }

            foreach (var socket in Descendants(channel, n => n == "SOCKET-ADDRESS"))
            {
                var path = PathWithin(channel, channelPath, socket);
                if (path == null)
                {
                    continue;
                }

                found++;
                var element = new ArxmlElement(path, ArxmlKinds.Socket)
                {
                    [ArxmlProperties.Name] = Text(socket.Element(Ar + ShortNameElement)),
                };
                PortOf(socket, element);

                if (collected.Claim(element, collected.Sockets) != PathClaim.Recorded)
                {
                    continue;
                }

                collected.Pending.Add(new Pending(path, ArxmlRelations.PartOf, channelPath));

                // The address it uses. A REF and not a nesting: several sockets on one endpoint is the
                // ordinary case, since one address serves every port on it.
                var endpointRef = Clean(First(socket, "NETWORK-ENDPOINT-REF"));
                if (endpointRef != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.BoundTo, endpointRef));
                }
            }

            foreach (var connection in Descendants(channel, n => ConnectionElements.Contains(n)))
            {
                var path = PathWithin(channel, channelPath, connection);
                if (path == null)
                {
                    continue;
                }

                found++;
                CollectConnection(channelPath, path, connection, collected);
            }

            if (found == 0)
            {
                collected.SocketLayerUnrecognised(channelPath, VocabularyUnder(channel));
            }
        }

        /// <summary>One socket connection, whichever of the two spellings it arrived in.</summary>
        private static void CollectConnection(String channelPath, String path, XElement connection,
            Collected collected)
        {
            var headerIds = 0;
            foreach (var identifier in Descendants(connection, n => PduIdentifierElements.Contains(n)))
            {
                if (First(identifier, "HEADER-ID") != null)
                {
                    headerIds++;
                }
            }

            var element = new ArxmlElement(path, ArxmlKinds.Connection)
            {
                [ArxmlProperties.Name] = Text(connection.Element(Ar + ShortNameElement)),
                // The source's own word, so a normalised element still says what the file called it.
                [ArxmlProperties.SourceSpelling] = connection.Name.LocalName,
                [ArxmlProperties.HeaderIdCount] = headerIds == 0
                    ? null
                    : headerIds.ToString(CultureInfo.InvariantCulture),
            };

            if (collected.Claim(element, collected.Connections) != PathClaim.Recorded)
            {
                // A LEAF, so an already-owned path means stop: the work below records this element's own
                // references under the same path and would hand them to the surviving twin.
                return;
            }

            collected.Pending.Add(new Pending(path, ArxmlRelations.PartOf, channelPath));

            // THE PDUS IT CARRIES, through the triggering: a PDU identifier points at a PDU TRIGGERING and
            // never at a PDU, exactly as a container PDU's contained references do, so it resolves through
            // the same indirection.
            foreach (var identifier in Descendants(connection, n => PduIdentifierElements.Contains(n)))
            {
                var triggering = Clean(First(identifier, "PDU-TRIGGERING-REF"));
                if (triggering != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Carries, triggering,
                        Indirection.Triggering));
                }
            }

            // THE TWO ENDS. Classified by the reference's own NAME rather than by where it sits, because
            // that is what the two revisions agree on: one names a server port and bundles client ports
            // under it, the other hangs the connection off the serving socket and names remote addresses.
            // Anything that is not the server side is a client side, which is the standard's own dichotomy.
            foreach (var reference in Descendants(connection, n => PortReferenceElements.Contains(n)))
            {
                var port = Clean(reference.Value);
                if (port == null)
                {
                    continue;
                }

                var type = reference.Name.LocalName.Contains("SERVER", StringComparison.Ordinal)
                    ? ArxmlRelations.ServerPort
                    : ArxmlRelations.ClientPort;
                collected.Pending.Add(new Pending(path, type, port, Indirection.SocketPort));
            }

            // The newer spelling states no server port at all: the connection HANGS OFF the serving socket,
            // so the parent is the server. Emitted only when the file named none, so the two spellings
            // produce one shape without either being guessed at.
            var named = false;
            foreach (var reference in Descendants(connection, n => PortReferenceElements.Contains(n)))
            {
                if (reference.Name.LocalName.Contains("SERVER", StringComparison.Ordinal))
                {
                    named = true;
                    break;
                }
            }

            if (!named && Parent(path) is { } parent && !String.Equals(parent, channelPath,
                    StringComparison.Ordinal))
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.ServerPort, parent,
                    Indirection.SocketPort));
            }
        }

        /// <summary>
        ///   A network endpoint's address, whichever IP version it carries. Both are read, the version is
        ///   recorded, and the mask and prefix land on ONE property: they answer the same question and a
        ///   query that had to know the version to ask it would be written twice for no reason.
        /// </summary>
        private static void AddressOf(XElement endpoint, ArxmlElement element)
        {
            foreach (var configuration in Descendants(endpoint, n => n == "IPV-4-CONFIGURATION"))
            {
                element[ArxmlProperties.IpVersion] = "ipv4";
                element[ArxmlProperties.Address] = First(configuration, "IPV-4-ADDRESS");
                element[ArxmlProperties.AddressSource] = First(configuration, "IPV-4-ADDRESS-SOURCE");
                element[ArxmlProperties.NetworkMask] = First(configuration, "NETWORK-MASK");
                return;
            }

            foreach (var configuration in Descendants(endpoint, n => n == "IPV-6-CONFIGURATION"))
            {
                element[ArxmlProperties.IpVersion] = "ipv6";
                element[ArxmlProperties.Address] = First(configuration, "IPV-6-ADDRESS");
                element[ArxmlProperties.AddressSource] = First(configuration, "IPV-6-ADDRESS-SOURCE");
                element[ArxmlProperties.NetworkMask] = First(configuration, "ADDRESS-PREFIX");
                return;
            }
        }

        /// <summary>
        ///   A socket's port and transport, from the application endpoint's transport configuration. The
        ///   TRANSPORT is why this is not one lookup: UDP and TCP are different elements rather than a value,
        ///   so which one is present IS the answer, and the port number sits inside whichever it is.
        /// </summary>
        private static void PortOf(XElement socket, ArxmlElement element)
        {
            foreach (var transport in Descendants(socket, n => n == "UDP-TP" || n == "TCP-TP"))
            {
                element[ArxmlProperties.Transport] =
                    String.Equals(transport.Name.LocalName, "UDP-TP", StringComparison.Ordinal)
                        ? "udp"
                        : "tcp";
                element[ArxmlProperties.Port] = First(transport, "PORT-NUMBER")
                                                ?? First(transport, "DYNAMICALLY-ASSIGNED");
                return;
            }
        }

        /// <summary>
        ///   The reference path of an element inside a materialised subtree, composed the way the streaming
        ///   pass composes one: this element's short name, preceded by every named ancestor up to the
        ///   subtree's root, whose own path is <paramref name="rootPath" />. Null when the element has no
        ///   short name, which in the standard's terms means it has no identity to be referenced by.
        /// </summary>
        private static String? PathWithin(XElement root, String rootPath, XElement element)
        {
            var own = Text(element.Element(Ar + ShortNameElement));
            if (own == null)
            {
                return null;
            }

            var segments = new List<String> { own };
            for (var ancestor = element.Parent; ancestor != null && ancestor != root;
                 ancestor = ancestor.Parent)
            {
                var name = Text(ancestor.Element(Ar + ShortNameElement));
                if (name != null)
                {
                    segments.Add(name);
                }
            }

            var path = new StringBuilder(rootPath);
            for (var i = segments.Count - 1; i >= 0; i--)
            {
                path.Append('/').Append(segments[i]);
            }

            return path.ToString();
        }

        /// <summary>
        ///   The distinct element names under a channel, in first-seen order and BOUNDED - the actionable
        ///   half of the socket-layer diagnostic. A maintainer reading "this channel contains
        ///   SOME-OTHER-SPELLING" can add a table entry; a maintainer reading "nothing was found" cannot do
        ///   anything at all.
        /// </summary>
        private static IReadOnlyList<String> VocabularyUnder(XElement channel)
        {
            const Int32 most = 12;
            var names = new List<String>();
            var seen = new HashSet<String>(StringComparer.Ordinal);
            foreach (var descendant in channel.Descendants())
            {
                var name = descendant.Name.LocalName;
                if (name.EndsWith("-REF", StringComparison.Ordinal) ||
                    String.Equals(name, ShortNameElement, StringComparison.Ordinal))
                {
                    // References and short names say nothing about which VOCABULARY this is, and there are
                    // hundreds of them: they would fill the bound with noise.
                    continue;
                }

                if (seen.Add(name))
                {
                    names.Add(name);
                    if (names.Count == most)
                    {
                        break;
                    }
                }
            }

            return names;
        }

        /// <summary>
        ///   What a frame's triggering says about the frame, denormalised onto it.
        ///
        ///   <para>Onto the FRAME, because a frame's slot or identifier is the fact an engineer asks for and
        ///   the triggering is the standard's indirection rather than a thing anybody names. The two
        ///   protocols put it in different places: FlexRay nests a schedule element, CAN carries the
        ///   identifier and addressing mode directly on the triggering.</para>
        ///
        ///   <para>FIRST DECLARATION WINS in both cases, guarded once at the top. A frame can be triggered
        ///   more than once (two slots, a slot per cycle, or the same frame triggered by two extracts of
        ///   one bus), and the alternative is a set of fields assembled from different triggerings that
        ///   describes a transmission appearing nowhere in any file.</para>
        /// </summary>
        private static void CollectFrameTriggering(XElement triggering, Collected collected, BusProtocol bus)
        {
            var frameRef = Text(triggering.Element(Ar + "FRAME-REF"));
            if (frameRef == null)
            {
                return;
            }

            foreach (var portRef in Descendants(triggering, n => n == "FRAME-PORT-REF"))
            {
                var port = Clean(portRef.Value);
                if (port != null)
                {
                    collected.Flow(frameRef, port);
                }
            }

            if (collected.FrameFacts.ContainsKey(frameRef))
            {
                return;
            }

            if (String.Equals(bus.Protocol, ArxmlProperties.CanProtocol, StringComparison.Ordinal))
            {
                // Directly on the triggering, and read from the triggering ELEMENT rather than by
                // descending, so a nested triggering reference cannot contribute somebody else's id.
                var canId = Text(triggering.Element(Ar + "IDENTIFIER"));
                var addressing = Text(triggering.Element(Ar + "CAN-ADDRESSING-MODE"));
                if (canId != null || addressing != null)
                {
                    collected.FrameFacts[frameRef] = TriggeringFacts.Can(canId, addressing);
                }

                return;
            }

            foreach (var timing in Descendants(triggering,
                         n => n == "FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING"))
            {
                var slot = First(timing, "SLOT-ID");
                var baseCycle = First(timing, "BASE-CYCLE");
                String? repetition = null;
                foreach (var candidate in Descendants(timing, n => n == "CYCLE-REPETITION"))
                {
                    var value = Clean(candidate.Value);
                    if (value != null && value.StartsWith("CYCLE-REPETITION", StringComparison.Ordinal))
                    {
                        repetition = value;
                        break;
                    }
                }

                if (slot != null || baseCycle != null || repetition != null)
                {
                    collected.FrameFacts[frameRef] =
                        TriggeringFacts.FlexRay(slot, baseCycle, repetition);
                }

                // Only the first timing is read at all, so the three fields always describe one
                // scheduled transmission.
                break;
            }
        }

        /// <summary>
        ///   A frame. Identical on both protocols, which is the measured basis for one <c>frame</c> kind
        ///   rather than one per bus: a CAN frame carries the same children as a FlexRay one.
        /// </summary>
        private static void CollectFrame(String path, String shortName, XElement element,
            Collected collected, BusProtocol bus)
        {
            if (collected.Claim(new ArxmlElement(path, ArxmlKinds.Frame)
                {
                    [ArxmlProperties.Name] = shortName,
                    [ArxmlProperties.FrameLengthBytes] = Text(element.Element(Ar + "FRAME-LENGTH")),
                }, collected.Frames) != PathClaim.Recorded)
            {
                // A LEAF, so an already-owned path still means skip: the work below records this element's
                // own references keyed by its path, and repeating it would give the surviving twin this
                // one's edges.
                return;
            }

            foreach (var mapping in Descendants(element, n => n == "PDU-TO-FRAME-MAPPING"))
            {
                var pduRef = Text(mapping.Element(Ar + "PDU-REF"));
                if (pduRef != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Contains, pduRef));
                }
            }
        }

        private static void CollectPdu(String name, String path, String shortName, XElement element,
            Collected collected)
        {
            var pdu = new ArxmlElement(path, ArxmlKinds.Pdu)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.PduKind] = name,
                [ArxmlProperties.LengthBytes] = Text(element.Element(Ar + "LENGTH")),
            };
            Describe(pdu, element);
            if (collected.Claim(pdu, collected.Pdus) != PathClaim.Recorded)
            {
                return;
            }

            foreach (var mapping in Descendants(element, n => n == "I-SIGNAL-TO-I-PDU-MAPPING"))
            {
                var signalRef = Text(mapping.Element(Ar + "I-SIGNAL-REF"));
                if (signalRef != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Contains, signalRef));
                }
            }

            // A container and a secured PDU both point at a TRIGGERING rather than at a PDU, so these two
            // resolve through the channel's triggering map instead of the element table.
            foreach (var reference in Descendants(element, n => n == "CONTAINED-PDU-TRIGGERING-REF"))
            {
                var contained = Clean(reference.Value);
                if (contained != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Carries, contained, Indirection.Triggering));
                }
            }

            // A secured PDU's PAYLOAD-REF carries DEST="PDU-TRIGGERING", so it resolves the same way.
            var payload = Text(element.Element(Ar + "PAYLOAD-REF"));
            if (payload != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.Secures, payload, Indirection.Triggering));
            }
        }

        private static void CollectSignal(String path, String shortName, XElement element,
            Collected collected)
        {
            var signal = new ArxmlElement(path, ArxmlKinds.Signal)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.LengthBits] = Text(element.Element(Ar + "LENGTH")),
                [ArxmlProperties.InitValue] = FirstUnder(element, "INIT-VALUE", "VALUE"),
                [ArxmlProperties.BaseType] = LastSegment(First(element, "BASE-TYPE-REF")),
            };
            Describe(signal, element);
            if (collected.Claim(signal, collected.Signals) != PathClaim.Recorded)
            {
                return;
            }

            var systemSignalRef = Text(element.Element(Ar + "SYSTEM-SIGNAL-REF"));
            if (systemSignalRef != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.Implements, systemSignalRef));
                collected.SignalToSystemSignal[path] = systemSignalRef;
            }
        }

        private static void CollectSystemSignal(String path, String shortName, XElement element,
            Collected collected)
        {
            var systemSignal = new ArxmlElement(path, ArxmlKinds.SystemSignal)
            {
                [ArxmlProperties.Name] = shortName,
            };
            Describe(systemSignal, element);
            if (collected.Claim(systemSignal, collected.SystemSignals) != PathClaim.Recorded)
            {
                return;
            }

            var compuRef = First(element, "COMPU-METHOD-REF");
            if (compuRef != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.ScaledBy, compuRef));
                collected.SystemSignalToCompuMethod[path] = compuRef;
            }
        }

        private static void CollectCompuMethod(String path, String shortName, XElement element,
            Collected collected)
        {
            if (collected.Claim(new ArxmlElement(path, ArxmlKinds.CompuMethod)
                {
                    [ArxmlProperties.Name] = shortName,
                    [ArxmlProperties.Category] = Text(element.Element(Ar + "CATEGORY")),
                }, collected.CompuMethods) != PathClaim.Recorded)
            {
                return;
            }

            var unitRef = First(element, "UNIT-REF");
            if (unitRef != null)
            {
                collected.CompuMethodToUnit[path] = unitRef;
            }
        }

        /// <summary>
        ///   Stage two: everything that needed the whole file. References resolve by exact path equality, and
        ///   one that names nothing becomes a diagnostic rather than a dropped edge nobody hears about.
        /// </summary>
        private static ArxmlNetwork Resolve(Collected collected)
        {
            var network = new ArxmlNetwork();
            network.Diagnostics.AddRange(collected.Diagnostics);

            // Units first: a compu method's unit is what a signal ends up carrying.
            var unitOfCompuMethod = new Dictionary<String, String>(StringComparer.Ordinal);
            foreach (var pair in collected.CompuMethodToUnit)
            {
                String? display;
                if (collected.UnitDisplayNames.TryGetValue(pair.Value, out var found))
                {
                    display = found;
                }
                else
                {
                    // The unit is not in the file. Its short name is still the best available label, so it
                    // is used, but the substitution is REPORTED: silently showing "UNIT_KM" where every
                    // other signal shows "km" would look like data rather than like a missing package,
                    // and it is the semantic payload that degrades.
                    display = LastSegment(pair.Value);
                    network.Diagnostics.Add(new ArxmlDiagnostic(
                        ArxmlDiagnosticKind.UnresolvedReference,
                        "What was read names a unit that nothing in it defines, so the unit's short name was " +
                        "used as its label instead of the display name a person would recognise. The usual " +
                        "cause is a partial export that left the unit package out, or a job that left out " +
                        "the extract carrying it.",
                        pair.Value));
                }

                if (display != null)
                {
                    unitOfCompuMethod[pair.Key] = display;
                    if (collected.CompuMethods.TryGetValue(pair.Key, out var compuMethod))
                    {
                        compuMethod[ArxmlProperties.Unit] = display;
                    }
                }
            }

            // The unit is DENORMALISED onto the signal, two hops down its own chain. It is the one derived
            // property here, and it exists for one reason: an odometer's description says "accumulated
            // distance" and never "kilometer", so without its unit on the signal a semantic query for a
            // distance in kilometers cannot reach it.
            foreach (var pair in collected.SignalToSystemSignal)
            {
                if (collected.SystemSignalToCompuMethod.TryGetValue(pair.Value, out var compuPath) &&
                    unitOfCompuMethod.TryGetValue(compuPath, out var unit) &&
                    collected.Signals.TryGetValue(pair.Key, out var signal))
                {
                    signal[ArxmlProperties.Unit] = unit;
                }
            }

            foreach (var pair in collected.FrameFacts)
            {
                if (collected.Frames.TryGetValue(pair.Key, out var frame))
                {
                    // Each protocol writes only its own fields, so a CAN frame carries no slot and a
                    // FlexRay frame no identifier. A null assignment is a no-op on ArxmlElement, so the
                    // absent ones never reach the snapshot as empty properties.
                    frame[ArxmlProperties.SlotId] = pair.Value.Slot;
                    frame[ArxmlProperties.BaseCycle] = pair.Value.BaseCycle;
                    frame[ArxmlProperties.CycleRepetition] = pair.Value.Repetition;
                    frame[ArxmlProperties.CanId] = pair.Value.CanId;
                    frame[ArxmlProperties.CanAddressingMode] = pair.Value.AddressingMode;
                }
                else
                {
                    network.Diagnostics.Add(Unresolved("a frame triggering's frame", pair.Key));
                }
            }

            foreach (var element in collected.Ordered())
            {
                network.Elements.Add(element);
            }

            // What the set carried that this version cannot read. Carried on the network AND reported: the
            // provider needs the list to say what it found when nothing readable turned up, and an operator
            // whose job DID import a bus still needs telling that another one went by unread, because the
            // snapshot is declared complete either way.
            foreach (var unread in collected.UnreadClusterKinds())
            {
                network.UnreadClusters.Add(unread);
                network.Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.UnreadCluster,
                    String.Format(CultureInfo.InvariantCulture,
                        "The set carries a {0} in {1} file(s), and this version does not read that bus. " +
                        "Everything under it was skipped, so its ECUs, frames and flow are absent, while " +
                        "any signals and PDUs those files declare outside it were still read. The run is " +
                        "reported as COMPLETE over what was read, so a later job that leaves these files " +
                        "out withdraws whatever only they described.", unread.Element, unread.Files),
                    unread.Element));
            }

            // An ECU is attached to a network through its CONNECTOR, which is not an entity: the connector
            // is the standard's join between an ECU and a channel, and describing it would add a node no
            // engineer asks a question about.
            foreach (var attachment in collected.ConnectorAttachments)
            {
                if (collected.ConnectorToEcu.TryGetValue(attachment.Right, out var ecuPath))
                {
                    Relate(network, ecuPath, ArxmlRelations.AttachedTo, attachment.Left);
                }
                else
                {
                    network.Diagnostics.Add(Unresolved("a channel's communication connector",
                        attachment.Right));
                }
            }

            // Flow. A port's DIRECTION decides which way the edge points, so a path query from a sender to a
            // receiver never has to traverse an edge backwards.
            foreach (var flow in collected.FlowByPort)
            {
                if (!collected.Ports.TryGetValue(flow.Right, out var direction))
                {
                    network.Diagnostics.Add(Unresolved("a triggering's port", flow.Right));
                    continue;
                }

                var ecuPath = EcuOfPort(collected, flow.Right);
                if (ecuPath == null)
                {
                    network.Diagnostics.Add(Unresolved("the ECU of a port", flow.Right));
                    continue;
                }

                if (String.Equals(direction, "OUT", StringComparison.Ordinal))
                {
                    Relate(network, ecuPath, ArxmlRelations.Sends, flow.Left);
                }
                else if (String.Equals(direction, "IN", StringComparison.Ordinal))
                {
                    Relate(network, flow.Left, ArxmlRelations.DeliversTo, ecuPath);
                }
                else
                {
                    // Neither word. The edge is DROPPED and named rather than pointed by a guess:
                    // guessing makes a receiver look like a sender, and a wrong edge answers an impact
                    // query confidently while a missing one at least shows up as a gap.
                    network.Diagnostics.Add(new ArxmlDiagnostic(
                        ArxmlDiagnosticKind.UndecidablePortDirection,
                        String.Format(CultureInfo.InvariantCulture,
                            "This port declares the communication direction '{0}', which is neither IN nor " +
                            "OUT, so which way the data flows through it cannot be decided and the edge was " +
                            "dropped. A direction is how this reader tells a sender from a receiver.",
                            direction.Length == 0 ? "(none)" : direction),
                        flow.Right));
                }
            }

            foreach (var pending in collected.Pending)
            {
                String? target;
                switch (pending.Through)
                {
                    case Indirection.Triggering:
                        target = Lookup(collected.PduTriggerings, pending.ToReference);
                        if (target == null)
                        {
                            network.Diagnostics.Add(Unresolved("a PDU triggering", pending.ToReference));
                            continue;
                        }

                        break;

                    case Indirection.SocketPort:
                        // The path as written, else its parent: a port reference names either the socket
                        // address or the application endpoint inside it, and both mean the socket. Checked
                        // against the SOCKETS rather than every element, so a reference that happens to
                        // have a parent path cannot resolve onto something that is not a socket.
                        target = collected.Sockets.ContainsKey(pending.ToReference)
                            ? pending.ToReference
                            : Parent(pending.ToReference) is { } parent
                              && collected.Sockets.ContainsKey(parent)
                                ? parent
                                : null;
                        if (target == null)
                        {
                            network.Diagnostics.Add(Unresolved("a socket connection's port",
                                pending.ToReference));
                            continue;
                        }

                        break;

                    default:
                        target = pending.ToReference;
                        break;
                }

                Relate(network, pending.FromPath, pending.Type, target);
            }

            // Every relation is checked against the element table, so a file that points at something it
            // does not define costs one named diagnostic and one edge rather than a wrong edge.
            var known = new HashSet<String>(StringComparer.Ordinal);
            foreach (var element in network.Elements)
            {
                known.Add(element.Path);
            }

            var kept = new List<ArxmlRelation>(network.Relations.Count);
            var seen = new HashSet<String>(StringComparer.Ordinal);
            foreach (var relation in network.Relations)
            {
                if (!known.Contains(relation.FromPath))
                {
                    network.Diagnostics.Add(Unresolved("a relation's source", relation.FromPath));
                    continue;
                }

                if (!known.Contains(relation.ToPath))
                {
                    network.Diagnostics.Add(Unresolved("a relation's target", relation.ToPath));
                    continue;
                }

                // One signal mapped at several byte positions of one PDU is normal and says nothing new, so
                // the repeat is dropped silently rather than reported: a diagnostic per repeat would bury
                // the ones that mean something.
                if (seen.Add(relation.FromPath + "\0" + relation.Type + "\0" + relation.ToPath))
                {
                    kept.Add(relation);
                }
            }

            network.Relations.Clear();
            network.Relations.AddRange(kept);
            return network;
        }

        private static void Relate(ArxmlNetwork network, String from, String type, String to)
        {
            network.Relations.Add(new ArxmlRelation(from, type, to));
        }

        private static ArxmlDiagnostic Unresolved(String what, String reference)
        {
            return new ArxmlDiagnostic(ArxmlDiagnosticKind.UnresolvedReference, String.Format(
                CultureInfo.InvariantCulture,
                "What was read names {0} that nothing in it defines, so what pointed at it was dropped. The " +
                "usual cause is a partial export - an extract that references a package it did not include - " +
                "or, where a job carries several extracts, one the job left out.", what),
                reference);
        }

        private static String? EcuOfPort(Collected collected, String portPath)
        {
            var connector = Parent(portPath);
            return connector != null && collected.ConnectorToEcu.TryGetValue(connector, out var ecu)
                ? ecu
                : null;
        }

        private static void Describe(ArxmlElement element, XElement source)
        {
            var description = source.Element(Ar + "DESC");
            if (description == null)
            {
                return;
            }

            String? languageNeutral = null;

            foreach (var variant in description.Elements(Ar + "L-2"))
            {
                var language = (String?)variant.Attribute("L");
                if (String.Equals(language, "DE", StringComparison.Ordinal))
                {
                    element[ArxmlProperties.DescriptionDe] = Clean(variant.Value);
                }
                else if (String.Equals(language, "EN", StringComparison.Ordinal))
                {
                    element[ArxmlProperties.DescriptionEn] = Clean(variant.Value);
                }
                else if (String.Equals(language, "FOR-ALL", StringComparison.Ordinal))
                {
                    // The standard's LANGUAGE-NEUTRAL variant: text meant for every locale. Kept as a
                    // fallback rather than a third property, because an element described only this way
                    // would otherwise carry no prose at all and drop out of every semantic query.
                    languageNeutral = Clean(variant.Value);
                }
            }

            if (languageNeutral != null)
            {
                if (element[ArxmlProperties.DescriptionEn] == null)
                {
                    element[ArxmlProperties.DescriptionEn] = languageNeutral;
                }

                if (element[ArxmlProperties.DescriptionDe] == null)
                {
                    element[ArxmlProperties.DescriptionDe] = languageNeutral;
                }
            }
        }

        private static IEnumerable<XElement> Descendants(XElement element, Func<String, Boolean> matches)
        {
            foreach (var candidate in element.Descendants())
            {
                if (matches(candidate.Name.LocalName))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        ///   The name of the VLAN an Ethernet channel carries, which the standard puts on a VLAN element
        ///   INSIDE the channel rather than as a field on it - so it is that element's short name, not a
        ///   value. Null on CAN and FlexRay, where there is no VLAN element to find, which is exactly the
        ///   "absent means not applicable" the property documents.
        /// </summary>
        private static String? VlanNameOf(XElement channel)
        {
            foreach (var vlan in Descendants(channel, n => n == "VLAN"))
            {
                var name = Text(vlan.Element(Ar + ShortNameElement));
                if (name != null)
                {
                    return name;
                }
            }

            return null;
        }

        private static String? First(XElement element, String localName)
        {
            foreach (var candidate in element.Descendants(Ar + localName))
            {
                return Clean(candidate.Value);
            }

            return null;
        }

        private static String? FirstUnder(XElement element, String outerName, String innerName)
        {
            foreach (var outer in element.Descendants(Ar + outerName))
            {
                foreach (var inner in outer.Descendants(Ar + innerName))
                {
                    return Clean(inner.Value);
                }
            }

            return null;
        }

        private static String? Text(XElement? element)
        {
            return element == null ? null : Clean(element.Value);
        }

        /// <summary>Trims, and treats a value that is nothing but whitespace as ABSENT: the presence rule
        /// the snapshot contract's <c>EntityDto</c> owns, applied where the file is read.</summary>
        private static String? Clean(String? value)
        {
            if (value == null)
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static String? LastSegment(String? path)
        {
            if (path == null)
            {
                return null;
            }

            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : Clean(path.Substring(slash + 1));
        }

        private static String? Parent(String path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? null : path.Substring(0, slash);
        }

        private static String? Lookup(Dictionary<String, String> map, String key)
        {
            return map.TryGetValue(key, out var value) ? value : null;
        }

        private static String PathOf(List<Frame> stack)
        {
            var path = new StringBuilder();
            foreach (var frame in stack)
            {
                if (frame.ShortName != null)
                {
                    path.Append('/').Append(frame.ShortName);
                }
            }

            return path.ToString();
        }

        private static HashSet<String> BuildInterestSet()
        {
            var set = new HashSet<String>(StringComparer.Ordinal)
            {
                "ECU-INSTANCE",
                "I-SIGNAL",
                "SYSTEM-SIGNAL",
                "COMPU-METHOD",
                "UNIT",
            };

            // Derived from the protocol table rather than listed again, so adding a protocol is adding one
            // table entry. A cluster element missing from the interest set is not a compile error and not a
            // failure either: the reader would simply never see that bus.
            foreach (var bus in BusProtocols)
            {
                set.Add(bus.ClusterElement);
                if (bus.FrameElement != null)
                {
                    set.Add(bus.FrameElement);
                }
            }

            // The cluster kinds this version does NOT read are in the interest set too, and that is the
            // whole mechanism behind naming them in a diagnostic: the reader materialises only what it is
            // interested in, so an unread bus is invisible unless it is asked for. They dispatch to nothing
            // in Collect, which is what makes them cost a short-name read and no more.
            foreach (var unread in UnreadClusterElements)
            {
                set.Add(unread);
            }

            foreach (var pdu in PduElements)
            {
                set.Add(pdu);
            }

            return set;
        }

        private static Dictionary<String, BusProtocol> BuildClusterElements()
        {
            var map = new Dictionary<String, BusProtocol>(StringComparer.Ordinal);
            foreach (var bus in BusProtocols)
            {
                map[bus.ClusterElement] = bus;
            }

            return map;
        }

        private static Dictionary<String, BusProtocol> BuildFrameElements()
        {
            var map = new Dictionary<String, BusProtocol>(StringComparer.Ordinal);
            foreach (var bus in BusProtocols)
            {
                if (bus.FrameElement != null)
                {
                    map[bus.FrameElement] = bus;
                }
            }

            return map;
        }

        /// <summary>One level of the short-name stack.</summary>
        private sealed class Frame
        {
            public Frame(String element)
            {
                Element = element;
            }

            public String Element { get; }

            public String? ShortName { get; set; }
        }

        /// <summary>
        ///   What one frame's triggering said about it. PROTOCOL-CONDITIONAL by construction: a FlexRay
        ///   triggering fills the schedule fields and a CAN one fills the identity fields, and neither
        ///   fills the other's, which is why every field is nullable and why the two named constructors
        ///   exist instead of one taking five arguments.
        /// </summary>
        private sealed class TriggeringFacts
        {
            private TriggeringFacts(String? slot, String? baseCycle, String? repetition, String? canId,
                String? addressingMode)
            {
                Slot = slot;
                BaseCycle = baseCycle;
                Repetition = repetition;
                CanId = canId;
                AddressingMode = addressingMode;
            }

            public String? Slot { get; }

            public String? BaseCycle { get; }

            public String? Repetition { get; }

            public String? CanId { get; }

            public String? AddressingMode { get; }

            public static TriggeringFacts FlexRay(String? slot, String? baseCycle, String? repetition)
            {
                return new TriggeringFacts(slot, baseCycle, repetition, null, null);
            }

            public static TriggeringFacts Can(String? canId, String? addressingMode)
            {
                return new TriggeringFacts(null, null, null, canId, addressingMode);
            }
        }

        private sealed class Pair
        {
            public Pair(String left, String right)
            {
                Left = left;
                Right = right;
            }

            public String Left { get; }

            public String Right { get; }
        }

        private sealed class Pending
        {
            public Pending(String fromPath, String type, String toReference,
                Indirection through = Indirection.None)
            {
                FromPath = fromPath;
                Type = type;
                ToReference = toReference;
                Through = through;
            }

            public String FromPath { get; }

            public String Type { get; }

            public String ToReference { get; }

            public Indirection Through { get; }
        }

        /// <summary>
        ///   What stands between a reference as the file writes it and the element it means.
        /// </summary>
        private enum Indirection
        {
            /// <summary>The reference IS the element's path. Most of them.</summary>
            None,

            /// <summary>
            ///   The reference names a PDU TRIGGERING, so the PDU is whatever that triggering triggers. The
            ///   standard's own indirection: a container PDU, a secured PDU's payload and a socket
            ///   connection's PDU identifiers all point at a triggering rather than at a PDU.
            /// </summary>
            Triggering,

            /// <summary>
            ///   The reference names a socket's PORT, which is either the socket address itself or the
            ///   application endpoint inside it depending on the revision and the construct. Resolved as
            ///   the path, falling back to its PARENT - which is the socket address when the path was the
            ///   application endpoint. Two spellings of one thing rather than a guess: both land on the
            ///   socket, and a reference that resolves to neither is reported like any other.
            /// </summary>
            SocketPort,
        }

        /// <summary>
        ///   Everything the streaming pass gathered, ACROSS EVERY DOCUMENT of one read. Elements are kept per
        ///   kind AND in one path table, so a repeated path is caught once rather than per kind, and the table
        ///   is shared by every document because that is what makes a cross-document reference resolve.
        /// </summary>
        private sealed class Collected
        {
            /// <summary>How many documents have been begun. Doubles as the current document's own id.</summary>
            private Int32 _documents;

            private String _documentName = String.Empty;

            private Int32 _redeclared;

            /// <summary>Unread bus kinds, in first-seen order, each with the files that declared one.</summary>
            private readonly List<String> _unreadOrder = new List<String>();

            private readonly Dictionary<String, HashSet<String>> _unreadFiles =
                new Dictionary<String, HashSet<String>>(StringComparer.Ordinal);

            /// <summary>Cluster paths already reported as re-declared, so the diagnostic is once per path.</summary>
            private readonly HashSet<String> _redeclaredClusters = new HashSet<String>(StringComparer.Ordinal);

            /// <summary>How many Ethernet channels had no socket layer at all. Reported once, on the first.</summary>
            private Int32 _socketLayerEmpty;

            /// <summary>
            ///   What the CURRENT document declares as its schema, from <c>xsi:schemaLocation</c> on the
            ///   document element. Captured because the XML NAMESPACE does not identify the AUTOSAR revision -
            ///   every revision of the classic platform uses the same one - so this is the only thing in the
            ///   file that says which it is. Nothing branches on it: it exists to make the socket-layer
            ///   diagnostic actionable, since "which revision is this" is the first question anybody chasing
            ///   an unrecognised spelling will ask.
            /// </summary>
            private String _schemaLocation = String.Empty;

            /// <summary>Attachments and flows already recorded, so the union cannot repeat a relation.</summary>
            private readonly HashSet<String> _attachmentsSeen = new HashSet<String>(StringComparer.Ordinal);

            private readonly HashSet<String> _flowSeen = new HashSet<String>(StringComparer.Ordinal);

            public Dictionary<String, ArxmlElement> Networks { get; } = New();

            public Dictionary<String, ArxmlElement> Channels { get; } = New();

            public Dictionary<String, ArxmlElement> Ecus { get; } = New();

            public Dictionary<String, ArxmlElement> Frames { get; } = New();

            public Dictionary<String, ArxmlElement> Pdus { get; } = New();

            public Dictionary<String, ArxmlElement> Signals { get; } = New();

            public Dictionary<String, ArxmlElement> SystemSignals { get; } = New();

            public Dictionary<String, ArxmlElement> CompuMethods { get; } = New();

            public Dictionary<String, ArxmlElement> Endpoints { get; } = New();

            public Dictionary<String, ArxmlElement> Sockets { get; } = New();

            public Dictionary<String, ArxmlElement> Connections { get; } = New();

            public Dictionary<String, String> UnitDisplayNames { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> Ports { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> ConnectorToEcu { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> PduTriggerings { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, TriggeringFacts> FrameFacts { get; } =
                new Dictionary<String, TriggeringFacts>(StringComparer.Ordinal);

            public Dictionary<String, String> SignalToSystemSignal { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> SystemSignalToCompuMethod { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            /// <summary>Compu method path to the UNIT path it references, resolved to a display name later.</summary>
            public Dictionary<String, String> CompuMethodToUnit { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public List<Pair> ConnectorAttachments { get; } = new List<Pair>();

            public List<Pair> FlowByPort { get; } = new List<Pair>();

            public List<Pending> Pending { get; } = new List<Pending>();

            public List<ArxmlDiagnostic> Diagnostics { get; } = new List<ArxmlDiagnostic>();

            /// <summary>How many documents this read has taken in.</summary>
            public Int32 Documents => _documents;

            private List<String> Order { get; } = new List<String>();

            /// <summary>
            ///   Each path taken, against the document that took it. The OWNER is what separates the two
            ///   duplicate cases, which are a fault and an expectation respectively and must not be reported
            ///   as one thing.
            /// </summary>
            private Dictionary<String, Int32> PathOwner { get; } =
                new Dictionary<String, Int32>(StringComparer.Ordinal);

            private Dictionary<String, ArxmlElement> All { get; } = New();

            /// <summary>Starts a document. Its name is what its own diagnostics name.</summary>
            /// <summary>What this document declared as its schema, for the diagnostics that need it.</summary>
            public void DeclaresSchema(String? schemaLocation)
            {
                _schemaLocation = schemaLocation == null ? String.Empty : schemaLocation.Trim();
            }

            public void BeginDocument(String fileName)
            {
                _documents++;
                _documentName = fileName;
                _redeclared = 0;
            }

            /// <summary>
            ///   Closes a document, reporting what it re-declared ONCE rather than per path. Reported here, at
            ///   the end of the document that did it, so a set's diagnostics come out in document order and two
            ///   runs over the same ordered set say the same thing in the same sequence.
            /// </summary>
            public void EndDocument()
            {
                if (_redeclared == 0)
                {
                    return;
                }

                Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.RedeclaredPaths,
                    String.Format(CultureInfo.InvariantCulture,
                        "An earlier file in the set already declared {0} of the reference paths this file " +
                        "declares, so those elements stayed the earlier file's and this file's twins were " +
                        "dropped along with the references they carried. This is the expected case rather " +
                        "than a fault - every extract of one system repeats the standard's shared packages - " +
                        "and it is reported ONCE for the file rather than once per path, because hundreds of " +
                        "entries would bury the diagnostics that mean something.", _redeclared),
                    _documentName));
            }

            /// <summary>
            ///   Offers an element for a reference path, and says what the caller should do next.
            ///
            ///   <para>The ELEMENT is always the first declaration's: a later file never overwrites one, so
            ///   which properties an element carries can never depend on the order the caller listed the
            ///   files. What changed when multi-bus input arrived is that a later file may still contribute
            ///   the container's CHILDREN, which is <see cref="PathClaim" />'s whole subject; the old rule
            ///   skipped the subtree and so attached a gateway ECU to one bus and lost two extracts' worth
            ///   of a shared cluster's channels.</para>
            ///
            ///   <para>WITHIN one document a repeat is still a fault and still named per path. ACROSS
            ///   documents it is counted for the aggregate <see cref="EndDocument"/> reports. Only what the
            ///   per-path diagnostic would have counted is counted, which is why a repeated UNIT (not an
            ///   element, and silently first-wins within a file too) appears in neither.</para>
            /// </summary>
            public PathClaim Claim(ArxmlElement element, Dictionary<String, ArxmlElement> byKind)
            {
                if (PathOwner.TryGetValue(element.Path, out var owner))
                {
                    if (owner == _documents)
                    {
                        Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.DuplicatePath,
                            "Two elements compose this same reference path, so only the first was described, " +
                            "and nothing the later one referenced was recorded either. One path is one thing " +
                            "in the standard's own terms, and keeping both would make which one wins depend " +
                            "on the order the file happens to be written in.",
                            element.Path));
                        return PathClaim.DuplicateInThisFile;
                    }

                    _redeclared++;
                    return PathClaim.SharedWithEarlierFile;
                }

                PathOwner[element.Path] = _documents;
                byKind[element.Path] = element;
                All[element.Path] = element;
                Order.Add(element.Path);
                return PathClaim.Recorded;
            }

            /// <summary>
            ///   Reports that a CLUSTER path was declared by more than one file, once per path however many
            ///   files pile onto it.
            ///
            ///   <para>Separate from the ordinary re-declaration count because the two are different facts.
            ///   A shared signal or compu-method path is the standard's catalogue appearing in every
            ///   extract, which is expected. A shared CLUSTER path means several extracts describe what this
            ///   reader will present as ONE bus, unioning their channels: right when they are one bus split
            ///   across extracts, lossy when they are two buses that happen to share a path, and nothing
            ///   here can tell which. So it is said out loud rather than counted with the ordinary ones.</para>
            /// </summary>
            public void ClusterRedeclared(String path)
            {
                if (!_redeclaredClusters.Add(path))
                {
                    return;
                }

                Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.RedeclaredCluster,
                    "More than one file in the set declares this CLUSTER, so what they say about it was " +
                    "MERGED into one network: the first file's own properties, and the channels, frames and " +
                    "attachments of all of them. That is what one bus split across several extracts needs. " +
                    "If these are really two different buses that happen to share a reference path, they " +
                    "are now one network in the graph and nothing downstream can separate them again - " +
                    "check the extracts describe the same bus.",
                    path));
            }

            /// <summary>
            ///   Notes that an Ethernet channel's SOCKET LAYER yielded nothing, and reports it ONCE for the
            ///   whole read - naming how many channels it happened to, the schema the first such file
            ///   declared, and the vocabulary actually present under the first of them.
            ///
            ///   <para>Aggregated rather than per channel because a channel may legitimately carry no socket
            ///   layer, and a backbone has a couple of dozen: one line per channel would bury every
            ///   diagnostic that means something. Aggregated on the FIRST occurrence's detail rather than the
            ///   union of all of them, because a reader chasing an unrecognised spelling needs one concrete
            ///   example, not a merged list from twenty channels that may not even agree.</para>
            /// </summary>
            public void SocketLayerUnrecognised(String channelPath, IReadOnlyList<String> vocabulary)
            {
                _socketLayerEmpty++;
                if (_socketLayerEmpty > 1)
                {
                    return;
                }

                Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.SocketLayerUnrecognised,
                    String.Format(CultureInfo.InvariantCulture,
                        "This Ethernet channel was read and its SOCKET LAYER yielded nothing: no network " +
                        "endpoint, no socket and no connection, so the PDUs on it have no addressing in the " +
                        "graph. Either the extract carries none - which is legal - or this reader does not " +
                        "know the spelling this one uses: the socket layer's element names differ between " +
                        "AUTOSAR revisions with no overlap. The file declares schema '{0}' and the channel " +
                        "contains: {1}. Everything else on the bus imported normally. If those names look " +
                        "like a socket layer, report them: it is a table entry, not a redesign.",
                        _schemaLocation.Length == 0 ? "(none stated)" : _schemaLocation,
                        vocabulary.Count == 0 ? "(nothing)" : String.Join(", ", vocabulary)),
                    channelPath));
            }

            /// <summary>
            ///   Notes that this file declared a bus of a kind this version does not read. Counted per KIND
            ///   and per FILE, so the report is "LIN, in 18 files" rather than one line per cluster.
            /// </summary>
            public void UnreadCluster(String element)
            {
                if (!_unreadFiles.TryGetValue(element, out var files))
                {
                    files = new HashSet<String>(StringComparer.Ordinal);
                    _unreadFiles[element] = files;
                    _unreadOrder.Add(element);
                }

                files.Add(_documentName);
            }

            /// <summary>The unread bus kinds, in first-seen order so a run stays reproducible.</summary>
            public IEnumerable<UnreadCluster> UnreadClusterKinds()
            {
                foreach (var element in _unreadOrder)
                {
                    yield return new UnreadCluster(element, _unreadFiles[element].Count);
                }
            }

            /// <summary>
            ///   An ECU connector attached to a bus, recorded once however many files say it.
            ///
            ///   <para>Deduped HERE and not downstream. Under the union rule two extracts of one bus both
            ///   name the same connector, and nothing between this and the graph removes a repeated
            ///   relation: <c>Relate</c> appends. An ordered list plus a seen-set rather than a set alone,
            ///   because emission order has to stay stable for the conformance suite's determinism check.</para>
            /// </summary>
            public void AttachConnector(String networkPath, String connectorRef)
            {
                if (_attachmentsSeen.Add(networkPath + "\u0000" + connectorRef))
                {
                    ConnectorAttachments.Add(new Pair(networkPath, connectorRef));
                }
            }

            /// <summary>
            ///   A thing that crosses a port, recorded once however many files or triggerings say it. Deduped
            ///   for the same reason as <see cref="AttachConnector" />.
            /// </summary>
            public void Flow(String reference, String portRef)
            {
                if (_flowSeen.Add(reference + "\u0000" + portRef))
                {
                    FlowByPort.Add(new Pair(reference, portRef));
                }
            }

            /// <summary>The elements in the order the files described them, which keeps a run reproducible.</summary>
            public IEnumerable<ArxmlElement> Ordered()
            {
                foreach (var path in Order)
                {
                    yield return All[path];
                }
            }

            private static Dictionary<String, ArxmlElement> New()
            {
                return new Dictionary<String, ArxmlElement>(StringComparer.Ordinal);
            }
        }
    }
}
