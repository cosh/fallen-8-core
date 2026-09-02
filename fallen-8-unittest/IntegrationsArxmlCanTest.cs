// MIT License
//
// IntegrationsArxmlCanTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   CAN buses, and the multi-bus rules CAN is the first input to need (feature
    ///   arxml-can-clusters).
    ///
    ///   <para>A vehicle does not arrive as one bus. It arrives as one extract per bus, which turned two
    ///   assumptions in the FlexRay-only reader into defects: that a re-declared reference path is always a
    ///   repeat of the standard's shared catalogue, and that "no FlexRay cluster" means the same thing as
    ///   "no bus". Both are fixed here, and both are tested against the case that exposed them: several
    ///   extracts describing one vehicle, each carrying only its own bus's view of a shared ECU.</para>
    ///
    ///   <para>Every fixture is HAND-AUTHORED and describes an invented network. No content derived from a
    ///   real manufacturer's export appears in this repository in any form, which is a rule of the feature
    ///   rather than a preference.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsArxmlCanTest
    {
        #region what a CAN bus is read as

        /// <summary>
        ///   The whole of CAN in one assertion: a bus, its ECUs, its frames with their identifiers, and the
        ///   flow. If the protocol table is wrong anywhere, this is what says so.
        /// </summary>
        [TestMethod]
        public void ACanClusterIsReadToTheSameVocabularyAsAFlexRayOne()
        {
            var network = ArxmlReader.Read(CanExtract);

            var bus = Element(network, "/Clusters/BODYCAN");
            Assert.AreEqual(ArxmlKinds.Network, bus.Kind, "a CAN cluster is a network like any other bus");
            Assert.AreEqual("can", bus[ArxmlProperties.Protocol]);
            Assert.AreEqual("BODYCAN", bus[ArxmlProperties.Name]);

            // The frame is one KIND across protocols, which is what keeps a query for "what does this ECU
            // send" from having to enumerate a label per bus technology.
            var frame = Element(network, "/Frames/FRM_Doors");
            Assert.AreEqual(ArxmlKinds.Frame, frame.Kind);
            Assert.AreEqual("8", frame[ArxmlProperties.FrameLengthBytes]);

            // Containment runs frame to PDU to signal exactly as on FlexRay: none of that code is
            // protocol-specific, and this asserts it rather than assuming it.
            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Doors" },
                Targets(network, "/Frames/FRM_Doors", ArxmlRelations.Contains));
            CollectionAssert.AreEqual(new[] { "/ISignals/SIG_DoorLock" },
                Targets(network, "/Pdus/PDU_Doors", ArxmlRelations.Contains));

            Assert.AreEqual(0, network.Diagnostics.Count,
                "a whole, self-contained CAN extract produced diagnostics: " + Describe(network));
        }

        /// <summary>
        ///   The identifier and the addressing mode land ON THE FRAME, denormalised from its triggering,
        ///   because the identifier is what an engineer names a CAN frame by and the triggering is the
        ///   standard's indirection rather than a thing anybody names.
        /// </summary>
        [TestMethod]
        public void ACanFramesIdentifierAndAddressingModeLandOnTheFrame()
        {
            var network = ArxmlReader.Read(CanExtract);
            var frame = Element(network, "/Frames/FRM_Doors");

            Assert.AreEqual("1626", frame[ArxmlProperties.CanId]);
            Assert.AreEqual("STANDARD", frame[ArxmlProperties.CanAddressingMode],
                "the standard's own word is kept: an 11-bit value is legal in either mode, so the mode " +
                "cannot be derived from the identifier");

            // And the FlexRay schedule fields are ABSENT rather than present-and-empty, which is what a
            // query filtering on protocol relies on.
            Assert.IsNull(frame[ArxmlProperties.SlotId], "a CAN frame is not scheduled and has no slot");
            Assert.IsNull(frame[ArxmlProperties.BaseCycle]);
            Assert.IsNull(frame[ArxmlProperties.CycleRepetition]);
        }

        /// <summary>The bus properties the standard puts on every protocol's cluster conditional.</summary>
        [TestMethod]
        public void TheBusCarriesItsBaudrateAndTheProtocolWordsTheFileUses()
        {
            var bus = Element(ArxmlReader.Read(CanExtract), "/Clusters/BODYCAN");

            Assert.AreEqual("500000", bus[ArxmlProperties.Baudrate]);
            Assert.AreEqual("2000000", bus[ArxmlProperties.CanFdBaudrate],
                "the FD baudrate is present on an FD bus, and its absence elsewhere is the fact that the " +
                "bus is classic CAN rather than a missing property");
            Assert.AreEqual("CAN", bus[ArxmlProperties.ProtocolName]);
            Assert.AreEqual("2.0", bus[ArxmlProperties.ProtocolVersion]);
        }

        /// <summary>
        ///   A classic CAN bus carries no FD baudrate, and that absence is information rather than a hole.
        /// </summary>
        [TestMethod]
        public void AClassicCanBusHasNoFdBaudrateAtAll()
        {
            var bus = Element(ArxmlReader.Read(ClassicCanExtract), "/Clusters/CHASSISCAN");

            Assert.AreEqual("250000", bus[ArxmlProperties.Baudrate]);
            Assert.IsNull(bus[ArxmlProperties.CanFdBaudrate]);
        }

        /// <summary>
        ///   A frame triggered twice takes BOTH its fields from the first declaration. The alternative is a
        ///   frame whose identifier comes from one triggering and whose mode comes from another, describing
        ///   a transmission that appears in no file.
        /// </summary>
        [TestMethod]
        public void AFrameTriggeredTwice_TakesItsIdentityFromTheFirstTriggeringOnly()
        {
            var frame = Element(ArxmlReader.Read(TwiceTriggeredCanExtract), "/Frames/FRM_Twice");

            Assert.AreEqual("100", frame[ArxmlProperties.CanId]);
            Assert.AreEqual("STANDARD", frame[ArxmlProperties.CanAddressingMode],
                "the second triggering's EXTENDED mode was taken while the first triggering's id was kept, " +
                "which describes a frame neither triggering declares");
        }

        #endregion

        #region both protocols in one job

        /// <summary>
        ///   THE CASE THE FEATURE EXISTS FOR: a vehicle as one extract per bus, sharing a gateway ECU.
        ///
        ///   <para>Each extract carries only its own bus's connector for that ECU, because an ECU's
        ///   declaration in an AUTOSAR extract is bus-local. Under the reader's old rule the second
        ///   extract's ECU was refused as a re-declared path and its subtree skipped, so the gateway ended
        ///   up attached to whichever bus was read first and to nothing else.</para>
        /// </summary>
        [TestMethod]
        public void AGatewayEcuDeclaredByTwoExtracts_IsAttachedToBothItsBuses()
        {
            var network = ReadSet(("body-can.arxml", CanExtract), ("chassis-fr.arxml", FlexRayExtract));

            Assert.AreEqual(2, network.Elements.Count(e => e.Kind == ArxmlKinds.Network),
                "one job over two extracts describes two buses");

            // One ECU element, two attachments. Not two ECU elements: the path is the identity, and the
            // gateway is one physical unit.
            Assert.AreEqual(1, network.Elements.Count(e => e.Path == "/Ecus/ECU_Gateway"),
                "the gateway was described twice, so the two extracts' views did not merge");

            // attachedTo runs FROM the ECU TO the bus, so a path query starts at the unit and reaches the
            // networks it sits on - and, since the channel became an element, the channels too. Both are
            // asserted: on CAN and FlexRay the two coincide, and it is on Ethernet that the channel edge
            // starts saying something the network edge cannot (which VLAN).
            var reached = Targets(network, "/Ecus/ECU_Gateway", ArxmlRelations.AttachedTo);
            CollectionAssert.AreEqual(
                new[]
                {
                    "/Clusters/BODYCAN", "/Clusters/BODYCAN/BODYCAN_CH",
                    "/Clusters/CHASSISFR", "/Clusters/CHASSISFR/CHASSISFR_CH_A",
                },
                reached,
                "the gateway reached [" + String.Join(", ", reached) + "] rather than both its buses and " +
                "their channels, which is the defect this feature fixes. Diagnostics: " + Describe(network));

            Assert.AreEqual(2, reached.Count(p => p.Count(c => c == '/') == 2),
                "exactly two of those are the NETWORKS, so 'which buses is this gateway on' stays a " +
                "one-hop question after the channel became an element");
        }

        /// <summary>
        ///   The order the caller lists the files does not change the graph. This is the property the union
        ///   rule most easily breaks, because a side table written twice is last-one-wins unless every
        ///   write is guarded.
        /// </summary>
        [TestMethod]
        public void TheOrderOfTheExtractsDoesNotChangeTheGraph()
        {
            var forwards = ReadSet(("body-can.arxml", CanExtract), ("chassis-fr.arxml", FlexRayExtract));
            var backwards = ReadSet(("chassis-fr.arxml", FlexRayExtract), ("body-can.arxml", CanExtract));

            CollectionAssert.AreEquivalent(Signature(forwards), Signature(backwards),
                "reading the same extracts in the other order produced a different graph, so something " +
                "the union contributes is last-file-wins");

            // Relations too, not just elements: the attachment and flow tables are where a repeat would
            // show up as a duplicated edge rather than a changed one.
            Assert.AreEqual(forwards.Relations.Count, backwards.Relations.Count,
                "the two orders produced different relation counts");
        }

        /// <summary>
        ///   A relation is recorded once however many extracts say it. Under the union rule two extracts of
        ///   one bus both name the same connector, and nothing between the reader and the graph removes a
        ///   repeated relation.
        /// </summary>
        [TestMethod]
        public void TwoExtractsOfOneBus_ProduceNoDuplicateRelations()
        {
            var network = ReadSet(("first.arxml", SharedBusFirstHalf), ("second.arxml", SharedBusSecondHalf));

            var distinct = network.Relations
                .Select(r => r.FromPath + "|" + r.Type + "|" + r.ToPath)
                .ToList();
            CollectionAssert.AllItemsAreUnique(distinct,
                "the same relation was recorded twice, which duplicates an edge in the graph");
        }

        /// <summary>
        ///   Two extracts declaring ONE cluster path have their channels MERGED, and the merge is reported.
        ///
        ///   <para>Merging is what one bus split across extracts needs. It is also what would silently
        ///   collapse two different buses that happen to share a path, and this reader cannot tell those
        ///   apart, so the diagnostic exists to make the case visible rather than to forbid it.</para>
        /// </summary>
        [TestMethod]
        public void TwoExtractsDeclaringOneCluster_MergeItsChannelsAndSaySo()
        {
            var network = ReadSet(("first.arxml", SharedBusFirstHalf), ("second.arxml", SharedBusSecondHalf));

            Assert.AreEqual(1, network.Elements.Count(e => e.Kind == ArxmlKinds.Network),
                "one cluster path is one network however many files declare it");

            // BOTH halves' frames reached the graph. Under the old rule the second extract contributed
            // nothing at all, losing most of a split cluster's triggerings.
            Assert.IsNotNull(Element(network, "/Frames/FRM_First"));
            Assert.IsNotNull(Element(network, "/Frames/FRM_Second"));
            Assert.AreEqual("11", Element(network, "/Frames/FRM_First")[ArxmlProperties.CanId]);
            Assert.AreEqual("22", Element(network, "/Frames/FRM_Second")[ArxmlProperties.CanId]);

            var reported = network.Diagnostics
                .Where(d => d.Kind == ArxmlDiagnosticKind.RedeclaredCluster)
                .ToList();
            Assert.AreEqual(1, reported.Count,
                "the cluster collision is reported ONCE per path, however many files pile onto it: " +
                Describe(network));
            Assert.AreEqual("/Clusters/SHAREDCAN", reported[0].Subject);
            StringAssert.Contains(reported[0].Message, "merged into one network", reported[0].Message);
            StringAssert.Contains(reported[0].Message, "same bus", reported[0].Message);
        }

        /// <summary>
        ///   A shared CATALOGUE path is not reported as a cluster collision. The two are different facts:
        ///   merging a compu-method every extract repeats is simply correct and needs no warning, and
        ///   warning about it would bury the one that matters.
        /// </summary>
        [TestMethod]
        public void ASharedCataloguePathIsNotReportedAsAClusterCollision()
        {
            var network = ReadSet(("body-can.arxml", CanExtract), ("chassis-fr.arxml", FlexRayExtract));

            Assert.IsFalse(network.Diagnostics.Any(d => d.Kind == ArxmlDiagnosticKind.RedeclaredCluster),
                "a shared compu-method was reported as a merged bus: " + Describe(network));
            Assert.IsTrue(network.Diagnostics.Any(d => d.Kind == ArxmlDiagnosticKind.RedeclaredPaths),
                "the ordinary re-declaration is still counted");
        }

        #endregion

        #region a bus this version does not read

        /// <summary>
        ///   An unread bus is NAMED. The reader materialises only its interest set, so a bus it does not
        ///   read leaves no trace at all unless it is asked for: an operator would otherwise be left
        ///   inferring it from a network that never appeared.
        /// </summary>
        [TestMethod]
        public void AnUnreadBusIsNamedWithTheNumberOfFilesThatCarryOne()
        {
            var network = ReadSet(("body-can.arxml", CanExtract), ("comfort-lin.arxml", UnreadBusExtract));

            var unread = network.UnreadClusters.SingleOrDefault(u => u.Element == "LIN-CLUSTER");
            Assert.IsNotNull(unread, "the LIN bus went by unmentioned: " + Describe(network));
            Assert.AreEqual(1, unread.Files);

            var said = network.Diagnostics.Single(d => d.Kind == ArxmlDiagnosticKind.UnreadCluster);
            StringAssert.Contains(said.Message, "does not read a LIN-CLUSTER", said.Message);
            // The consequence is stated, not just the fact: the snapshot is still complete over what was
            // read, so a later job that omits these files withdraws whatever only they described.
            StringAssert.Contains(said.Message, "still counts as complete", said.Message);

            // The readable bus still imported. That is the decision: import what we can, and say what we
            // could not.
            Assert.AreEqual(1, network.Elements.Count(e => e.Kind == ArxmlKinds.Network));
        }

        /// <summary>
        ///   A set of only unread buses produces NO network, which is what lets the provider refuse the run
        ///   rather than report an empty complete snapshot that withdraws everything.
        /// </summary>
        [TestMethod]
        public void ASetOfOnlyUnreadBuses_DescribesNoNetworkAtAll()
        {
            var network = ArxmlReader.Read(UnreadBusExtract);

            Assert.AreEqual(0, network.Elements.Count(e => e.Kind == ArxmlKinds.Network));
            Assert.AreEqual(1, network.UnreadClusters.Count);
            Assert.AreEqual("LIN-CLUSTER", network.UnreadClusters[0].Element);
        }

        /// <summary>The same unread kind in several files is one diagnostic naming the count.</summary>
        [TestMethod]
        public void TheSameUnreadBusInSeveralFilesIsCountedOnce()
        {
            var network = ReadSet(("comfort-a.arxml", UnreadBusExtract), ("comfort-b.arxml", UnreadBusExtract),
                ("body-can.arxml", CanExtract));

            var unread = network.UnreadClusters.Single();
            Assert.AreEqual("LIN-CLUSTER", unread.Element);
            Assert.AreEqual(2, unread.Files,
                "two files declared a LIN cluster, and the report counts FILES so an operator can tell one " +
                "stray extract from a whole segment they meant to include");
            Assert.AreEqual(1, network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnreadCluster));
        }

        #endregion

        #region the PDU flow path

        /// <summary>
        ///   A PDU triggering's ports produce flow, which neither protocol read before. It is additive on
        ///   both, and it is the only flow an Ethernet cluster will ever have, since Ethernet carries no
        ///   frame layer at all.
        /// </summary>
        [TestMethod]
        public void APduTriggeringsPortsProduceFlow()
        {
            var network = ArxmlReader.Read(CanExtract);

            // The gateway's OUT port on the PDU triggering makes it a sender of that PDU.
            CollectionAssert.Contains(Targets(network, "/Ecus/ECU_Gateway", ArxmlRelations.Sends),
                "/Pdus/PDU_Doors");
            // The door module's IN port makes it a receiver.
            CollectionAssert.Contains(Sources(network, ArxmlRelations.DeliversTo, "/Ecus/ECU_Door"),
                "/Pdus/PDU_Doors");
        }

        #endregion

        #region helpers and fixtures

        private static ArxmlNetwork ReadSet(params (String Name, String Xml)[] documents)
        {
            var reader = new ArxmlReader();
            foreach (var document in documents)
            {
                reader.Add(document.Name, document.Xml);
            }

            return reader.Complete();
        }

        private static ArxmlElement Element(ArxmlNetwork network, String path)
        {
            var found = network.Elements.SingleOrDefault(e => e.Path == path);
            Assert.IsNotNull(found, "the fixture must describe '" + path + "'");
            return found;
        }

        private static List<String> Targets(ArxmlNetwork network, String fromPath, String type)
        {
            return network.Relations
                .Where(r => r.FromPath == fromPath && r.Type == type)
                .Select(r => r.ToPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        private static List<String> Sources(ArxmlNetwork network, String type, String toPath)
        {
            return network.Relations
                .Where(r => r.ToPath == toPath && r.Type == type)
                .Select(r => r.FromPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Every element and relation, so two readings can be compared as wholes.</summary>
        private static List<String> Signature(ArxmlNetwork network)
        {
            var lines = network.Elements.Select(e => "E " + e.Kind + " " + e.Path).ToList();
            lines.AddRange(network.Relations.Select(r => "R " + r.FromPath + " " + r.Type + " " + r.ToPath));
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        private static String Describe(ArxmlNetwork network)
        {
            return String.Join("; ", network.Diagnostics.Select(d => d.Kind + " " + d.Subject));
        }

        /// <summary>
        ///   An invented CAN bus with a gateway and a door module, an FD baudrate, one frame carrying one
        ///   PDU carrying one signal, and ports in both directions on both the frame and the PDU triggering.
        ///   It shares a compu-method package with the FlexRay extract below, which is the ordinary
        ///   catalogue collision every multi-extract job has.
        /// </summary>
        private const String CanExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_Shared</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                    </COMPU-METHOD>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL><SHORT-NAME>SIG_DoorLock</SHORT-NAME><LENGTH>2</LENGTH></I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Doors</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_DoorLock</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_DoorLock</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-FRAME>
                      <SHORT-NAME>FRM_Doors</SHORT-NAME>
                      <FRAME-LENGTH>8</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>FMAP_Doors</SHORT-NAME>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Doors</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </CAN-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Gateway</SHORT-NAME>
                      <CONNECTORS>
                        <CAN-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>CONN_GatewayCan</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                            <I-PDU-PORT>
                              <SHORT-NAME>PP_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </CAN-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Door</SHORT-NAME>
                      <CONNECTORS>
                        <CAN-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>CONN_DoorCan</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-PDU-PORT>
                              <SHORT-NAME>PP_In</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </CAN-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>BODYCAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <BAUDRATE>500000</BAUDRATE>
                          <CAN-FD-BAUDRATE>2000000</CAN-FD-BAUDRATE>
                          <PROTOCOL-NAME>CAN</PROTOCOL-NAME>
                          <PROTOCOL-VERSION>2.0</PROTOCOL-VERSION>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>BODYCAN_CH</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="CAN-COMMUNICATION-CONNECTOR">/Ecus/ECU_Gateway/CONN_GatewayCan</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="CAN-COMMUNICATION-CONNECTOR">/Ecus/ECU_Door/CONN_DoorCan</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_Doors</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_Doors</FRAME-REF>
                                  <FRAME-PORT-REFS>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/Ecus/ECU_Gateway/CONN_GatewayCan/FP_Out</FRAME-PORT-REF>
                                  </FRAME-PORT-REFS>
                                  <IDENTIFIER>1626</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_Doors</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Doors</I-PDU-REF>
                                  <I-PDU-PORT-REFS>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/ECU_Gateway/CONN_GatewayCan/PP_Out</I-PDU-PORT-REF>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/ECU_Door/CONN_DoorCan/PP_In</I-PDU-PORT-REF>
                                  </I-PDU-PORT-REFS>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                            </CAN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </CAN-CLUSTER-CONDITIONAL>
                      </CAN-CLUSTER-VARIANTS>
                    </CAN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>
        ///   The other half of the same invented vehicle: a FlexRay bus, and the SAME gateway ECU carrying
        ///   only its FlexRay connector. It also repeats the shared compu-method package, which is the
        ///   ordinary catalogue collision.
        /// </summary>
        private const String FlexRayExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_Shared</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                    </COMPU-METHOD>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Gateway</SHORT-NAME>
                      <CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>CONN_GatewayFr</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_FrOut</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>CHASSISFR</SHORT-NAME>
                      <FLEXRAY-CLUSTER-VARIANTS>
                        <FLEXRAY-CLUSTER-CONDITIONAL>
                          <BAUDRATE>10000000</BAUDRATE>
                          <PROTOCOL-NAME>FlexRay</PROTOCOL-NAME>
                          <PROTOCOL-VERSION>3.0</PROTOCOL-VERSION>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CHASSISFR_CH_A</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/Ecus/ECU_Gateway/CONN_GatewayFr</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </FLEXRAY-CLUSTER-CONDITIONAL>
                      </FLEXRAY-CLUSTER-VARIANTS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>A classic CAN bus: no FD baudrate at all.</summary>
        private const String ClassicCanExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>CHASSISCAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <BAUDRATE>250000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CHASSISCAN_CH</SHORT-NAME>
                            </CAN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </CAN-CLUSTER-CONDITIONAL>
                      </CAN-CLUSTER-VARIANTS>
                    </CAN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>One frame triggered twice, with a different identifier and mode each time.</summary>
        private const String TwiceTriggeredCanExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-FRAME><SHORT-NAME>FRM_Twice</SHORT-NAME><FRAME-LENGTH>8</FRAME-LENGTH></CAN-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>TWICECAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>TWICECAN_CH</SHORT-NAME>
                              <FRAME-TRIGGERINGS>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_One</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_Twice</FRAME-REF>
                                  <IDENTIFIER>100</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_Two</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_Twice</FRAME-REF>
                                  <IDENTIFIER>200</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>EXTENDED</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </CAN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </CAN-CLUSTER-CONDITIONAL>
                      </CAN-CLUSTER-VARIANTS>
                    </CAN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>
        ///   Half of one bus, declaring the cluster path with one frame on it. Its twin below declares the
        ///   SAME cluster path with a different frame, which is one bus arriving as two extracts.
        /// </summary>
        private const String SharedBusFirstHalf = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-FRAME><SHORT-NAME>FRM_First</SHORT-NAME><FRAME-LENGTH>8</FRAME-LENGTH></CAN-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Shared</SHORT-NAME>
                      <CONNECTORS>
                        <CAN-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>CONN_A</SHORT-NAME>
                        </CAN-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>SHAREDCAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <BAUDRATE>500000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>SHAREDCAN_CH</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="CAN-COMMUNICATION-CONNECTOR">/Ecus/ECU_Shared/CONN_A</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_First</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_First</FRAME-REF>
                                  <IDENTIFIER>11</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </CAN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </CAN-CLUSTER-CONDITIONAL>
                      </CAN-CLUSTER-VARIANTS>
                    </CAN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>The other half, re-declaring the same cluster and the same ECU with its own content.</summary>
        private const String SharedBusSecondHalf = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-FRAME><SHORT-NAME>FRM_Second</SHORT-NAME><FRAME-LENGTH>8</FRAME-LENGTH></CAN-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Shared</SHORT-NAME>
                      <CONNECTORS>
                        <CAN-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>CONN_A</SHORT-NAME>
                        </CAN-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>SHAREDCAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <BAUDRATE>500000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>SHAREDCAN_CH</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="CAN-COMMUNICATION-CONNECTOR">/Ecus/ECU_Shared/CONN_A</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_Second</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_Second</FRAME-REF>
                                  <IDENTIFIER>22</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </CAN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </CAN-CLUSTER-CONDITIONAL>
                      </CAN-CLUSTER-VARIANTS>
                    </CAN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        /// <summary>A bus this version does not read, carrying nothing else readable.</summary>
        /// <summary>
        ///   A bus this version does NOT read, which is what these three tests are about. It used to be an
        ///   Ethernet cluster; Ethernet is read now (feature arxml-vehicle-model, step 2), so the fixture
        ///   moved to LIN rather than the tests being weakened to match. The mechanism it exercises - a bus
        ///   nobody reads has to be NAMED, because the reader materialises only its interest set and would
        ///   otherwise leave no trace of it - is the same whichever protocol is unread.
        /// </summary>
        private const String UnreadBusExtract = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <LIN-CLUSTER>
                      <SHORT-NAME>COMFORT_LIN</SHORT-NAME>
                      <LIN-CLUSTER-VARIANTS>
                        <LIN-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <LIN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>LIN_CH</SHORT-NAME>
                            </LIN-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </LIN-CLUSTER-CONDITIONAL>
                      </LIN-CLUSTER-VARIANTS>
                    </LIN-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        #endregion
    }
}
