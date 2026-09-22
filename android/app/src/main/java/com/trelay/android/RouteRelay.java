package com.trelay.android;

import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

// Port of Windows RouteRelay: a stable loopback SOCKS5 entry for one route. The front TUN
// router always points here, so switching servers never restarts it. New TCP connections and
// UDP flows go to the currently selected backend; UDP flows are moved on failover.
final class RouteRelay implements Closeable {
    static final class Target {
        final String id;
        final String name;
        private volatile int port;
        Target(String id, String name) { this.id = id; this.name = name; }
        int port() { return port; }
        void port(int value) { port = value; }
    }

    enum Failure { LOCAL_CONNECT, HANDSHAKE, NO_DATA }

    static final class Closed {
        String route, via, destination, closedBy;
        long lifetimeMs, idleMs, up, down;
        long openedAt;
    }

    interface Listener {
        Target[] targets();
        void failure(Target target, Failure kind);
        void closed(Closed info);
        void sipTarget(String host, int port, boolean udp);
        void log(String line);
    }

    private static final InetAddress LOOPBACK;
    static {
        InetAddress loopback;
        try { loopback = InetAddress.getByAddress("127.0.0.1", new byte[] {127, 0, 0, 1}); }
        catch (Exception error) { throw new IllegalStateException(error); }
        LOOPBACK = loopback;
    }

    private static final AtomicInteger THREADS = new AtomicInteger();
    private static final ExecutorService POOL = Executors.newCachedThreadPool(r -> {
        Thread thread = new Thread(r, "trelay-relay-" + THREADS.incrementAndGet());
        thread.setDaemon(true);
        return thread;
    });

    final String name;
    final int port;
    private final Listener listener;
    private final Object sync = new Object();
    private final Set<Connection> connections = new HashSet<>();
    private final Set<Socket> controls = new HashSet<>();
    private final Map<InetSocketAddress, UdpSession> sessions = new HashMap<>();
    private final ServerSocket server;
    private final DatagramSocket udp;
    private int associations;
    private volatile boolean closed;
    private final AtomicLong lastTraffic = new AtomicLong();

    /** True when application data passed through this route recently (used to pace probes). */
    boolean recentTraffic(long windowMs) { return System.currentTimeMillis() - lastTraffic.get() < windowMs; }

    RouteRelay(String name, int preferredPort, Listener listener) throws IOException {
        this.name = name;
        this.listener = listener;
        ServerSocket tcp = new ServerSocket();
        DatagramSocket datagram = null;
        try {
            tcp.setReuseAddress(false);
            tcp.bind(new InetSocketAddress(LOOPBACK, preferredPort), 512);
            port = tcp.getLocalPort();
            datagram = new DatagramSocket(null);
            datagram.setReuseAddress(false);
            datagram.bind(new InetSocketAddress(LOOPBACK, port));
        } catch (IOException error) {
            SocksProbe.closeQuietly(tcp);
            if (datagram != null) datagram.close();
            throw error;
        }
        server = tcp;
        udp = datagram;
        start("trelay-relay-accept-" + name, this::acceptLoop);
        start("trelay-relay-udp-" + name, this::udpLoop);
        start("trelay-relay-sweep-" + name, this::sweepLoop);
    }

    private static void start(String name, Runnable body) {
        Thread thread = new Thread(body, name);
        thread.setDaemon(true);
        thread.start();
    }

    int connectionCount(Target target) {
        synchronized (sync) {
            int count = 0;
            for (Connection connection : connections) if (connection.target == target) count++;
            for (UdpSession session : sessions.values()) if (session.target == target) count++;
            return count;
        }
    }

    int totalConnections() { synchronized (sync) { return connections.size() + sessions.size(); } }

    /** True when an application connection to this destination opened after the given time is still alive. */
    boolean hasLiveConnection(String destination, long openedAfterMs) {
        synchronized (sync) {
            for (Connection connection : connections)
                if (connection.destination.equals(destination) && connection.openedAt >= openedAfterMs) return true;
        }
        return false;
    }

