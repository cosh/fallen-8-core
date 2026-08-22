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
    ///   Reads an AUTOSAR classic-platform system extract and describes the FlexRay communication matrix it
    ///   carries. It knows nothing about snapshots, claims or the graph: it produces
    ///   <see cref="ArxmlNetwork"/>, which the provider maps. That split is what lets every parsing rule be
    ///   tested without a runtime, and it is why the rules that decide identity live on the other side.
    ///
    ///   <para>Two properties of the AUTOSAR standard carry the whole design. An element's identity is its
    ///   REFERENCE PATH, the slash-separated chain of short-names from the package root, and every
    ///   cross-reference in the file is written as that same path. So the reader rebuilds paths from a
    ///   short-name stack while streaming, and resolution afterwards is exact string equality on a
    ///   dictionary. Nothing is matched by name, position or similarity.</para>
    ///
    ///   <para>It streams. A system extract is routinely tens of megabytes of which the communication matrix
    ///   is a small fraction, so only the elements in the interest set are materialised as subtrees and
    ///   everything else advances the reader without allocating.</para>
    /// </summary>
    public static class ArxmlReader
    {
        /// <summary>The one schema namespace this reader understands. It covers every AUTOSAR 4.x release.</summary>
        public const String Namespace = "http://autosar.org/schema/r4.0";

        /// <summary>The document element every system extract carries.</summary>
        public const String RootElement = "AUTOSAR";

        private static readonly XNamespace Ar = Namespace;

        private const String ShortNameElement = "SHORT-NAME";

        // The PDU kinds worth describing. A PDU is a PDU whatever its flavour, so the flavour becomes a
        // property rather than a kind: a query for "what does this frame carry" must not have to enumerate
        // eleven labels, and a twelfth flavour must not become a twelfth entity kind.
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

        /// <summary>
        ///   Reads one system extract.
        /// </summary>
        /// <param name="xml">The document text.</param>
        /// <exception cref="ArxmlFormatException">The text is not XML this reader will read: a DTD, a root
        /// element or namespace that is not an AUTOSAR r4.0 extract, or malformed XML. Each is a refusal to
        /// GUESS, and the provider turns it into a failed run rather than an empty description, because a
        /// file that could not be read is not a network with nothing in it.</exception>
        public static ArxmlNetwork Read(String xml)
        {
            if (xml == null)
            {
                throw new ArgumentNullException(nameof(xml));
            }

            var collected = new Collected();

            // Hardened deliberately. This text arrives from a file an operator dropped in a directory, so a
            // DTD is refused outright: entity expansion and external-entity resolution are the two ways an
            // XML reader turns a data file into a fetch or a memory exhaustion, and an AUTOSAR extract has
            // no legitimate use for either.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            try
            {
                using (var text = new StringReader(xml))
                using (var reader = XmlReader.Create(text, settings))
                {
                    Stream(reader, collected);
                }
            }
            catch (XmlException failure)
            {
                throw new ArxmlFormatException(
                    "The file is not readable as XML: " + failure.Message +
                    " A DTD is refused outright, so a document carrying one arrives here too.", failure);
            }

            if (!collected.SawRoot)
            {
                throw new ArxmlFormatException(
                    "The file carries no elements at all, so it is not an AUTOSAR system extract.");
            }

            return Resolve(collected);
        }

        /// <summary>
        ///   The streaming pass. It maintains the short-name stack that IS the path of whatever the reader is
        ///   inside, and hands each interesting element to <see cref="Collect"/> as a materialised subtree.
        /// </summary>
        private static void Stream(XmlReader reader, Collected collected)
        {
            // (element name, short name once seen). An entry whose short name stays null is a structural
            // wrapper such as AR-PACKAGES or ELEMENTS, and contributes nothing to a path, which is exactly
            // the standard's own path semantics.
            var stack = new List<Frame>();
            var advanced = true;

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

                    if (!collected.SawRoot)
                    {
                        Gate(reader, name);
                        collected.SawRoot = true;
                    }

                    // A short name names the element the reader is currently inside, which is the frame on
                    // top of the stack. Reading its content advances the reader past the end tag, so the
                    // outer loop must not read again.
                    if (String.Equals(name, ShortNameElement, StringComparison.Ordinal) &&
                        stack.Count > 0 && stack[stack.Count - 1].ShortName == null)
                    {
                        stack[stack.Count - 1].ShortName = reader.ReadElementContentAsString();
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
        }

        /// <summary>
        ///   Refuses a document that is not an AUTOSAR r4.0 extract, naming what was found. A reader that
        ///   carried on would describe an empty network, and an empty COMPLETE description withdraws
        ///   everything the identity ever claimed.
        /// </summary>
        private static void Gate(XmlReader reader, String name)
        {
            if (!String.Equals(name, RootElement, StringComparison.Ordinal) ||
                !String.Equals(reader.NamespaceURI, Namespace, StringComparison.Ordinal))
            {
                throw new ArxmlFormatException(String.Format(CultureInfo.InvariantCulture,
                    "The document element is '{0}' in namespace '{1}', and an AUTOSAR system extract is " +
                    "'{2}' in namespace '{3}'. The file was not read further rather than being read as an " +
                    "extract with nothing in it.", name, reader.NamespaceURI, RootElement, Namespace));
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
                    collected.UnitDisplayNames[path] =
                        Text(element.Element(Ar + "DISPLAY-NAME")) ?? shortName;
                    break;
            }
        }

        private static void CollectEcu(String path, String shortName, XElement element, Collected collected)
        {
            collected.Add(new ArxmlElement(path, ArxmlKinds.Ecu) { [ArxmlProperties.Name] = shortName },
                collected.Ecus);

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
                    var direction = Text(port.Element(Ar + "COMMUNICATION-DIRECTION"));
                    if (portName == null || direction == null)
                    {
                        continue;
                    }

                    collected.Ports[connectorPath + "/" + portName] =
                        String.Equals(direction, "OUT", StringComparison.Ordinal);
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
            var channels = new List<XElement>();
            foreach (var channel in Descendants(element, n => n == "FLEXRAY-PHYSICAL-CHANNEL"))
            {
                channels.Add(channel);
            }

            collected.Add(new ArxmlElement(path, ArxmlKinds.Network)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.Protocol] = ArxmlProperties.FlexRayProtocol,
                [ArxmlProperties.ChannelCount] =
                    channels.Count.ToString(CultureInfo.InvariantCulture),
            }, collected.Networks);

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
                    collected.ConnectorAttachments.Add(new Pair(path, reference.Value));
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
                        collected.FlowByPort.Add(new Pair(signalRef, portRef.Value));
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
                collected.FlowByPort.Add(new Pair(frameRef, portRef.Value));
            }

            // Schedule. Recorded against the FRAME, because a frame's slot is the fact an engineer asks for
            // and the triggering is the standard's indirection rather than a thing anybody names.
            if (!collected.FrameSchedules.ContainsKey(frameRef))
            {
                var slot = First(triggering, "SLOT-ID");
                var baseCycle = First(triggering, "BASE-CYCLE");
                String? repetition = null;
                foreach (var candidate in Descendants(triggering, n => n == "CYCLE-REPETITION"))
                {
                    var value = candidate.Value;
                    if (value.StartsWith("CYCLE-REPETITION", StringComparison.Ordinal))
                    {
                        repetition = value;
                        break;
                    }
                }

                if (slot != null || baseCycle != null || repetition != null)
                {
                    collected.FrameSchedules[frameRef] = new Schedule(slot, baseCycle, repetition);
                }
            }
        }

        private static void CollectFrame(String path, String shortName, XElement element,
            Collected collected)
        {
            collected.Add(new ArxmlElement(path, ArxmlKinds.Frame)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.FrameLengthBytes] = Text(element.Element(Ar + "FRAME-LENGTH")),
            }, collected.Frames);

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
            collected.Add(pdu, collected.Pdus);

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
                collected.Pending.Add(new Pending(path, ArxmlRelations.Carries, reference.Value, true));
            }

            var payload = element.Element(Ar + "PAYLOAD-REF");
            if (payload != null)
            {
                collected.Pending.Add(new Pending(path, ArxmlRelations.Secures, payload.Value, true));
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
            collected.Add(signal, collected.Signals);

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
            collected.Add(systemSignal, collected.SystemSignals);

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
            collected.Add(new ArxmlElement(path, ArxmlKinds.CompuMethod)
            {
                [ArxmlProperties.Name] = shortName,
                [ArxmlProperties.Category] = Text(element.Element(Ar + "CATEGORY")),
            }, collected.CompuMethods);

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
                var display = collected.UnitDisplayNames.TryGetValue(pair.Value, out var found)
                    ? found
                    : LastSegment(pair.Value);
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
                if (!collected.Ports.TryGetValue(flow.Right, out var isOut))
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

                if (isOut)
                {
                    Relate(network, ecuPath, ArxmlRelations.Sends, flow.Left);
                }
                else
                {
                    Relate(network, flow.Left, ArxmlRelations.DeliversTo, ecuPath);
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
                if (seen.Add(relation.FromPath + " " + relation.Type + " " + relation.ToPath))
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
                "The file names {0} that it does not define, so what pointed at it was dropped. The usual " +
                "cause is a partial export: an extract that references a package it did not include.", what),
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

        /// <summary>
        ///   Trims, and treats a value that is nothing but whitespace as ABSENT. An absent value must not
        ///   become an empty property: writing one makes the property exist and overwrites what another
        ///   integration knows.
        /// </summary>
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
        ///   Everything the streaming pass gathered. Elements are kept per kind AND in one path table, so a
        ///   repeated path is caught once rather than per kind.
        /// </summary>
        private sealed class Collected
        {
            public Boolean SawRoot { get; set; }

            public Dictionary<String, ArxmlElement> Networks { get; } = New();

            public Dictionary<String, ArxmlElement> Ecus { get; } = New();

            public Dictionary<String, ArxmlElement> Frames { get; } = New();

            public Dictionary<String, ArxmlElement> Pdus { get; } = New();

            public Dictionary<String, ArxmlElement> Signals { get; } = New();

            public Dictionary<String, ArxmlElement> SystemSignals { get; } = New();

            public Dictionary<String, ArxmlElement> CompuMethods { get; } = New();

            public Dictionary<String, String> UnitDisplayNames { get; } =
                new Dictionary<String, String>(StringComparer.Ordinal);

            public Dictionary<String, Boolean> Ports { get; } =
                new Dictionary<String, Boolean>(StringComparer.Ordinal);

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

            private List<String> Order { get; } = new List<String>();

            private HashSet<String> Paths { get; } = new HashSet<String>(StringComparer.Ordinal);

            private Dictionary<String, ArxmlElement> All { get; } = New();

            /// <summary>
            ///   Records an element unless its path is already taken. A repeat is a diagnostic and the FIRST
            ///   wins: the alternative is a silent overwrite whose result depends on file order.
            /// </summary>
            public void Add(ArxmlElement element, Dictionary<String, ArxmlElement> byKind)
            {
                if (!Paths.Add(element.Path))
                {
                    Diagnostics.Add(new ArxmlDiagnostic(ArxmlDiagnosticKind.DuplicatePath,
                        "Two elements compose this same reference path, so only the first was described. " +
                        "One path is one thing in the standard's own terms, and keeping both would make " +
                        "which one wins depend on the order the file happens to be written in.",
                        element.Path));
                    return;
                }

                byKind[element.Path] = element;
                All[element.Path] = element;
                Order.Add(element.Path);
            }

            /// <summary>The elements in the order the file described them, which keeps a run reproducible.</summary>
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
