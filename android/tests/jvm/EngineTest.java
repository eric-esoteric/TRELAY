package com.trelay.android;

import java.io.File;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

// End-to-end engine check with real Xray processes: warm backends, relay TCP+UDP, failover, UDP flow move, recovery.
public class EngineTest {
    static String xray;
    static File work;
    static final List<String> events = new ArrayList<>();

    static Process startXray(JSONish config, String name) throws Exception {
        File file = new File(work, name + ".json");
        Files.write(file.toPath(), config.text.getBytes(StandardCharsets.UTF_8));
        ProcessBuilder builder = new ProcessBuilder(xray, "run", "-c", file.getAbsolutePath());
        builder.redirectErrorStream(true);
        builder.redirectOutput(new File(work, name + ".log"));
        return builder.start();
    }

    static final class JSONish { final String text; JSONish(String text) { this.text = text; } }

    static Process upstream(int port) throws Exception {
        return startXray(new JSONish("{\"inbounds\":[{\"listen\":\"127.0.0.1\",\"port\":" + port + ",\"protocol\":\"socks\",\"settings\":{\"udp\":true,\"ip\":\"127.0.0.1\"}}],\"outbounds\":[{\"protocol\":\"freedom\"}]}"), "up" + port);
    }

    public static void main(String[] args) throws Exception {
        xray = args[0];
        work = new File(args[1]);
        work.mkdirs();
        // Local HTTP-ish service (health endpoint + TCP check) and UDP echo.
        ServerSocket service = new ServerSocket(18080, 50, InetAddress.getByName("127.0.0.1"));
        Thread serviceThread = new Thread(() -> {
            while (true) {
                try {
                    Socket client = service.accept();
                    new Thread(() -> {
                        try (Socket s = client) {
                            InputStream in = s.getInputStream();
                            byte[] buffer = new byte[1024];
                            int read = in.read(buffer);
                            if (read > 0) s.getOutputStream().write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok".getBytes());
                            Thread.sleep(100);
                        } catch (Exception ignored) { }
                    }).start();
                } catch (Exception error) { return; }
            }
        });
        serviceThread.setDaemon(true);
        serviceThread.start();
        DatagramSocket echo = new DatagramSocket(new InetSocketAddress("127.0.0.1", 18081));
        Thread echoThread = new Thread(() -> {
            byte[] buffer = new byte[2048];
            while (true) {
                try { DatagramPacket p = new DatagramPacket(buffer, buffer.length); echo.receive(p); echo.send(new DatagramPacket(p.getData(), p.getLength(), p.getSocketAddress())); }
                catch (Exception error) { return; }
            }
        });
        echoThread.setDaemon(true);
        echoThread.start();

        Process upA = upstream(21001);
        Process upB = upstream(21002);
        Process upC = upstream(21003);
        Thread.sleep(800);

        final List<Integer> routerPorts = new ArrayList<>();
        TunnelEngine engine = new TunnelEngine(new TunnelEngine.Platform() {
            @Override public TunnelEngine.Runner startBackend(String serverId, String key, int port) throws Exception {
                Process process = startXray(new JSONish(XrayConfigs.backend(key, port).toString()), "backend-" + port);
                return new TunnelEngine.Runner() {
                    @Override public boolean alive() { return process.isAlive(); }
                    @Override public void stop() { process.destroyForcibly(); }
                };
            }
            @Override public void applyRouter(List<TunnelEngine.RouterGroup> groups, int dnsPort) {
                routerPorts.clear();
                for (TunnelEngine.RouterGroup group : groups) routerPorts.add(group.relayPort);
                log("TEST router applied " + routerPorts + " dns=" + dnsPort);
            }
            @Override public boolean routerAlive() { return true; }
            @Override public void stopRouter() { }
            @Override public boolean interactive() { return true; }
            @Override public void keepAwake(long ms) { }
            @Override public void log(String line) { System.out.println(System.currentTimeMillis() % 100000 + " " + line); }
        });
        engine.addListener(new TunnelEngine.Listener() {
            @Override public void stateChanged() { }
            @Override public void alert(TunnelEngine.Alert alert) { synchronized (events) { events.add(alert.kind + " critical=" + alert.critical); } System.out.println("ALERT>> " + alert.title + " | " + alert.message); }
        });
        Model.Config config = new Model.Config();
        Model.Route route = new Model.Route();
        route.name = "Телефония";
        route.telephony = true;
        route.healthEndpoint = "127.0.0.1:18080";
        route.packages.add("ru.megafon.communicator");
        Model.Server a = new Model.Server(); a.name = "A"; a.link = "socks5://127.0.0.1:21001#A";
        Model.Server b = new Model.Server(); b.name = "B"; b.link = "socks5://127.0.0.1:21002#B";
        Model.Server c = new Model.Server(); c.name = "C"; c.link = "socks5://127.0.0.1:21003#C";
        route.servers.add(a); route.servers.add(b); route.servers.add(c);
        config.routes.add(route);
        engine.start(config);

        waitFor(() -> engine.state() == TunnelEngine.State.READY && active(engine, route.id, a.id), 20000, "ready on A");
        int relay = routerPorts.get(0);
        check(httpVia(relay), "HTTP via relay on A");
        DatagramSocket udpClient = new DatagramSocket();
        Socket control = new Socket("127.0.0.1", relay);
        InetSocketAddress udpRelay = associate(control);
        check(udpEcho(udpClient, udpRelay, "ping-1"), "UDP via relay on A");
        Map<String, TunnelEngine.ServerInfo> info = engine.serverInfo();
        System.out.println("status A=" + info.get(a.id).status + " B=" + info.get(b.id).status + " :: " + engine.statusText());
        check(info.get(b.id).status == TunnelEngine.ServerStatus.STANDBY, "B warm standby");
        check(info.get(c.id).status == TunnelEngine.ServerStatus.COLD, "C cold reserve (saves memory)");

        long killed = System.currentTimeMillis();
        upA.destroyForcibly();
        waitFor(() -> active(engine, route.id, b.id), 15000, "failover to B");
        System.out.println("failover took " + (System.currentTimeMillis() - killed) + " ms");
        check(httpVia(relay), "HTTP via relay after failover");
        check(udpEcho(udpClient, udpRelay, "ping-2"), "UDP flow survived failover");
        waitFor(() -> engine.serverInfo().get(c.id).status == TunnelEngine.ServerStatus.STANDBY, 20000, "C warmed up after failure");

        upA = upstream(21001);
        long restarted = System.currentTimeMillis();
        waitFor(() -> active(engine, route.id, a.id), 60000, "recovery to A");
        System.out.println("recovery took " + (System.currentTimeMillis() - restarted) + " ms");
        check(httpVia(relay), "HTTP via relay after recovery");

        upA.destroyForcibly(); upB.destroyForcibly();
        waitFor(() -> active(engine, route.id, c.id), 30000, "failover to warmed reserve C");
        check(httpVia(relay), "HTTP via C");
        upC.destroyForcibly();
        waitFor(() -> engine.state() == TunnelEngine.State.ERROR, 30000, "route lost");
        Thread.sleep(6500);
        System.out.println("EVENTS " + events);
        check(events.toString().contains("ROUTE_LOST critical=true"), "loud route-lost alert");
        engine.stop();
        waitFor(() -> engine.state() == TunnelEngine.State.OFF, 10000, "stopped");
        control.close();
        System.out.println("ENGINE TEST PASSED");
        System.exit(0);
    }

