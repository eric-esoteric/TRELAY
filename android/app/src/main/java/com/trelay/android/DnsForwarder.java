package com.trelay.android;

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.function.Supplier;

// DNS for applications inside the VPN. The system resolver asks on behalf of the app, so the owner
// cannot be proven; queries go over TCP through the first working route (priority order) to a public
// resolver. AAAA is answered empty so apps use IPv4 through the tunnel. Nothing is resolved locally.
final class DnsForwarder implements Closeable {
    private static final String[] UPSTREAMS = {"1.1.1.1", "8.8.8.8"};
    private final Supplier<List<Integer>> relayPorts;
    private final ServerSocket tcp;
    private final DatagramSocket udp;
    private final ExecutorService pool = Executors.newCachedThreadPool(r -> {
        Thread thread = new Thread(r, "trelay-dns");
        thread.setDaemon(true);
        return thread;
    });
    private final Map<String, CacheEntry> cache = new LinkedHashMap<String, CacheEntry>(256, 0.75f, true) {
        @Override protected boolean removeEldestEntry(Map.Entry<String, CacheEntry> eldest) { return size() > 2048; }
    };
    private volatile boolean closed;
    final int port;

    private static final class CacheEntry { byte[] response; long expires; }

    DnsForwarder(Supplier<List<Integer>> relayPorts) throws IOException {
        this.relayPorts = relayPorts;
        InetAddress loopback = InetAddress.getByAddress("127.0.0.1", new byte[] {127, 0, 0, 1});
        tcp = new ServerSocket();
        tcp.bind(new InetSocketAddress(loopback, 0), 128);
        port = tcp.getLocalPort();
        DatagramSocket datagram;
        try { datagram = new DatagramSocket(new InetSocketAddress(loopback, port)); }
        catch (IOException error) { tcp.close(); throw error; }
        udp = datagram;
        Thread udpThread = new Thread(this::udpLoop, "trelay-dns-udp");
        udpThread.setDaemon(true);
        udpThread.start();
        Thread tcpThread = new Thread(this::tcpLoop, "trelay-dns-tcp");
        tcpThread.setDaemon(true);
        tcpThread.start();
    }

