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
    ///   <see cref="Add"/>ed streams into ONE table and <see cref="Complete"/> resolves once over their
    ///   union: that, and nothing else, is what makes a reference from one extract into another resolve
    ///   exactly like a reference within one file. Order is part of the meaning - where two documents declare
    ///   one path, the earlier one owns it - so the caller's order is kept rather than sorted.</para>
    ///
    ///   <para>It streams. A system extract is routinely tens of megabytes of which the communication matrix
    ///   is a small fraction, so only the elements in the interest set are materialised as subtrees and
    ///   everything else advances the reader without allocating. One document at a time, too: a caller hands
    ///   over one text per <see cref="Add"/> and nothing here keeps it, which is what stops a set of
    ///   tens-of-megabytes extracts from being held decoded all at once.</para>
    /// </summary>
    public sealed class ArxmlReader
    {
        /// <summary>The one schema namespace this reader understands. It covers every AUTOSAR 4.x release.</summary>
        public const String Namespace = "http://autosar.org/schema/r4.0";

        /// <summary>The document element every system extract carries.</summary>
        public const String RootElement = "AUTOSAR";

        private static readonly XNamespace Ar = Namespace;

        private const String ShortNameElement = "SHORT-NAME";

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
        private static readonly HashSet<String> Interesting = BuildInterestSet();

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
            reader.Consume(String.Empty, xml);
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

            Consume(fileName, xml);
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
        /// </summary>
        private void Consume(String fileName, String xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }

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
                using (var text = new StringReader(xml))
                using (var reader = XmlReader.Create(text, settings))
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

            switch (name)
            {
                case "ECU-INSTANCE":
                    CollectEcu(path, shortName, element, collected);
                    break;
                case "FLEXRAY-CLUSTER":
                    CollectCluster(path, shortName, element, collected);
                    break;
                case "FLEXRAY-FRAME":
                    CollectFrame(path, shortName, element, collected);
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

        private static void CollectEcu(String path, String shortName, XElement element, Collected collected)
        {
            if (!collected.Add(new ArxmlElement(path, ArxmlKinds.Ecu) { [ArxmlProperties.Name] = shortName },
                    collected.Ecus))
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
                collected.ConnectorToEcu[connectorPath] = path;

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
                    collected.Ports[connectorPath + "/" + portName] =
                        Text(port.Element(Ar + "COMMUNICATION-DIRECTION")) ?? String.Empty;
                }
            }
        }

        private static void CollectCluster(String path, String shortName, XElement element,
            Collected collected)
        {
            // The NETWORK is the CLUSTER and never the channel. A FlexRay cluster's channels A and B are
            // physical redundancy of one bus carrying one schedule, so an element per channel would split a
            // single network into two that no ECU on it experiences as separate, and would double every
            // frame. The channel still matters internally, because a PDU triggering's path runs through it.
            // Counted by DISTINCT short name, not by element: a cluster's variants each repeat the same
            // physical channels, so counting elements would report a two-channel bus as having four.
            var channels = new List<XElement>();
            var channelNames = new HashSet<String>(StringComparer.Ordinal);
            foreach (var channel in Descendants(element, n => n == "FLEXRAY-PHYSICAL-CHANNEL"))
            {
                channels.Add(channel);
                var name = Text(channel.Element(Ar + ShortNameElement));
                if (name != null)
                {
                    channelNames.Add(name);
                }
            }

            if (!collected.Add(new ArxmlElement(path, ArxmlKinds.Network)
                {
                    [ArxmlProperties.Name] = shortName,
                    [ArxmlProperties.Protocol] = ArxmlProperties.FlexRayProtocol,
                    [ArxmlProperties.ChannelCount] =
                        channelNames.Count.ToString(CultureInfo.InvariantCulture),
                }, collected.Networks))
            {
                return;
            }

            foreach (var channel in channels)
            {
                var channelName = Text(channel.Element(Ar + ShortNameElement));
                if (channelName == null)
                {
                    continue;
                }

                var channelPath = path + "/" + channelName;

                foreach (var reference in Descendants(channel, n => n == "COMMUNICATION-CONNECTOR-REF"))
                {
                    var connector = Clean(reference.Value);
                    if (connector != null)
                    {
                        collected.ConnectorAttachments.Add(new Pair(path, connector));
                    }
                }

                foreach (var triggering in Descendants(channel, n => n == "FLEXRAY-FRAME-TRIGGERING"))
                {
                    CollectFrameTriggering(triggering, collected);
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
                            collected.FlowByPort.Add(new Pair(signalRef, port));
                        }
                    }
                }

                foreach (var triggering in Descendants(channel, n => n == "PDU-TRIGGERING"))
                {
                    var triggeringName = Text(triggering.Element(Ar + ShortNameElement));
                    var pduRef = Text(triggering.Element(Ar + "I-PDU-REF"));
                    if (triggeringName != null && pduRef != null)
                    {
                        collected.PduTriggerings[channelPath + "/" + triggeringName] = pduRef;
                    }
                }
            }
        }

        private static void CollectFrameTriggering(XElement triggering, Collected collected)
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
                    collected.FlowByPort.Add(new Pair(frameRef, port));
                }
            }

            // Schedule. Recorded against the FRAME, because a frame's slot is the fact an engineer asks for
            // and the triggering is the standard's indirection rather than a thing anybody names.
            //
            // Read from ONE timing element rather than by searching the triggering for each field
            // separately: a frame may be scheduled more than once (two slots, or a slot per cycle), and
            // independent searches would take the slot from the first timing and the cycle from whichever
            // happened to carry one, reporting a schedule that appears in the file nowhere.
            if (!collected.FrameSchedules.ContainsKey(frameRef))
            {
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
                        collected.FrameSchedules[frameRef] = new Schedule(slot, baseCycle, repetition);
                    }

                    // The first timing wins, and only the first is read at all, so the three fields
                    // always describe one scheduled transmission.
                    break;
                }
            }
        }

        private static void CollectFrame(String path, String shortName, XElement element,
            Collected collected)
        {
            if (!collected.Add(new ArxmlElement(path, ArxmlKinds.Frame)
                {
                    [ArxmlProperties.Name] = shortName,
                    [ArxmlProperties.FrameLengthBytes] = Text(element.Element(Ar + "FRAME-LENGTH")),
                }, collected.Frames))
            {
                return;
            }

            foreach (var mapping in Descendants(element, n => n == "PDU-TO-FRAME-MAPPING"))
            {
                var pduRef = Text(mapping.Element(Ar + "PDU-REF"));
                if (pduRef != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Contains, pduRef, false));
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
            if (!collected.Add(pdu, collected.Pdus))
            {
                return;
            }

            foreach (var mapping in Descendants(element, n => n == "I-SIGNAL-TO-I-PDU-MAPPING"))
            {
                var signalRef = Text(mapping.Element(Ar + "I-SIGNAL-REF"));
                if (signalRef != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Contains, signalRef, false));
                }
            }

            // A container and a secured PDU both point at a TRIGGERING rather than at a PDU, so these two
            // resolve through the channel's triggering map instead of the element table.
            foreach (var reference in Descendants(element, n => n == "CONTAINED-PDU-TRIGGERING-REF"))
            {
                var contained = Clean(reference.Value);
                if (contained != null)
                {
                    collected.Pending.Add(new Pending(path, ArxmlRelations.Carries, contained, true));
                }
            }

            // A secured PDU's PAYLOAD-REF carries DEST="PDU-TRIGGERING", so it resolves the same way.
            var payload = Text(element.Element(Ar + "PAYLOAD-REF"));
            if (payload != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.Secures, payload, true));
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
            if (!collected.Add(signal, collected.Signals))
            {
                return;
            }

            var systemSignalRef = Text(element.Element(Ar + "SYSTEM-SIGNAL-REF"));
            if (systemSignalRef != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.Implements, systemSignalRef, false));
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
            if (!collected.Add(systemSignal, collected.SystemSignals))
            {
                return;
            }

            var compuRef = First(element, "COMPU-METHOD-REF");
            if (compuRef != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.ScaledBy, compuRef, false));
                collected.SystemSignalToCompuMethod[path] = compuRef;
            }
        }

        private static void CollectCompuMethod(String path, String shortName, XElement element,
            Collected collected)
        {
            if (!collected.Add(new ArxmlElement(path, ArxmlKinds.CompuMethod)
                {
                    [ArxmlProperties.Name] = shortName,
                    [ArxmlProperties.Category] = Text(element.Element(Ar + "CATEGORY")),
                }, collected.CompuMethods))
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

            foreach (var pair in collected.FrameSchedules)
            {
                if (collected.Frames.TryGetValue(pair.Key, out var frame))
                {
                    frame[ArxmlProperties.SlotId] = pair.Value.Slot;
                    frame[ArxmlProperties.BaseCycle] = pair.Value.BaseCycle;
                    frame[ArxmlProperties.CycleRepetition] = pair.Value.Repetition;
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
                var target = pending.ThroughTriggering
                    ? Lookup(collected.PduTriggerings, pending.ToReference)
                    : pending.ToReference;

                if (target == null)
                {
                    network.Diagnostics.Add(Unresolved("a PDU triggering", pending.ToReference));
                    continue;
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
                "FLEXRAY-CLUSTER",
                "FLEXRAY-FRAME",
                "I-SIGNAL",
                "SYSTEM-SIGNAL",
                "COMPU-METHOD",
                "UNIT",
            };

            foreach (var pdu in PduElements)
            {
                set.Add(pdu);
            }

            return set;
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

        private sealed class Schedule
        {
            public Schedule(String? slot, String? baseCycle, String? repetition)
            {
                Slot = slot;
                BaseCycle = baseCycle;
                Repetition = repetition;
            }

            public String? Slot { get; }

            public String? BaseCycle { get; }

            public String? Repetition { get; }
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
            public Pending(String fromPath, String type, String toReference, Boolean throughTriggering)
            {
                FromPath = fromPath;
                Type = type;
                ToReference = toReference;
                ThroughTriggering = throughTriggering;
            }

            public String FromPath { get; }

            public String Type { get; }

            public String ToReference { get; }

            public Boolean ThroughTriggering { get; }
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

            public Dictionary<String, ArxmlElement> Networks { get; } = New();

            public Dictionary<String, ArxmlElement> Ecus { get; } = New();

            public Dictionary<String, ArxmlElement> Frames { get; } = New();

            public Dictionary<String, ArxmlElement> Pdus { get; } = New();

            public Dictionary<String, ArxmlElement> Signals { get; } = New();

            public Dictionary<String, ArxmlElement> SystemSignals { get; } = New();

            public Dictionary<String, ArxmlElement> CompuMethods { get; } = New();

            public Dictionary<String, String> UnitDisplayNames { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> Ports { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> ConnectorToEcu { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, String> PduTriggerings { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, Schedule> FrameSchedules { get; } =
                new Dictionary<String, Schedule>(StringComparer.Ordinal);

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
            ///   Records an element unless its path is already taken. A repeat is the FIRST one winning, in
            ///   both cases below: the alternative is a silent overwrite whose result depends on the order the
            ///   files happen to be written in.
            ///
            ///   <para>WITHIN one document a repeat is a fault and named per path: one path is one thing in
            ///   the standard's own terms, so a file declaring one twice contradicts itself. ACROSS documents
            ///   it is ordinary - the shared packages are in every extract - and is counted for the aggregate
            ///   <see cref="EndDocument"/> reports. Only what the per-path diagnostic would have counted is
            ///   counted here, which is why a repeated UNIT (not an element, and silently first-wins within a
            ///   file too) does not appear in either.</para>
            ///
            ///   <para>Returns FALSE when the element was refused, and every caller must stop there. The
            ///   caller's remaining work records the element's REFERENCES keyed by that same path, so
            ///   carrying on would give the surviving element the refused twin's unit chain and both
            ///   twins' edges: the twin would be invisible in the element list and present in the graph
            ///   anyway.</para>
            /// </summary>
            public Boolean Add(ArxmlElement element, Dictionary<String, ArxmlElement> byKind)
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
                    }
                    else
                    {
                        _redeclared++;
                    }

                    return false;
                }

                PathOwner[element.Path] = _documents;
                byKind[element.Path] = element;
                All[element.Path] = element;
                Order.Add(element.Path);
                return true;
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