    /** Telephony failover: drop everything so the SIP client re-registers from the new server at once. */
    int evacuateAll() {
        List<Connection> tcp;
        List<UdpSession> flows;
        synchronized (sync) {
            tcp = new ArrayList<>(connections);
            flows = new ArrayList<>(sessions.values());
        }
        for (Connection connection : tcp) connection.close(true);
        for (UdpSession session : flows) session.unbind(true);
        return tcp.size() + flows.size();
    }

    /** The backend is gone: reset its TCP connections so applications reconnect at once, move its UDP flows. */
    int evacuate(Target target) {
        List<Connection> tcp = new ArrayList<>();
        List<UdpSession> flows = new ArrayList<>();
        synchronized (sync) {
            for (Connection connection : connections) if (connection.target == target) tcp.add(connection);
            for (UdpSession session : sessions.values()) if (session.target == target) flows.add(session);
        }
        for (Connection connection : tcp) connection.close(true);
        for (UdpSession session : flows) session.unbind(true);
        return tcp.size() + flows.size();
    }

    private void acceptLoop() {
        while (!closed) {
            try {
                Socket client = server.accept();
                if (closed) { SocksProbe.closeQuietly(client); return; }
                POOL.execute(() -> handleSafe(client));
            } catch (IOException error) {
                if (closed) return;
                sleep(20);
            } catch (RuntimeException error) {
                if (closed) return;
                listener.log("RELAY_ACCEPT_ERROR port=" + port + " " + error.getClass().getSimpleName());
                sleep(20);
            }
        }
    }

    private void handleSafe(Socket client) {
        try { handle(client); }
        catch (Exception ignored) { }
        finally { SocksProbe.closeQuietly(client); }
    }

    private void handle(Socket client) throws IOException {
        client.setTcpNoDelay(true);
        client.setSoTimeout(15000);
        InputStream input = client.getInputStream();
        OutputStream output = client.getOutputStream();
        byte[] greeting = SocksProbe.readExact(input, 2);
        if (greeting[0] != 5 || greeting[1] == 0) return;
        byte[] methods = SocksProbe.readExact(input, greeting[1] & 0xff);
        boolean noAuth = false;
        for (byte method : methods) if (method == 0) noAuth = true;
        if (!noAuth) { output.write(new byte[] {5, (byte) 0xFF}); return; }
        output.write(new byte[] {5, 0});
        output.flush();
        byte[] request = readRequest(input);
        if (request == null) return;
        client.setSoTimeout(0);
        if (request[1] == 1) connect(client, output, request);
        else if (request[1] == 3) associate(client, input, output);
        else output.write(reply((byte) 7));
    }

    private static byte[] reply(byte code) { return new byte[] {5, code, 0, 1, 0, 0, 0, 0, 0, 0}; }

    private static byte[] readRequest(InputStream input) throws IOException {
        byte[] head = SocksProbe.readExact(input, 4);
        if (head[0] != 5 || head[2] != 0) return null;
        byte[] address;
        if (head[3] == 1) address = SocksProbe.readExact(input, 4);
        else if (head[3] == 4) address = SocksProbe.readExact(input, 16);
        else if (head[3] == 3) {
            byte[] length = SocksProbe.readExact(input, 1);
            if (length[0] == 0) return null;
            byte[] host = SocksProbe.readExact(input, length[0] & 0xff);
            address = new byte[1 + host.length];
            address[0] = length[0];
            System.arraycopy(host, 0, address, 1, host.length);
        } else return null;
        byte[] portBytes = SocksProbe.readExact(input, 2);
        byte[] request = new byte[4 + address.length + 2];
        System.arraycopy(head, 0, request, 0, 4);
        System.arraycopy(address, 0, request, 4, address.length);
        System.arraycopy(portBytes, 0, request, 4 + address.length, 2);
        return request;
    }

