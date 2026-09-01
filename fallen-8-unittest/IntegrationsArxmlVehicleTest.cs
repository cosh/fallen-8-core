// MIT License
//
// IntegrationsArxmlVehicleTest.cs
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
    ///   THE DELIVERABLE of feature arxml-vehicle-model: a vehicle whose buses are of three different
    ///   protocols, in one graph, traversable from an ECU on one bus to an ECU on another.
    ///
    ///   <para>The join is not a feature and never was. A <c>SYSTEM-SIGNAL</c> is bus-independent and an
    ///   <c>I-SIGNAL</c> is its per-bus realisation, so one piece of information carried on three buses is
    ///   ONE system signal with three distinct I-signals - and the reader has always emitted
    ///   <c>implements</c> between them. What was missing was the ability to have the buses in the graph
    ///   at the same time: a job carrying all three used to be too large for the transport, and one
    ///   carrying one bus withdrew the others. Both are fixed (per-scope completeness, and the transport
    ///   raised to what it can carry), and Ethernet is now read at all.</para>
    ///
    ///   <para>So this file asserts the traversal rather than the plumbing, and it asserts it as a WALK -
    ///   following edges the way a query would - because a set of per-hop assertions can all pass over a
    ///   graph the walk cannot actually cross.</para>
    ///
    ///   <para>Every fixture is HAND-AUTHORED and describes an invented vehicle. No content derived from a
    ///   real manufacturer's export appears in this repository in any form.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsArxmlVehicleTest
    {
        #region the three-bus vehicle

        [TestMethod]
        public void ThreeBusesOfThreeProtocolsImportAsOneVehicle()
        {
            var vehicle = Vehicle();

            var buses = vehicle.Elements
                .Where(e => e.Kind == ArxmlKinds.Network)
                .ToDictionary(e => e.Path, e => e[ArxmlProperties.Protocol]);

            Assert.AreEqual(3, buses.Count, "one extract per bus, three buses");
            Assert.AreEqual(ArxmlProperties.CanProtocol, buses["/Clusters/CHASSIS_CAN"]);
            Assert.AreEqual(ArxmlProperties.FlexRayProtocol, buses["/Clusters/CHASSIS_FR"]);
            Assert.AreEqual(ArxmlProperties.EthernetProtocol, buses["/Clusters/BACKBONE"]);

            Assert.AreEqual(1, vehicle.Elements.Count(e => e.Path == "/Shared/SYS_WheelSpeed"),
                "all three extracts declare the shared system signal, as every real extract repeats the " +
                "standard's packages, and it must be ONE element or there is no join");

            Assert.AreEqual(0,
                vehicle.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "every reference the three extracts write resolves across their union: " + String.Join("; ",
                    vehicle.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        /// <summary>
        ///   THE TRAVERSAL. From the ECU that produces a value on CAN, through the bus-independent system
        ///   signal, to the ECUs that consume it on FlexRay and on Ethernet - without the walk knowing which
        ///   protocols are involved.
        /// </summary>
        [TestMethod]
        public void AValueProducedOnCanReachesItsConsumersOnFlexRayAndEthernet()
        {
            var vehicle = Vehicle();

            var reached = Consumers(vehicle, "/Ecus/ECU_Brake");

            CollectionAssert.AreEqual(new[] { "/Ecus/ECU_Drive", "/Ecus/ECU_Steering" },
                reached.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                "the brake unit's wheel speed must reach both consumers. It reached [" +
                String.Join(", ", reached.Keys) + "]");

            CollectionAssert.AreEquivalent(
                new[] { ArxmlProperties.FlexRayProtocol, ArxmlProperties.EthernetProtocol },
                reached.Values.ToArray(),
                "and they must be on OTHER protocols than the producer, which is the whole point: the walk " +
                "crossed from CAN to FlexRay and to Ethernet through one shared system signal");
        }

        /// <summary>
        ///   The join is the SYSTEM signal and never the I-signal. If two buses ever shared an I-signal, the
        ///   model would be wrong in a way that looks right: the graph would still traverse, and it would be
        ///   asserting that one bus's wire representation is the other's.
        /// </summary>
        [TestMethod]
        public void TheJoinIsTheSystemSignal_AndNoISignalIsSharedByTwoBuses()
        {
            var vehicle = Vehicle();

            var implementers = vehicle.Relations
                .Where(r => r.Type == ArxmlRelations.Implements && r.ToPath == "/Shared/SYS_WheelSpeed")
                .Select(r => r.FromPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "/ISignals/SIG_WheelSpeed_Can", "/ISignals/SIG_WheelSpeed_Eth",
                    "/ISignals/SIG_WheelSpeed_Fr" },
                implementers,
                "one system signal, three per-bus realisations: that is the standard's own shape and it is " +
                "what makes the value one thing across the vehicle");

            // Every I-signal is carried by exactly one bus. Asserted through the PDU, because that is the
            // only containment there is on Ethernet.
            foreach (var signal in implementers)
            {
                var pdus = vehicle.Relations
                    .Where(r => r.Type == ArxmlRelations.Contains && r.ToPath == signal)
                    .Select(r => r.FromPath)
                    .Distinct()
                    .ToArray();
                Assert.AreEqual(1, pdus.Length,
                    signal + " is carried by " + pdus.Length + " PDUs. An I-signal belongs to ONE bus; a " +
                    "shared one would mean the model had joined two buses at the wire level");
            }
        }

        /// <summary>
        ///   The protocols keep their own shapes in one graph. A CAN frame is still a frame; the Ethernet bus
        ///   has none at all. This is what a traversal has to tolerate, and it is why a query that reaches
        ///   signals through frames cannot be the only way to walk a vehicle.
        /// </summary>
        [TestMethod]
        public void EachBusKeepsItsOwnShape_AndOnlyEthernetHasNoFrames()
        {
            var vehicle = Vehicle();

            var frames = vehicle.Elements.Where(e => e.Kind == ArxmlKinds.Frame).Select(e => e.Path).ToArray();
            CollectionAssert.AreEquivalent(new[] { "/Frames/FRM_WheelSpeed", "/Frames/FRM_Chassis" }, frames,
                "the CAN and FlexRay buses each carry a frame; the Ethernet one carries none");

            var canFrame = vehicle.Elements.Single(e => e.Path == "/Frames/FRM_WheelSpeed");
            Assert.AreEqual("419", canFrame[ArxmlProperties.CanId],
                "and a CAN frame keeps its identifier, denormalised from its triggering");
            Assert.IsNull(canFrame[ArxmlProperties.SlotId],
                "while the FlexRay schedule is absent on it, as it must be: a CAN frame is not scheduled");
        }

        /// <summary>
        ///   The channels of all three buses are in the graph, and only the Ethernet ones are VLANs. This is
        ///   the fact the channel became an element for: on one bus it is redundancy, on another it is the
        ///   single channel, on the third it is a broadcast domain an ECU is or is not in.
        /// </summary>
        [TestMethod]
        public void EveryBusContributesItsChannels_AndOnlyEthernetsAreVlans()
        {
            var vehicle = Vehicle();

            var channels = vehicle.Elements
                .Where(e => e.Kind == ArxmlKinds.Channel)
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(
                new[]
                {
                    "/Clusters/BACKBONE/CH_BACKBONE",
                    "/Clusters/CHASSIS_CAN/CH_CAN",
                    "/Clusters/CHASSIS_FR/CH_FR_A",
                    "/Clusters/CHASSIS_FR/CH_FR_B",
                },
                channels.Select(c => c.Path).ToArray());

            Assert.AreEqual("7", channels.Single(c => c.Path == "/Clusters/BACKBONE/CH_BACKBONE")
                    [ArxmlProperties.VlanId],
                "the Ethernet channel is a VLAN and says which");
            Assert.AreEqual(3, channels.Count(c => c[ArxmlProperties.VlanId] == null),
                "and the CAN and FlexRay channels are not VLANs, so the property is ABSENT on them rather " +
                "than empty: a query filtering on vlanId finds the Ethernet channels and only those");
        }

        #endregion

        #region the walk

        /// <summary>
        ///   Follows the vehicle the way a query would: out of an ECU by <c>sends</c>, up to the
        ///   bus-independent signal by <c>implements</c>, back down every other realisation of it, and on to
        ///   whoever it is delivered to. Returns each ECU reached with the protocol of the bus it was reached
        ///   ON, which is what makes "it crossed protocols" assertable rather than assumed.
        ///
        ///   <para>Written as a WALK and not as per-hop assertions on purpose: every hop can be present
        ///   while the composition still fails - an edge pointing the wrong way, or a system signal that is
        ///   two elements - and only following it end to end catches that.</para>
        /// </summary>
        private static Dictionary<String, String> Consumers(ArxmlNetwork vehicle, String producer)
        {
            var reached = new Dictionary<String, String>(StringComparer.Ordinal);

            foreach (var sent in Out(vehicle, producer, ArxmlRelations.Sends))
            {
                foreach (var systemSignal in Out(vehicle, sent, ArxmlRelations.Implements))
                {
                    foreach (var realisation in In(vehicle, systemSignal, ArxmlRelations.Implements))
                    {
                        if (String.Equals(realisation, sent, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var consumer in Out(vehicle, realisation, ArxmlRelations.DeliversTo))
                        {
                            reached[consumer] = ProtocolOf(vehicle, consumer);
                        }
                    }
                }
            }

            return reached;
        }

        /// <summary>
        ///   The protocol of the bus an ECU is attached to, read from the NETWORK it reaches by
        ///   <c>attachedTo</c> - not the channel, which would answer the same thing twice for an ECU on two
        ///   channels of one bus.
        /// </summary>
        private static String ProtocolOf(ArxmlNetwork vehicle, String ecu)
        {
            foreach (var target in Out(vehicle, ecu, ArxmlRelations.AttachedTo))
            {
                var element = vehicle.Elements.SingleOrDefault(e => e.Path == target);
                if (element != null && element.Kind == ArxmlKinds.Network)
                {
                    return element[ArxmlProperties.Protocol];
                }
            }

            Assert.Fail("'" + ecu + "' is attached to no network, so which bus it was reached on cannot " +
                "be said");
            return null;
        }

        private static IEnumerable<String> Out(ArxmlNetwork vehicle, String from, String type)
        {
            return vehicle.Relations
                .Where(r => r.FromPath == from && r.Type == type)
                .Select(r => r.ToPath)
                .Distinct(StringComparer.Ordinal);
        }

        private static IEnumerable<String> In(ArxmlNetwork vehicle, String to, String type)
        {
            return vehicle.Relations
                .Where(r => r.ToPath == to && r.Type == type)
                .Select(r => r.FromPath)
                .Distinct(StringComparer.Ordinal);
        }

        #endregion

        #region the fixtures

        /// <summary>The three extracts as ONE source, in the order a job would carry them.</summary>
        private static ArxmlNetwork Vehicle()
        {
            var reader = new ArxmlReader();
            reader.Add("chassis-can.arxml", Extract(CanExtract));
            reader.Add("chassis-fr.arxml", Extract(FlexRayExtract));
            reader.Add("backbone-eth.arxml", Extract(EthernetExtract));
            return reader.Complete();
        }

        /// <summary>
        ///   One fixture with the shared catalogue spliced in. Substituted rather than written out three
        ///   times so the three extracts provably declare the SAME path: a typo in one copy would turn the
        ///   join this file exists to test into three unrelated signals, and every assertion here would
        ///   still be readable while asserting nothing.
        /// </summary>
        private static String Extract(String fixture)
        {
            Assert.IsTrue(fixture.Contains("__SHARED__", StringComparison.Ordinal),
                "every fixture here carries the shared catalogue, as a real extract does");
            return fixture.Replace("__SHARED__", SharedPackage, StringComparison.Ordinal);
        }

        /// <summary>
        ///   The shared catalogue every extract repeats, which is what a real set of extracts looks like: the
        ///   standardised packages are common to all of them by construction. Written once here and
        ///   substituted into each fixture, so the three really do declare the SAME path.
        /// </summary>
        private const String SharedPackage = """
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_WheelSpeed</SHORT-NAME>
                      <DESC><L-2 L="EN">Wheel speed, produced on the chassis bus and consumed vehicle wide.</L-2></DESC>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
            """;

        /// <summary>The CAN chassis bus: the PRODUCER of the wheel speed, over a frame with an identifier.</summary>
        private const String CanExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
            __SHARED__
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed_Can</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_WheelSpeed</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Can</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-FRAME>
                      <SHORT-NAME>FRM_WheelSpeed</SHORT-NAME>
                      <FRAME-LENGTH>8</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>PFM_WheelSpeed</SHORT-NAME>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_WheelSpeed</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </CAN-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Brake</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <CAN-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>BRAKE_CAN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-SIGNAL-PORT>
                              <SHORT-NAME>BRAKE_TX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </I-SIGNAL-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </CAN-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <CAN-CLUSTER>
                      <SHORT-NAME>CHASSIS_CAN</SHORT-NAME>
                      <CAN-CLUSTER-VARIANTS>
                        <CAN-CLUSTER-CONDITIONAL>
                          <BAUDRATE>500000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <CAN-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_CAN</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="CAN-COMMUNICATION-CONNECTOR">/Ecus/ECU_Brake/BRAKE_CAN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <CAN-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_WheelSpeed</SHORT-NAME>
                                  <FRAME-REF DEST="CAN-FRAME">/Frames/FRM_WheelSpeed</FRAME-REF>
                                  <IDENTIFIER>419</IDENTIFIER>
                                  <CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>
                                </CAN-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                              <I-SIGNAL-TRIGGERINGS>
                                <I-SIGNAL-TRIGGERING>
                                  <SHORT-NAME>ST_WheelSpeed_Can</SHORT-NAME>
                                  <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Can</I-SIGNAL-REF>
                                  <I-SIGNAL-PORT-REFS>
                                    <I-SIGNAL-PORT-REF DEST="I-SIGNAL-PORT">/Ecus/ECU_Brake/BRAKE_CAN/BRAKE_TX</I-SIGNAL-PORT-REF>
                                  </I-SIGNAL-PORT-REFS>
                                </I-SIGNAL-TRIGGERING>
                              </I-SIGNAL-TRIGGERINGS>
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

        /// <summary>The FlexRay chassis bus: a CONSUMER, over a scheduled frame and a redundant pair of channels.</summary>
        private const String FlexRayExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
            __SHARED__
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed_Fr</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Chassis</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed_Fr</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Fr</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-FRAME>
                      <SHORT-NAME>FRM_Chassis</SHORT-NAME>
                      <FRAME-LENGTH>16</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>PFM_Chassis</SHORT-NAME>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Chassis</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </FLEXRAY-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Steering</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>STEER_FR</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-SIGNAL-PORT>
                              <SHORT-NAME>STEER_RX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-SIGNAL-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>CHASSIS_FR</SHORT-NAME>
                      <FLEXRAY-CLUSTER-VARIANTS>
                        <FLEXRAY-CLUSTER-CONDITIONAL>
                          <BAUDRATE>10000000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_FR_A</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/Ecus/ECU_Steering/STEER_FR</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <I-SIGNAL-TRIGGERINGS>
                                <I-SIGNAL-TRIGGERING>
                                  <SHORT-NAME>ST_WheelSpeed_Fr</SHORT-NAME>
                                  <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Fr</I-SIGNAL-REF>
                                  <I-SIGNAL-PORT-REFS>
                                    <I-SIGNAL-PORT-REF DEST="I-SIGNAL-PORT">/Ecus/ECU_Steering/STEER_FR/STEER_RX</I-SIGNAL-PORT-REF>
                                  </I-SIGNAL-PORT-REFS>
                                </I-SIGNAL-TRIGGERING>
                              </I-SIGNAL-TRIGGERINGS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_FR_B</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/Ecus/ECU_Steering/STEER_FR</COMMUNICATION-CONNECTOR-REF>
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

        /// <summary>The Ethernet backbone: a CONSUMER, with no frame layer and a VLAN for a channel.</summary>
        private const String EthernetExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
            __SHARED__
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed_Eth</SHORT-NAME>
                      <LENGTH>32</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Backbone</SHORT-NAME>
                      <LENGTH>64</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed_Eth</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Eth</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ECU_Drive</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>DRIVE_ETH</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-SIGNAL-PORT>
                              <SHORT-NAME>DRIVE_RX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-SIGNAL-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </ETHERNET-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <ETHERNET-CLUSTER>
                      <SHORT-NAME>BACKBONE</SHORT-NAME>
                      <ETHERNET-CLUSTER-VARIANTS>
                        <ETHERNET-CLUSTER-CONDITIONAL>
                          <BAUDRATE>1000</BAUDRATE>
                          <PHYSICAL-CHANNELS>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_BACKBONE</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/ECU_Drive/DRIVE_ETH</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_Backbone</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Backbone</I-PDU-REF>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                              <I-SIGNAL-TRIGGERINGS>
                                <I-SIGNAL-TRIGGERING>
                                  <SHORT-NAME>ST_WheelSpeed_Eth</SHORT-NAME>
                                  <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed_Eth</I-SIGNAL-REF>
                                  <I-SIGNAL-PORT-REFS>
                                    <I-SIGNAL-PORT-REF DEST="I-SIGNAL-PORT">/Ecus/ECU_Drive/DRIVE_ETH/DRIVE_RX</I-SIGNAL-PORT-REF>
                                  </I-SIGNAL-PORT-REFS>
                                </I-SIGNAL-TRIGGERING>
                              </I-SIGNAL-TRIGGERINGS>
                              <VLAN>
                                <SHORT-NAME>VLAN_BACKBONE</SHORT-NAME>
                                <VLAN-IDENTIFIER>7</VLAN-IDENTIFIER>
                              </VLAN>
                            </ETHERNET-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </ETHERNET-CLUSTER-CONDITIONAL>
                      </ETHERNET-CLUSTER-VARIANTS>
                    </ETHERNET-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        #endregion
    }
}
