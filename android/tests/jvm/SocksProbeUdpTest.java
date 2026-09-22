package com.trelay.android;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicReference;

public final class SocksProbeUdpTest {
    public static void main(String[] args) throws Exception {
        ServerSocket controlServer = new ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"));
        DatagramSocket relay = new DatagramSocket(0, InetAddress.getByName("127.0.0.1"));
        AtomicReference<Throwable> serverFailure = new AtomicReference<>();
        Thread server = new Thread(() -> {
            try (Socket control = controlServer.accept()) {
                InputStream in = control.getInputStream();
                OutputStream out = control.getOutputStream();
                byte[] hello = SocksProbe.readExact(in, 3);
                require(Arrays.equals(hello, new byte[] {5, 1, 0}), "bad SOCKS greeting");
                out.write(new byte[] {5, 0});
                byte[] associate = SocksProbe.readExact(in, 10);
                require(associate[0] == 5 && associate[1] == 3, "not UDP ASSOCIATE");
                int port = relay.getLocalPort();
                out.write(new byte[] {5, 0, 0, 1, 127, 0, 0, 1, (byte) (port >> 8), (byte) port});
                out.flush();

                byte[] data = new byte[8192];
                DatagramPacket packet = new DatagramPacket(data, data.length);
                relay.receive(packet);
                require(data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 3, "bad UDP envelope");
                int hostLength = data[4] & 0xff;
                String host = new String(data, 5, hostLength, StandardCharsets.US_ASCII);
                int portOffset = 5 + hostLength;
                int destinationPort = ((data[portOffset] & 0xff) << 8) | (data[portOffset + 1] & 0xff);
                int payloadOffset = portOffset + 2;
                String request = new String(data, payloadOffset, packet.getLength() - payloadOffset, StandardCharsets.US_ASCII);
                require("sip.example.com".equals(host), "wrong SIP host: " + host);
                require(destinationPort == 5060, "wrong SIP port: " + destinationPort);
                require(request.startsWith("OPTIONS sip:sip.example.com SIP/2.0\r\nVia: SIP/2.0/UDP"), "bad SIP OPTIONS");

                byte[] answer = "SIP/2.0 401 Unauthorized\r\nContent-Length: 0\r\n\r\n".getBytes(StandardCharsets.US_ASCII);
                byte[] response = Arrays.copyOf(data, payloadOffset + answer.length);
                System.arraycopy(answer, 0, response, payloadOffset, answer.length);
                relay.send(new DatagramPacket(response, response.length, packet.getSocketAddress()));
            } catch (Throwable error) {
                serverFailure.set(error);
            }
        }, "fake-socks-sip");
        server.start();

        SocksProbe.Result result = SocksProbe.probe(controlServer.getLocalPort(), "udp://sip.example.com:5060", 3000);
        server.join(4000);
        relay.close();
        controlServer.close();
        if (serverFailure.get() != null) throw new AssertionError("fake server failed", serverFailure.get());
        require(result.ok, "probe failed: " + result.failure);
        testRelayLearnsSipTarget();
        System.out.println("SocksProbeUdpTest OK");
    }

    private static void testRelayLearnsSipTarget() throws Exception {
        AtomicReference<String> learned = new AtomicReference<>();
        RouteRelay route = new RouteRelay("test", 0, new RouteRelay.Listener() {
            @Override public RouteRelay.Target[] targets() { return new RouteRelay.Target[0]; }
            @Override public void failure(RouteRelay.Target target, RouteRelay.Failure kind) { }
            @Override public void closed(RouteRelay.Closed info) { }
            @Override public void sipTarget(String host, int port, boolean udp) {
                learned.set((udp ? "udp://" : "tcp://") + host + ":" + port);
            }
            @Override public void log(String line) { }
        });
        try (Socket control = new Socket("127.0.0.1", route.port);
             DatagramSocket client = new DatagramSocket(new InetSocketAddress("127.0.0.1", 0))) {
            InputStream input = control.getInputStream();
            OutputStream output = control.getOutputStream();
            output.write(new byte[] {5, 1, 0});
            output.flush();
            require(Arrays.equals(SocksProbe.readExact(input, 2), new byte[] {5, 0}), "relay greeting failed");
            output.write(new byte[] {5, 3, 0, 1, 0, 0, 0, 0, 0, 0});
            output.flush();
            SocksProbe.readExact(input, 10);

            String host = "sip.example.com";
            byte[] name = host.getBytes(StandardCharsets.US_ASCII);
            byte[] payload = "REGISTER sip:sip.example.com SIP/2.0\r\n\r\n".getBytes(StandardCharsets.US_ASCII);
            byte[] packet = new byte[7 + name.length + payload.length];
            packet[3] = 3;
            packet[4] = (byte) name.length;
            System.arraycopy(name, 0, packet, 5, name.length);
            int portOffset = 5 + name.length;
            packet[portOffset] = (byte) (5060 >> 8);
            packet[portOffset + 1] = (byte) 5060;
            System.arraycopy(payload, 0, packet, portOffset + 2, payload.length);
            client.send(new DatagramPacket(packet, packet.length, InetAddress.getByName("127.0.0.1"), route.port));
            long deadline = System.currentTimeMillis() + 2000;
            while (learned.get() == null && System.currentTimeMillis() < deadline) Thread.sleep(10);
            require("udp://sip.example.com:5060".equals(learned.get()), "relay did not learn SIP target: " + learned.get());
        } finally {
            route.close();
        }
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
