// MIT License
//
// IntegrationsArxmlEthernetTest.cs
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
    ///   The AUTOSAR reader on ETHERNET (feature arxml-vehicle-model, step 2), which is not "one more
    ///   entry in the protocol table" - the structure below the cluster is different in kind.
    ///
    ///   <para>What these tests are really about: an Ethernet cluster has NO FRAME LAYER. Its channel
    ///   carries PDU triggerings directly, so a traversal that reaches signals through frames finds
    ///   nothing, and a channel is a VLAN rather than redundancy, so several channels under one cluster
    ///   are several broadcast domains an ECU can be on one of.</para>
    ///
    ///   <para>Every fixture here is HAND-AUTHORED and describes an invented network. No content derived
    ///   from a real manufacturer's export appears in this repository in any form.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsArxmlEthernetTest
    {
        #region the cluster, its VLANs, and the missing frame layer

        [TestMethod]
        public void AnEthernetClusterIsANetwork_WithItsVlansAsChannels()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            var bus = Element(network, "/Clusters/BACKBONE");
            Assert.AreEqual(ArxmlKinds.Network, bus.Kind);
            Assert.AreEqual(ArxmlProperties.EthernetProtocol, bus[ArxmlProperties.Protocol],
                "the protocol is what tells a traversal which shape it is standing in: on this one there " +
                "are no frames to reach a signal through");
            Assert.AreEqual("1000", bus[ArxmlProperties.Baudrate],
                "the bit rate is protocol-neutral in the standard and is read once for every bus");

            var channels = network.Elements
                .Where(e => e.Kind == ArxmlKinds.Channel)
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ToList();
            CollectionAssert.AreEqual(
                new[] { "/Clusters/BACKBONE/CH_DIAG", "/Clusters/BACKBONE/CH_SENSORS" },
                channels.Select(c => c.Path).ToArray(),
                "each VLAN is a channel of its own. On Ethernet this is the whole reason the channel had " +
                "to become an element: these are distinct broadcast domains, not redundancy");

            var sensors = Element(network, "/Clusters/BACKBONE/CH_SENSORS");
            Assert.AreEqual("12", sensors[ArxmlProperties.VlanId]);
            Assert.AreEqual("VLAN_SENSORS", sensors[ArxmlProperties.VlanName],
                "the VLAN's name comes from the VLAN element inside the channel rather than from a field " +
                "on it, which is where the standard puts it");
            Assert.AreEqual(ArxmlProperties.EthernetProtocol, sensors[ArxmlProperties.Protocol]);
        }

        /// <summary>
        ///   NO FRAMES, and the signals still reachable. A reader that treated the missing frame layer as
        ///   an error would refuse a legal extract; one that looked for a frame element called "" would
        ///   quietly find nothing on every bus.
        /// </summary>
        [TestMethod]
        public void AnEthernetBusHasNoFrames_AndItsSignalsAreStillReachable()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0, network.Elements.Count(e => e.Kind == ArxmlKinds.Frame),
                "an Ethernet channel carries PDU triggerings directly: the socket layer does what a frame " +
                "does elsewhere, so a frame here would be an element the standard does not describe");

            // PDU to signal to system signal, with nothing between the channel and the PDU.
            CollectionAssert.AreEqual(new[] { "/ISignals/SIG_WheelSpeed" },
                Targets(network, "/Pdus/PDU_Sensors", ArxmlRelations.Contains).ToArray());
            CollectionAssert.AreEqual(new[] { "/Shared/SYS_WheelSpeed" },
                Targets(network, "/ISignals/SIG_WheelSpeed", ArxmlRelations.Implements).ToArray(),
                "and the system signal is reached, which is what makes this bus joinable to another one");
        }

        /// <summary>
        ///   The flow comes from the PDU triggering, which is the protocol-neutral object: a PDU triggering
        ///   names the ports it crosses exactly as a frame triggering does, and on Ethernet it is the only
        ///   flow there is.
        /// </summary>
        [TestMethod]
        public void TheFlowOnAnEthernetBusComesFromThePduTriggering()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            CollectionAssert.AreEqual(new[] { "/Pdus/PDU_Sensors" },
                Targets(network, "/Ecus/SENSOR_HUB", ArxmlRelations.Sends).ToArray(),
                "the sending ECU is the one whose I-PDU port declares OUT");
            CollectionAssert.AreEqual(new[] { "/Ecus/DRIVE_ECU" },
                Targets(network, "/Pdus/PDU_Sensors", ArxmlRelations.DeliversTo).ToArray(),
                "and the receiving one declares IN, so a path query never traverses an edge backwards");
        }

        /// <summary>
        ///   An ECU attaches to the network and to the CHANNELS IT IS ACTUALLY ON. On Ethernet that is the
        ///   fact the channel edge exists for: a unit on the sensor VLAN must not appear on the diagnostic
        ///   one, or every VLAN question in the graph answers "all of them".
        /// </summary>
        [TestMethod]
        public void AnEcuAttachesToTheVlansItIsOn_AndNotToTheOthers()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            CollectionAssert.AreEqual(
                new[] { "/Clusters/BACKBONE", "/Clusters/BACKBONE/CH_SENSORS" },
                Targets(network, "/Ecus/SENSOR_HUB", ArxmlRelations.AttachedTo).ToArray(),
                "the sensor hub is on the sensor VLAN only");
            CollectionAssert.AreEqual(
                new[]
                {
                    "/Clusters/BACKBONE", "/Clusters/BACKBONE/CH_DIAG", "/Clusters/BACKBONE/CH_SENSORS",
                },
                Targets(network, "/Ecus/DRIVE_ECU", ArxmlRelations.AttachedTo).ToArray(),
                "and the drive ECU is on both, which is what a multi-VLAN unit looks like");
        }

        /// <summary>
        ///   Ethernet is no longer reported as a bus this reader skips. The diagnostic existed so an
        ///   operator was not left inferring a skipped bus from a network that never appeared; leaving it in
        ///   place while reading the bus would be worse than either.
        /// </summary>
        [TestMethod]
        public void AnEthernetClusterIsNoLongerReportedAsUnread()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0, network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnreadCluster),
                "Ethernet is read now: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
            CollectionAssert.DoesNotContain(network.UnreadClusters.Select(u => u.Element).ToArray(),
                "ETHERNET-CLUSTER",
                "and it is off the unread list, which is what the report is built from");
        }

        [TestMethod]
        public void AnEthernetExtractResolvesEveryReferenceItWrites()
        {
            var network = ArxmlReader.Read(EthernetExtract);

            Assert.AreEqual(0,
                network.Diagnostics.Count(d => d.Kind == ArxmlDiagnosticKind.UnresolvedReference),
                "the fixture defines every path it references, so a diagnostic here means the reader " +
                "cannot resolve a reference the standard writes plainly: " + String.Join("; ",
                    network.Diagnostics.Select(d => d.Kind + " " + d.Subject)));
        }

        #endregion

        #region helpers

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

        #endregion

        #region the fixture

        /// <summary>
        ///   An invented Ethernet backbone with TWO VLANs, one ECU on each and one on both, carrying a PDU
        ///   with a signal that implements a shared system signal.
        ///
        ///   <para>Shaped to exercise the three things that make Ethernet different: no frame layer, a
        ///   channel per VLAN, and the flow coming from the PDU triggering's ports. The system signal is
        ///   under /Shared deliberately - it is the join a second bus of another protocol would meet it
        ///   at.</para>
        /// </summary>
        private const String EthernetExtract = """
            <?xml version="1.0" encoding="UTF-8"?>
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Shared</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_WheelSpeed</SHORT-NAME>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_WheelSpeed</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/Shared/SYS_WheelSpeed</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_Sensors</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_WheelSpeed</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_WheelSpeed</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Ecus</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>SENSOR_HUB</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>HUB_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-PDU-PORT>
                              <SHORT-NAME>HUB_TX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </ETHERNET-COMMUNICATION-CONNECTOR>
                      </COMM-CONNECTORS>
                    </ECU-INSTANCE>
                    <ECU-INSTANCE>
                      <SHORT-NAME>DRIVE_ECU</SHORT-NAME>
                      <COMM-CONNECTORS>
                        <ETHERNET-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>DRIVE_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <I-PDU-PORT>
                              <SHORT-NAME>DRIVE_RX</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>IN</COMMUNICATION-DIRECTION>
                            </I-PDU-PORT>
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
                              <SHORT-NAME>CH_SENSORS</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/SENSOR_HUB/HUB_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/DRIVE_ECU/DRIVE_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <PDU-TRIGGERINGS>
                                <PDU-TRIGGERING>
                                  <SHORT-NAME>PT_Sensors</SHORT-NAME>
                                  <I-PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_Sensors</I-PDU-REF>
                                  <I-PDU-PORT-REFS>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/SENSOR_HUB/HUB_CONN/HUB_TX</I-PDU-PORT-REF>
                                    <I-PDU-PORT-REF DEST="I-PDU-PORT">/Ecus/DRIVE_ECU/DRIVE_CONN/DRIVE_RX</I-PDU-PORT-REF>
                                  </I-PDU-PORT-REFS>
                                </PDU-TRIGGERING>
                              </PDU-TRIGGERINGS>
                              <VLAN>
                                <SHORT-NAME>VLAN_SENSORS</SHORT-NAME>
                                <VLAN-IDENTIFIER>12</VLAN-IDENTIFIER>
                              </VLAN>
                            </ETHERNET-PHYSICAL-CHANNEL>
                            <ETHERNET-PHYSICAL-CHANNEL>
                              <SHORT-NAME>CH_DIAG</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="ETHERNET-COMMUNICATION-CONNECTOR">/Ecus/DRIVE_ECU/DRIVE_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <VLAN>
                                <SHORT-NAME>VLAN_DIAG</SHORT-NAME>
                                <VLAN-IDENTIFIER>3</VLAN-IDENTIFIER>
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