    static String describe(byte[] request) {
        try {
            int port = ((request[request.length - 2] & 0xff) << 8) | (request[request.length - 1] & 0xff);
            String host;
            if (request[3] == 1) host = InetAddress.getByAddress(java.util.Arrays.copyOfRange(request, 4, 8)).getHostAddress();
            else if (request[3] == 4) host = "[" + InetAddress.getByAddress(java.util.Arrays.copyOfRange(request, 4, 20)).getHostAddress() + "]";
            else host = new String(request, 5, request[4] & 0xff, StandardCharsets.US_ASCII);
            return host + ":" + port;
        } catch (Exception error) { return "?"; }
    }

    private static byte[] readReply(InputStream input) throws IOException {
        byte[] head = SocksProbe.readExact(input, 4);
        if (head[0] != 5) throw new IOException("bad SOCKS reply");
        int length;
        byte[] nameLength = null;
        if (head[3] == 1) length = 4;
        else if (head[3] == 4) length = 16;
        else if (head[3] == 3) { nameLength = SocksProbe.readExact(input, 1); length = nameLength[0] & 0xff; }
        else throw new IOException("bad SOCKS reply address");
        byte[] rest = SocksProbe.readExact(input, length + 2);
        byte[] reply = new byte[4 + (nameLength == null ? 0 : 1) + rest.length];
        System.arraycopy(head, 0, reply, 0, 4);
        int offset = 4;
        if (nameLength != null) reply[offset++] = nameLength[0];
        System.arraycopy(rest, 0, reply, offset, rest.length);
        return reply;
    }

    private Target[] waitForTargets() {
        long deadline = System.currentTimeMillis() + 12000;
        while (!closed) {
            Target[] targets;
            try { targets = listener.targets(); } catch (Exception error) { targets = null; }
            if (targets == null) targets = new Target[0];
            if (targets.length > 0 || System.currentTimeMillis() > deadline) return targets;
            sleep(100);
        }
        return new Target[0];
    }

    private static final class Opened {
        Socket socket;
        byte[] reply;
    }

    // Only local failures (refused port, broken handshake) are reported: they mean the backend is unusable.
    private Opened openBackend(Target target, byte[] request) {
        Opened result = new Opened();
        Socket upstream = new Socket();
        boolean connected = false;
        try {
            upstream.setTcpNoDelay(true);
            try { upstream.connect(new InetSocketAddress(LOOPBACK, target.port()), 1500); }
            catch (IOException error) { listener.failure(target, Failure.LOCAL_CONNECT); SocksProbe.closeQuietly(upstream); return result; }
            connected = true;
            upstream.setSoTimeout(5000);
            InputStream input = upstream.getInputStream();
            OutputStream output = upstream.getOutputStream();
            output.write(new byte[] {5, 1, 0});
            output.flush();
            byte[] hello = SocksProbe.readExact(input, 2);
            if (hello[0] != 5 || hello[1] != 0) throw new IOException("backend refused SOCKS");
            output.write(request);
            output.flush();
            result.reply = readReply(input);
            if (result.reply[1] != 0) { SocksProbe.closeQuietly(upstream); return result; }
            upstream.setSoTimeout(0);
            result.socket = upstream;
            return result;
        } catch (Exception error) {
            if (connected) listener.failure(target, Failure.HANDSHAKE);
            SocksProbe.closeQuietly(upstream);
            result.reply = null;
            return result;
        }
    }