    static boolean active(TunnelEngine engine, String routeId, String serverId) {
        TunnelEngine.RouteInfo info = engine.routeInfo().get(routeId);
        return info != null && info.working && serverId.equals(info.activeServerId);
    }

    interface Cond { boolean ok() throws Exception; }

    static void waitFor(Cond cond, long ms, String what) throws Exception {
        long deadline = System.currentTimeMillis() + ms;
        while (System.currentTimeMillis() < deadline) { if (cond.ok()) { System.out.println("OK: " + what); return; } Thread.sleep(100); }
        System.out.println("FAILED waiting: " + what);
        System.exit(2);
    }

    static void check(boolean value, String what) {
        System.out.println((value ? "OK: " : "FAILED: ") + what);
        if (!value) System.exit(3);
    }

    static boolean httpVia(int relay) {
        try (Socket socket = new Socket("127.0.0.1", relay)) {
            socket.setSoTimeout(5000);
            InputStream in = socket.getInputStream(); OutputStream out = socket.getOutputStream();
            out.write(new byte[] {5, 1, 0}); SocksProbe.readExact(in, 2);
            out.write(SocksProbe.connectRequest("127.0.0.1", 18080));
            byte[] head = SocksProbe.readExact(in, 4);
            if (head[1] != 0) return false;
            SocksProbe.skipAddress(in, head[3]);
            out.write("GET / HTTP/1.1\r\nHost: x\r\n\r\n".getBytes());
            byte[] reply = SocksProbe.readExact(in, 12);
            return new String(reply).startsWith("HTTP/1.1 200");
        } catch (Exception error) { System.out.println("http error " + error); return false; }
    }

    static InetSocketAddress associate(Socket control) throws Exception {
        InputStream in = control.getInputStream(); OutputStream out = control.getOutputStream();
        out.write(new byte[] {5, 1, 0}); SocksProbe.readExact(in, 2);
        out.write(new byte[] {5, 3, 0, 1, 0, 0, 0, 0, 0, 0});
        byte[] reply = SocksProbe.readExact(in, 10);
        return new InetSocketAddress(InetAddress.getByAddress(new byte[] {reply[4], reply[5], reply[6], reply[7]}), ((reply[8] & 0xff) << 8) | (reply[9] & 0xff));
    }

    static boolean udpEcho(DatagramSocket socket, InetSocketAddress relay, String text) {
        try {
            socket.setSoTimeout(3000);
            byte[] payload = text.getBytes();
            byte[] packet = new byte[10 + payload.length];
            packet[3] = 1; packet[4] = 127; packet[7] = 1; packet[8] = (byte) (18081 >> 8); packet[9] = (byte) 18081;
            System.arraycopy(payload, 0, packet, 10, payload.length);
            for (int attempt = 0; attempt < 3; attempt++) {
                socket.send(new DatagramPacket(packet, packet.length, relay));
                byte[] buffer = new byte[2048];
                DatagramPacket reply = new DatagramPacket(buffer, buffer.length);
                try { socket.receive(reply); } catch (java.net.SocketTimeoutException timeout) { continue; }
                String body = new String(buffer, 10, reply.getLength() - 10);
                if (body.equals(text)) return true;
            }
            return false;
        } catch (Exception error) { System.out.println("udp error " + error); return false; }
    }
}