    private void udpLoop() {
        byte[] buffer = new byte[4096];
        while (!closed) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try { udp.receive(packet); } catch (IOException error) { if (closed) return; continue; }
            byte[] query = java.util.Arrays.copyOf(buffer, packet.getLength());
            java.net.SocketAddress client = packet.getSocketAddress();
            pool.execute(() -> {
                byte[] response = answer(query);
                if (response == null) return;
                if (response.length > 1400) response = truncated(query);
                try { udp.send(new DatagramPacket(response, response.length, client)); } catch (IOException ignored) { }
            });
        }
    }

    private void tcpLoop() {
        while (!closed) {
            try {
                Socket client = tcp.accept();
                pool.execute(() -> {
                    try (Socket socket = client) {
                        socket.setSoTimeout(20000);
                        DataInputStream input = new DataInputStream(socket.getInputStream());
                        OutputStream output = socket.getOutputStream();
                        while (true) {
                            int length = input.readUnsignedShort();
                            byte[] query = new byte[length];
                            input.readFully(query);
                            byte[] response = answer(query);
                            if (response == null) return;
                            output.write(new byte[] {(byte) (response.length >> 8), (byte) response.length});
                            output.write(response);
                            output.flush();
                        }
                    } catch (IOException ignored) { }
                });
            } catch (IOException error) { if (closed) return; }
        }
    }

    byte[] answer(byte[] query) {
        Question question = Question.parse(query);
        if (question == null) return null;
        if (question.type == 28) return empty(query, question, 0);
        String key = question.name.toLowerCase(Locale.ROOT) + "/" + question.type + "/" + question.klass;
        long now = System.currentTimeMillis();
        synchronized (cache) {
            CacheEntry entry = cache.get(key);
            if (entry != null && entry.expires > now) {
                byte[] copy = entry.response.clone();
                copy[0] = query[0]; copy[1] = query[1];
                return copy;
            }
        }
        List<Integer> ports = relayPorts.get();
        long deadline = now + 9000;
        for (int portIndex = 0; ports != null && portIndex < ports.size(); portIndex++) {
            for (String upstream : UPSTREAMS) {
                if (System.currentTimeMillis() > deadline) break;
                try {
                    byte[] response = forward(ports.get(portIndex), upstream, query);
                    if (response.length < 12 || response[0] != query[0] || response[1] != query[1]) continue;
                    int ttl = minTtl(response);
                    int rcode = response[3] & 0x0f;
                    CacheEntry entry = new CacheEntry();
                    entry.response = response;
                    entry.expires = System.currentTimeMillis() + 1000L * (rcode == 0 ? Math.max(30, Math.min(600, ttl)) : 30);
                    if (rcode == 0 || rcode == 3) synchronized (cache) { cache.put(key, entry); }
                    return response;
                } catch (Exception ignored) { }
            }
        }
        return empty(query, question, 2);
    }

    private static byte[] forward(int relayPort, String upstream, byte[] query) throws IOException {
        try (Socket socket = new Socket()) {
            socket.setTcpNoDelay(true);
            socket.setSoTimeout(3500);
            socket.connect(new InetSocketAddress(InetAddress.getByAddress(new byte[] {127, 0, 0, 1}), relayPort), 1000);
            InputStream input = socket.getInputStream();
            OutputStream output = socket.getOutputStream();
            output.write(new byte[] {5, 1, 0});
            output.flush();
            byte[] hello = SocksProbe.readExact(input, 2);
            if (hello[0] != 5 || hello[1] != 0) throw new IOException("socks");
            output.write(SocksProbe.connectRequest(upstream, 53));
            output.flush();
            byte[] head = SocksProbe.readExact(input, 4);
            if (head[1] != 0) throw new IOException("socks connect");
            SocksProbe.skipAddress(input, head[3]);
            output.write(new byte[] {(byte) (query.length >> 8), (byte) query.length});
            output.write(query);
            output.flush();
            byte[] lengthBytes = SocksProbe.readExact(input, 2);
            int length = ((lengthBytes[0] & 0xff) << 8) | (lengthBytes[1] & 0xff);
            return SocksProbe.readExact(input, length);
        }
    }

    static final class Question {
        String name;
        int type, klass, end;

        static Question parse(byte[] message) {
            try {
                if (message.length < 12 || (message[2] & 0x80) != 0) return null;
                int count = ((message[4] & 0xff) << 8) | (message[5] & 0xff);
                if (count < 1) return null;
                StringBuilder name = new StringBuilder();
                int offset = 12;
                while (true) {
                    int length = message[offset] & 0xff;
                    if (length == 0) { offset++; break; }
                    if ((length & 0xc0) != 0) return null;
                    if (name.length() > 0) name.append('.');
                    name.append(new String(message, offset + 1, length, java.nio.charset.StandardCharsets.ISO_8859_1));
                    offset += length + 1;
                }
                Question question = new Question();
                question.name = name.toString();
                question.type = ((message[offset] & 0xff) << 8) | (message[offset + 1] & 0xff);
                question.klass = ((message[offset + 2] & 0xff) << 8) | (message[offset + 3] & 0xff);
                question.end = offset + 4;
                return question;
            } catch (RuntimeException error) { return null; }
        }
    }

    private static byte[] empty(byte[] query, Question question, int rcode) {
        byte[] response = java.util.Arrays.copyOf(query, question.end);
        response[2] = (byte) (0x80 | (query[2] & 0x79));
        response[3] = (byte) (0x80 | rcode);
        response[4] = 0; response[5] = 1;
        for (int i = 6; i < 12; i++) response[i] = 0;
        return response;
    }

    private static byte[] truncated(byte[] query) {
        Question question = Question.parse(query);
        if (question == null) return new byte[0];
        byte[] response = empty(query, question, 0);
        response[2] |= 0x02;
        return response;
    }

    static int minTtl(byte[] message) {
        try {
            int questions = ((message[4] & 0xff) << 8) | (message[5] & 0xff);
            int answers = ((message[6] & 0xff) << 8) | (message[7] & 0xff);
            int offset = 12;
            for (int i = 0; i < questions; i++) offset = skipName(message, offset) + 4;
            int min = Integer.MAX_VALUE;
            for (int i = 0; i < answers; i++) {
                offset = skipName(message, offset);
                long ttl = ((message[offset + 4] & 0xffL) << 24) | ((message[offset + 5] & 0xff) << 16) | ((message[offset + 6] & 0xff) << 8) | (message[offset + 7] & 0xff);
                int length = ((message[offset + 8] & 0xff) << 8) | (message[offset + 9] & 0xff);
                min = (int) Math.min(min, Math.min(ttl, Integer.MAX_VALUE));
                offset += 10 + length;
            }
            return min == Integer.MAX_VALUE ? 60 : min;
        } catch (RuntimeException error) { return 60; }
    }

    private static int skipName(byte[] message, int offset) {
        while (true) {
            int length = message[offset] & 0xff;
            if (length == 0) return offset + 1;
            if ((length & 0xc0) == 0xc0) return offset + 2;
            offset += length + 1;
        }
    }

    @Override public void close() {
        closed = true;
        SocksProbe.closeQuietly(tcp);
        udp.close();
        pool.shutdownNow();
    }
}