    private void connect(Socket client, OutputStream output, byte[] request) throws IOException {
        SocksProbe.HostPort destination = destination(request, request.length);
        if (destination != null && SocksProbe.isSipPort(destination.port))
            listener.sipTarget(destination.host, destination.port, false);
        Target[] targets = waitForTargets();
        byte[] backendReply = null;
        for (int i = 0; i < Math.min(4, targets.length); i++) {
            if (closed) return;
            Target target = targets[i];
            Opened opened = openBackend(target, request);
            if (opened.socket == null) {
                // A SOCKS error from a live backend concerns the destination, not the tunnel.
                if (opened.reply != null) { backendReply = opened.reply; break; }
                continue;
            }
            Connection connection = new Connection(client, opened.socket, target, describe(request));
            synchronized (sync) {
                if (closed) { SocksProbe.closeQuietly(opened.socket); return; }
                connections.add(connection);
            }
            try {
                output.write(opened.reply);
                output.flush();
                connection.run();
            } finally {
                synchronized (sync) { connections.remove(connection); }
                connection.close(false);
                if (connection.closedByBackendWithoutData() && !connection.closedLocally) listener.failure(target, Failure.NO_DATA);
                long lifetime = System.currentTimeMillis() - connection.openedAt;
                if (lifetime >= 60000) {
                    Closed info = connection.describe(name);
                    listener.log("RELAY_CLOSED route=" + name + " via=" + info.via + " dst=" + info.destination + " closedBy=" + info.closedBy +
                            " lifetime=" + info.lifetimeMs / 1000 + "s idleBeforeClose=" + info.idleMs / 1000 + "s up=" + info.up + " down=" + info.down);
                    if (!closed) listener.closed(info);
                }
            }
            return;
        }
        try { output.write(backendReply != null ? backendReply : reply((byte) 1)); output.flush(); } catch (IOException ignored) { }
    }

    private void associate(Socket client, InputStream input, OutputStream output) {
        byte[] response = reply((byte) 0);
        response[4] = 127; response[7] = 1;
        response[8] = (byte) (port >> 8); response[9] = (byte) port;
        synchronized (sync) {
            if (closed) return;
            controls.add(client);
            associations++;
        }
        try {
            output.write(response);
            output.flush();
            byte[] discard = new byte[512];
            while (input.read(discard) >= 0) { }
        } catch (IOException ignored) {
        } finally {
            List<UdpSession> closing = null;
            synchronized (sync) {
                controls.remove(client);
                if (--associations <= 0) {
                    associations = 0;
                    closing = new ArrayList<>(sessions.values());
                    sessions.clear();
                }
            }
            if (closing != null) for (UdpSession session : closing) session.dispose();
        }
    }

    private void udpLoop() {
        byte[] buffer = new byte[65535];
        while (!closed) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try { udp.receive(packet); }
            catch (IOException error) { if (closed) return; continue; }
            int length = packet.getLength();
            // SOCKS5 UDP header: RSV(2)=0, FRAG=0, ATYP, ADDR, PORT. Fragments are not supported.
            if (!packet.getAddress().isLoopbackAddress() || length < 10 || buffer[0] != 0 || buffer[1] != 0 || buffer[2] != 0) continue;
            InetSocketAddress source = (InetSocketAddress) packet.getSocketAddress();
            UdpSession session;
            synchronized (sync) {
                if (closed || associations == 0) continue;
                session = sessions.get(source);
                if (session == null) {
                    if (sessions.size() >= 4096) continue;
                    session = new UdpSession(source);
                    sessions.put(source, session);
                }
            }
            session.send(java.util.Arrays.copyOf(buffer, length));
        }
    }

    private void sweepLoop() {
        while (!closed) {
            sleep(30000);
            List<UdpSession> idle = new ArrayList<>();
            synchronized (sync) {
                if (closed) return;
                // SIP clients keep a UDP binding alive with sparse keep-alives; five minutes is safe.
                long limit = System.currentTimeMillis() - TimeUnit.MINUTES.toMillis(5);
                for (UdpSession session : new ArrayList<>(sessions.values()))
                    if (session.lastActivity.get() < limit) { idle.add(session); sessions.remove(session.client); }
            }
            for (UdpSession session : idle) session.dispose();
        }
    }

    private void sendToClient(InetSocketAddress client, byte[] data, int length) {
        try { udp.send(new DatagramPacket(data, length, client)); } catch (IOException ignored) { }
    }

    @Override public void close() {
        List<Connection> tcp; List<UdpSession> flows; List<Socket> controlList;
        synchronized (sync) {
            if (closed) return;
            closed = true;
            tcp = new ArrayList<>(connections); connections.clear();
            flows = new ArrayList<>(sessions.values()); sessions.clear();
            controlList = new ArrayList<>(controls); controls.clear();
        }
        SocksProbe.closeQuietly(server);
        udp.close();
        for (Connection connection : tcp) connection.close(true);
        for (UdpSession session : flows) session.dispose();
        for (Socket control : controlList) SocksProbe.closeQuietly(control);
    }

    private static void sleep(long ms) { try { Thread.sleep(ms); } catch (InterruptedException ignored) { } }

    // ------------------------------------------------------------------ TCP

    private final class Connection {
        final Socket client, upstream;
        final Target target;
        final String destination;
        final long openedAt = System.currentTimeMillis();
        final AtomicLong fromBackend = new AtomicLong(), fromClient = new AtomicLong();
        final AtomicLong lastActivity = new AtomicLong(openedAt);
        final AtomicInteger backendEndedFirst = new AtomicInteger(-1);
        private final AtomicInteger done = new AtomicInteger();
        volatile boolean closedLocally;

        Connection(Socket client, Socket upstream, Target target, String destination) {
            this.client = client; this.upstream = upstream; this.target = target; this.destination = destination;
        }

        void run() {
            Thread up = new Thread(() -> pump(client, upstream, false), "trelay-pump-up");
            up.setDaemon(true);
            up.start();
            pump(upstream, client, true);
            try { up.join(); } catch (InterruptedException ignored) { }
        }

        private void pump(Socket from, Socket to, boolean backendSide) {
            byte[] buffer = new byte[16 * 1024];
            try {
                InputStream input = from.getInputStream();
                OutputStream output = to.getOutputStream();
                int read;
                while ((read = input.read(buffer)) >= 0) {
                    if (read == 0) continue;
                    (backendSide ? fromBackend : fromClient).addAndGet(read);
                    long now = System.currentTimeMillis();
                    lastActivity.set(now);
                    lastTraffic.lazySet(now);
                    output.write(buffer, 0, read);
                    output.flush();
                }
                backendEndedFirst.compareAndSet(-1, backendSide ? 1 : 0);
                try { to.shutdownOutput(); } catch (IOException error) { close(true); }
            } catch (IOException error) {
                backendEndedFirst.compareAndSet(-1, backendSide ? 1 : 0);
                close(true);
            }
        }

        boolean closedByBackendWithoutData() {
            return backendEndedFirst.get() == 1 && fromBackend.get() == 0 && System.currentTimeMillis() - openedAt < 4000;
        }

        Closed describe(String route) {
            Closed info = new Closed();
            info.route = route;
            info.via = target.name;
            info.destination = destination;
            info.closedBy = closedLocally ? "trelay" : backendEndedFirst.get() == 1 ? "server" : backendEndedFirst.get() == 0 ? "app" : "?";
            info.openedAt = openedAt;
            info.lifetimeMs = System.currentTimeMillis() - openedAt;
            info.idleMs = System.currentTimeMillis() - lastActivity.get();
            info.up = fromClient.get();
            info.down = fromBackend.get();
            return info;
        }

        // abort=true resets the application's socket so it notices the loss at once.
        void close(boolean abort) {
            if (!done.compareAndSet(0, 1)) return;
            if (backendEndedFirst.get() == -1) closedLocally = true;
            if (abort) try { client.setSoLinger(true, 0); } catch (SocketException ignored) { }
            SocksProbe.closeQuietly(client);
            SocksProbe.closeQuietly(upstream);
        }
    }

    // ------------------------------------------------------------------ UDP

    // SOCKS5 UDP envelope: RSV, FRAG, ATYP, address, then the destination port.
    private static SocksProbe.HostPort destination(byte[] data, int length) {
        try {
            if (length < 7) return null;
            int type = data[3] & 0xff;
            int offset;
            String host;
            if (type == 1) {
                offset = 8;
                if (length < offset + 2) return null;
                host = InetAddress.getByAddress(java.util.Arrays.copyOfRange(data, 4, 8)).getHostAddress();
            } else if (type == 4) {
                offset = 20;
                if (length < offset + 2) return null;
                host = InetAddress.getByAddress(java.util.Arrays.copyOfRange(data, 4, 20)).getHostAddress();
            } else if (type == 3) {
                int count = data[4] & 0xff;
                offset = 5 + count;
                if (count == 0 || length < offset + 2) return null;
                host = new String(data, 5, count, StandardCharsets.US_ASCII);
            } else return null;
            int port = ((data[offset] & 0xff) << 8) | (data[offset + 1] & 0xff);
            return new SocksProbe.HostPort(host, port);
        } catch (Exception error) { return null; }
    }

    private static int udpPort(byte[] data, int length) {
        if (length < 10 || data[0] != 0 || data[1] != 0 || data[2] != 0) return -1;
        int offset;
        int type = data[3] & 0xff;
        if (type == 1) offset = 8;
        else if (type == 4) offset = 20;
        else if (type == 3 && length > 5) offset = 5 + (data[4] & 0xff);
        else return -1;
        return length >= offset + 2 ? ((data[offset] & 0xff) << 8) | (data[offset + 1] & 0xff) : -1;
    }

    private final class UdpSession {
        final InetSocketAddress client;
        final AtomicLong lastActivity = new AtomicLong(System.currentTimeMillis());
        private final Object gate = new Object();
        private final ArrayDeque<byte[]> pending = new ArrayDeque<>();
        private Socket control;
        private DatagramSocket upstream;
        private InetSocketAddress relayEndpoint;
        private boolean binding, disposed;
        private boolean responseLogged;
        private volatile boolean sipSentLogged, sipReplyLogged;
        volatile Target target;

        UdpSession(InetSocketAddress client) { this.client = client; }

        void send(byte[] datagram) {
            SocksProbe.HostPort sipDestination = destination(datagram, datagram.length);
            int destinationPort = sipDestination == null ? -1 : sipDestination.port;
            if (SocksProbe.isSipPort(destinationPort)) {
                listener.sipTarget(sipDestination.host, destinationPort, true);
                if (!sipSentLogged) {
                    sipSentLogged = true;
                    listener.log("SIP_UDP_SENT route=" + name + " dst=" + sipDestination.host + ":" + destinationPort);
                }
            }
            long now = System.currentTimeMillis();
            lastActivity.set(now);
            lastTraffic.lazySet(now);
            DatagramSocket socket; InetSocketAddress destination;
            synchronized (gate) {
                if (disposed) return;
                socket = upstream; destination = relayEndpoint;
                if (socket == null) {
                    if (pending.size() < 256) pending.add(datagram);
                    if (!binding) { binding = true; POOL.execute(this::bind); }
                    return;
                }
            }
            try { socket.send(new DatagramPacket(datagram, datagram.length, destination)); }
            catch (IOException error) { unbind(false); }
        }

        private void bind() {
            try {
                Target[] targets = waitForTargets();
                for (int i = 0; i < Math.min(4, targets.length); i++) {
                    Target candidate = targets[i];
                    Opened opened = openBackend(candidate, new byte[] {5, 3, 0, 1, 0, 0, 0, 0, 0, 0});
                    InetSocketAddress bound = opened.socket == null ? null : parseBound(opened.reply, candidate);
                    if (opened.socket == null || bound == null) {
                        listener.log("UDP_ASSOCIATE_FAILED route=" + name + " via=" + candidate.name);
                        SocksProbe.closeQuietly(opened.socket);
                        continue;
                    }
                    DatagramSocket socket = new DatagramSocket(new InetSocketAddress(LOOPBACK, 0));
                    List<byte[]> flush;
                    synchronized (gate) {
                        if (disposed) { SocksProbe.closeQuietly(opened.socket); socket.close(); return; }
                        control = opened.socket; upstream = socket; relayEndpoint = bound; target = candidate; binding = false;
                        flush = new ArrayList<>(pending);
                        pending.clear();
                    }
                    Socket controlSocket = opened.socket;
                    listener.log("UDP_ASSOCIATE_READY route=" + name + " via=" + candidate.name);
                    POOL.execute(() -> receiveLoop(socket, bound));
                    POOL.execute(() -> watchControl(controlSocket, socket));
                    for (byte[] datagram : flush)
                        try { socket.send(new DatagramPacket(datagram, datagram.length, bound)); } catch (IOException ignored) { }
                    return;
                }
            } catch (Exception error) {
                listener.log("UDP_ASSOCIATE_ERROR route=" + name + " reason=" + error.getClass().getSimpleName());
            }
            listener.log("UDP_ASSOCIATE_UNAVAILABLE route=" + name);
            synchronized (gate) { binding = false; pending.clear(); }
        }

        private InetSocketAddress parseBound(byte[] reply, Target candidate) {
            try {
                InetAddress address;
                int offset;
                if (reply[3] == 1) { address = InetAddress.getByAddress(java.util.Arrays.copyOfRange(reply, 4, 8)); offset = 8; }
                else if (reply[3] == 4) { address = InetAddress.getByAddress(java.util.Arrays.copyOfRange(reply, 4, 20)); offset = 20; }
                else return null;
                int boundPort = ((reply[offset] & 0xff) << 8) | (reply[offset + 1] & 0xff);
                if (address.isAnyLocalAddress() || address.getAddress().length != 4) address = LOOPBACK;
                if (boundPort == 0) boundPort = candidate.port();
                return new InetSocketAddress(address, boundPort);
            } catch (Exception error) { return null; }
        }

        private void receiveLoop(DatagramSocket socket, InetSocketAddress expected) {
            byte[] buffer = new byte[65535];
            while (true) {
                DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                try { socket.receive(packet); }
                catch (IOException error) {
                    synchronized (gate) { if (disposed || upstream != socket) return; }
                    if (socket.isClosed()) return;
                    continue;
                }
                if (!expected.equals(packet.getSocketAddress())) continue;
                int sourcePort = udpPort(buffer, packet.getLength());
                if (SocksProbe.isSipPort(sourcePort) && !sipReplyLogged) {
                    sipReplyLogged = true;
                    listener.log("SIP_UDP_REPLY route=" + name + " srcPort=" + sourcePort);
                }
                if (!responseLogged) {
                    responseLogged = true;
                    listener.log("UDP_RESPONSE route=" + name + " via=" + (target == null ? "?" : target.name));
                }
                long now = System.currentTimeMillis();
                lastActivity.set(now);
                lastTraffic.lazySet(now);
                sendToClient(client, buffer, packet.getLength());
            }
        }

        // A closed control connection means the backend dropped the association.
        private void watchControl(Socket tcp, DatagramSocket socket) {
            try {
                InputStream input = tcp.getInputStream();
                byte[] discard = new byte[64];
                while (input.read(discard) >= 0) { }
            } catch (IOException ignored) { }
            synchronized (gate) { if (disposed || upstream != socket) return; }
            unbind(false);
        }

        void unbind(boolean evacuated) {
            Socket oldControl; DatagramSocket oldSocket;
            synchronized (gate) {
                oldControl = control; oldSocket = upstream;
                control = null; upstream = null; relayEndpoint = null;
                if (evacuated) target = null;
            }
            SocksProbe.closeQuietly(oldControl);
            if (oldSocket != null) oldSocket.close();
        }

        void dispose() {
            synchronized (gate) { disposed = true; pending.clear(); }
            unbind(true);
        }
    }
}

