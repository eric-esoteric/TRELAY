package com.trelay.android;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.IDN;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.cert.X509Certificate;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLHandshakeException;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

// Port of Windows SocksHealthProbe. Liveness probes must exchange real data with the far side:
// Xray and Mihomo answer a SOCKS CONNECT with "success" before they dial the remote server.
final class SocksProbe {
    static final String[] INTERNET_TARGETS = {"http://cp.cloudflare.com/generate_204", "http://www.gstatic.com/generate_204", "www.cloudflare.com:443"};
    private static final ScheduledExecutorService TIMER = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread thread = new Thread(r, "trelay-probe-timer");
        thread.setDaemon(true);
        return thread;
    });

    private SocksProbe() { }

    static final class Result {
        final boolean ok;
        final String failure;
        Result(boolean ok, String failure) { this.ok = ok; this.failure = failure; }
    }

    static final class HostPort {
        final String host;
        final int port;
        HostPort(String host, int port) { this.host = host; this.port = port; }
    }

    /** Validates "host:port" (IPv6 in brackets). Returns null when invalid. */
    static HostPort parseEndpoint(String value) {
        value = value == null ? "" : value.trim();
        String lower = value.toLowerCase(java.util.Locale.ROOT);
        if (lower.startsWith("udp://") || lower.startsWith("tcp://")) value = value.substring(6);
        int split = value.lastIndexOf(':');
        if (split <= 0 || split == value.length() - 1) return null;
        int port;
        try { port = Integer.parseInt(value.substring(split + 1)); } catch (NumberFormatException error) { return null; }
        if (port < 1 || port > 65535) return null;
        String host = value.substring(0, split);
        if (host.startsWith("[")) {
            if (!host.endsWith("]")) return null;
            host = host.substring(1, host.length() - 1);
            return host.contains(":") ? new HostPort(host, port) : null;
        }
        if (host.contains(":") || host.matches(".*[/\\\\@?#\\s].*")) return null;
        try { host = IDN.toASCII(host); } catch (Exception error) { return null; }
        if (host.isEmpty() || host.length() > 253 || !host.matches("[A-Za-z0-9._-]+")) return null;
        return new HostPort(host, port);
    }

    static String validateEndpoint(String value) {
        if (value == null || value.trim().isEmpty()) return null;
        return parseEndpoint(value) == null ? "Адрес проверки должен быть в формате host:port" : null;
    }

    /** SIP signalling ports: the probe asks OPTIONS and needs a real answer from the PBX. */
    static boolean isSipPort(int port) { return port == 5060 || port == 5061 || port == 5080 || port == 5090 || port == 6050; }

    static boolean isHoldOnly(String endpoint) {
        if (endpoint == null || endpoint.startsWith("http://") ||
                endpoint.toLowerCase(java.util.Locale.ROOT).startsWith("udp://")) return false;
        HostPort target = parseEndpoint(endpoint);
        return target != null && target.port != 80 && target.port != 443 && !isSipPort(target.port);
    }

    static Result probe(int socksPort, String endpoint, int timeoutMs) {
        endpoint = endpoint == null ? "" : endpoint.trim();
        String lower = endpoint.toLowerCase(java.util.Locale.ROOT);
        if (lower.startsWith("udp://")) {
            HostPort target = parseEndpoint(endpoint);
            return target == null ? new Result(false, "некорректный адрес") : sipUdp(socksPort, target.host, target.port, timeoutMs);
        }
        if (lower.startsWith("http://")) {
            try {
                URI uri = new URI(endpoint);
                if (uri.getHost() == null) return new Result(false, "некорректный адрес");
                String path = uri.getRawPath() == null || uri.getRawPath().isEmpty() ? "/" : uri.getRawPath();
                if (uri.getRawQuery() != null) path += "?" + uri.getRawQuery();
                return core(socksPort, uri.getHost(), uri.getPort() < 0 ? 80 : uri.getPort(), "http", path, timeoutMs);
            } catch (Exception error) { return new Result(false, "некорректный адрес"); }
        }
        HostPort target = parseEndpoint(endpoint);
        if (target == null) return new Result(false, "некорректный адрес");
        String mode = target.port == 443 ? "tls" : target.port == 80 ? "http" : isSipPort(target.port) ? "sip" : "hold";
        return core(socksPort, target.host, target.port, mode, "/", timeoutMs);
    }

    private static Result core(int socksPort, String host, int port, String mode, String path, int timeoutMs) {
        AtomicBoolean expired = new AtomicBoolean();
        long started = System.nanoTime();
        Socket socket = new Socket();
        ScheduledFuture<?> deadline = TIMER.schedule(() -> { expired.set(true); closeQuietly(socket); }, timeoutMs, TimeUnit.MILLISECONDS);
        try {
            socket.setTcpNoDelay(true);
            socket.setSoTimeout(timeoutMs);
            socket.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), socksPort), timeoutMs);
            InputStream input = socket.getInputStream();
            OutputStream output = socket.getOutputStream();
            output.write(new byte[] {5, 1, 0});
            output.flush();
            byte[] hello = readExact(input, 2);
            if (hello[0] != 5 || hello[1] != 0) throw new IOException("SOCKS отклонил подключение");
            output.write(connectRequest(host, port));
            output.flush();
            byte[] head = readExact(input, 4);
            if (head[0] != 5 || head[1] != 0 || head[2] != 0) throw new IOException("сервер вернул SOCKS-ошибку (код " + (head[1] & 0xff) + ")");
            skipAddress(input, head[3]);
            if (mode.equals("tls")) {
                boolean literal = isIpLiteral(host);
                SSLSocketFactory factory = literal ? trustAll() : (SSLSocketFactory) SSLSocketFactory.getDefault();
                SSLSocket tls = (SSLSocket) factory.createSocket(socket, host, port, true);
                tls.setSoTimeout(timeoutMs);
                tls.startHandshake();
                closeQuietly(tls);
            } else if (mode.equals("sip")) {
                // A dead tunnel answers the local SOCKS CONNECT anyway; only a real SIP reply proves
                // that the PBX is reachable through this server.
                String branch = "z9hG4bK" + Long.toHexString(System.nanoTime());
                String request = "OPTIONS sip:" + host + " SIP/2.0\r\nVia: SIP/2.0/TCP " + host + ";branch=" + branch +
                        "\r\nMax-Forwards: 70\r\nFrom: <sip:trelay@" + host + ">;tag=" + branch.substring(7) +
                        "\r\nTo: <sip:" + host + ">\r\nCall-ID: " + branch + "@trelay\r\nCSeq: 1 OPTIONS\r\n" +
                        "User-Agent: TRELAY-health\r\nAccept: application/sdp\r\nContent-Length: 0\r\n\r\n";
                output.write(request.getBytes(StandardCharsets.US_ASCII));
                output.flush();
                byte[] answer = readExact(input, 7);
                if (!"SIP/2.0".equals(new String(answer, StandardCharsets.US_ASCII))) throw new IOException("АТС ответила не по SIP");
            } else if (mode.equals("http")) {
                String hostHeader = port == 80 ? host : host + ":" + port;
                output.write(("HEAD " + path + " HTTP/1.1\r\nHost: " + hostHeader + "\r\nUser-Agent: TRELAY-health\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                output.flush();
                byte[] status = readExact(input, 5);
                if (!"HTTP/".equals(new String(status, StandardCharsets.US_ASCII))) throw new IOException("сервис вернул не HTTP-ответ");
            } else {
                long elapsed = (System.nanoTime() - started) / 1_000_000;
                // Engines retry a failed dial for about three seconds while keeping the local
                // connection open, so a shorter window would call a dead server healthy.
                int hold = (int) Math.min(4000, timeoutMs - elapsed - 250);
                if (hold < 4000) throw new IOException("слишком короткое окно проверки");
                {
                    socket.setSoTimeout(hold);
                    try {
                        if (input.read() < 0) throw new EOFException("удалённый узел закрыл соединение");
                    } catch (SocketTimeoutException stillOpen) {
                        if (expired.get()) throw stillOpen;
                    }
                }
            }
            if (expired.get()) return new Result(false, "истёк общий тайм-аут проверки");
            return new Result(true, "");
        } catch (Exception error) {
            if (expired.get()) return new Result(false, "истёк общий тайм-аут проверки");
            return new Result(false, (mode.equals("tls") ? "TLS-сервис не ответил: " : "сервис не ответил: ") + safeFailure(error));
        } finally {
            deadline.cancel(false);
            closeQuietly(socket);
        }
    }

    /** Sends a real SIP OPTIONS packet through SOCKS5 UDP ASSOCIATE. Any SIP response is success. */
    private static Result sipUdp(int socksPort, String host, int port, int timeoutMs) {
        Socket control = new Socket();
        DatagramSocket datagram = null;
        long deadline = System.currentTimeMillis() + timeoutMs;
        try {
            control.setSoTimeout(timeoutMs);
            control.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), socksPort), timeoutMs);
            InputStream input = control.getInputStream();
            OutputStream output = control.getOutputStream();
            output.write(new byte[] {5, 1, 0});
            output.flush();
            byte[] hello = readExact(input, 2);
            if (hello[0] != 5 || hello[1] != 0) throw new IOException("SOCKS отклонил подключение");
            output.write(new byte[] {5, 3, 0, 1, 0, 0, 0, 0, 0, 0});
            output.flush();
            byte[] head = readExact(input, 4);
            if (head[0] != 5 || head[1] != 0 || head[2] != 0)
                throw new IOException("сервер не поддерживает SOCKS UDP (код " + (head[1] & 0xff) + ")");
            InetSocketAddress relay = readBoundAddress(input, head[3], socksPort);

            datagram = new DatagramSocket(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0));
            String branch = "z9hG4bK" + Long.toHexString(System.nanoTime());
            String request = "OPTIONS sip:" + host + " SIP/2.0\r\nVia: SIP/2.0/UDP 127.0.0.1:" + datagram.getLocalPort() + ";rport;branch=" + branch +
                    "\r\nMax-Forwards: 70\r\nFrom: <sip:trelay@" + host + ">;tag=" + branch.substring(7) +
                    "\r\nTo: <sip:" + host + ">\r\nCall-ID: " + branch + "@trelay\r\nCSeq: 1 OPTIONS\r\n" +
                    "User-Agent: TRELAY-health\r\nAccept: application/sdp\r\nContent-Length: 0\r\n\r\n";
            byte[] envelope = udpEnvelope(host, port, request.getBytes(StandardCharsets.US_ASCII));
            datagram.send(new DatagramPacket(envelope, envelope.length, relay));

            byte[] response = new byte[8192];
            while (System.currentTimeMillis() < deadline) {
                datagram.setSoTimeout((int) Math.max(1, deadline - System.currentTimeMillis()));
                DatagramPacket packet = new DatagramPacket(response, response.length);
                datagram.receive(packet);
                if (!relay.equals(packet.getSocketAddress())) continue;
                int payload = udpPayloadOffset(response, packet.getLength());
                if (payload < 0 || packet.getLength() - payload < 7) continue;
                if ("SIP/2.0".equals(new String(response, payload, 7, StandardCharsets.US_ASCII)))
                    return new Result(true, "");
            }
            return new Result(false, "АТС не ответила по SIP/UDP");
        } catch (Exception error) {
            return new Result(false, "АТС не ответила по SIP/UDP: " + safeFailure(error));
        } finally {
            if (datagram != null) datagram.close();
            closeQuietly(control);
        }
    }

    private static InetSocketAddress readBoundAddress(InputStream input, byte type, int fallbackPort) throws IOException {
        InetAddress address;
        if (type == 1) address = InetAddress.getByAddress(readExact(input, 4));
        else if (type == 4) address = InetAddress.getByAddress(readExact(input, 16));
        else if (type == 3) {
            int length = readExact(input, 1)[0] & 0xff;
            if (length == 0) throw new IOException("SOCKS вернул пустой UDP-адрес");
            address = InetAddress.getByName(new String(readExact(input, length), StandardCharsets.US_ASCII));
        } else throw new IOException("неизвестный тип SOCKS UDP-адреса");
        byte[] port = readExact(input, 2);
        int number = ((port[0] & 0xff) << 8) | (port[1] & 0xff);
        if (address.isAnyLocalAddress() || address.getAddress().length != 4) address = InetAddress.getByName("127.0.0.1");
        if (number == 0) number = fallbackPort;
        return new InetSocketAddress(address, number);
    }

    private static byte[] udpEnvelope(String host, int port, byte[] payload) throws IOException {
        byte[] address;
        byte type;
        if (isIpLiteral(host)) {
            address = InetAddress.getByName(host).getAddress();
            type = (byte) (address.length == 4 ? 1 : 4);
        } else {
            address = host.getBytes(StandardCharsets.US_ASCII);
            if (address.length == 0 || address.length > 255) throw new IOException("некорректное имя узла");
            type = 3;
        }
        int extra = type == 3 ? 1 : 0;
        int header = 6 + extra + address.length;
        byte[] packet = new byte[header + payload.length];
        packet[0] = 0; packet[1] = 0; packet[2] = 0; packet[3] = type;
        if (type == 3) packet[4] = (byte) address.length;
        System.arraycopy(address, 0, packet, 4 + extra, address.length);
        packet[header - 2] = (byte) (port >> 8);
        packet[header - 1] = (byte) port;
        System.arraycopy(payload, 0, packet, header, payload.length);
        return packet;
    }

    private static int udpPayloadOffset(byte[] packet, int length) {
        if (length < 10 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return -1;
        int type = packet[3] & 0xff;
        int offset = type == 1 ? 10 : type == 4 ? 22 :
                type == 3 && length > 5 ? 7 + (packet[4] & 0xff) : -1;
        return offset <= length ? offset : -1;
    }

    static byte[] connectRequest(String host, int port) throws IOException {
        byte[] address;
        byte type;
        if (isIpLiteral(host)) {
            address = InetAddress.getByName(host).getAddress();
            type = (byte) (address.length == 4 ? 1 : 4);
        } else {
            address = host.getBytes(StandardCharsets.US_ASCII);
            if (address.length == 0 || address.length > 255) throw new IOException("некорректное имя узла");
            type = 3;
        }
        int extra = type == 3 ? 1 : 0;
        byte[] request = new byte[6 + extra + address.length];
        request[0] = 5; request[1] = 1; request[2] = 0; request[3] = type;
        if (type == 3) request[4] = (byte) address.length;
        System.arraycopy(address, 0, request, 4 + extra, address.length);
        request[request.length - 2] = (byte) (port >> 8);
        request[request.length - 1] = (byte) port;
        return request;
    }

    static boolean isIpLiteral(String host) {
        return host.matches("\\d{1,3}(\\.\\d{1,3}){3}") || (host.contains(":") && host.matches("[0-9A-Fa-f:.]+"));
    }

    static void skipAddress(InputStream input, byte type) throws IOException {
        if (type == 1) readExact(input, 4);
        else if (type == 4) readExact(input, 16);
        else if (type == 3) {
            int length = readExact(input, 1)[0] & 0xff;
            if (length == 0) throw new IOException("SOCKS вернул пустой адрес");
            readExact(input, length);
        } else throw new IOException("неизвестный тип SOCKS-адреса");
        readExact(input, 2);
    }

    static byte[] readExact(InputStream input, int count) throws IOException {
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count) {
            int read = input.read(data, offset, count - offset);
            if (read < 0) throw new EOFException("SOCKS закрыл соединение");
            offset += read;
        }
        return data;
    }

    private static String safeFailure(Exception error) {
        if (error instanceof SSLHandshakeException) return "проверка сертификата или TLS не пройдена";
        if (error instanceof SocketTimeoutException) return "нет ответа";
        if (error instanceof java.net.ConnectException) return "локальный SOCKS не отвечает";
        if (error instanceof java.net.SocketException) return "ошибка сетевого соединения";
        if (error instanceof IOException && error.getMessage() != null && error.getMessage().matches(".*[а-яА-Я].*")) return error.getMessage();
        if (error instanceof IOException) return "соединение закрыто";
        return error.getClass().getSimpleName();
    }

    private static volatile SSLSocketFactory trustAllFactory;

    // An IP literal has no name to verify; the completed handshake still proves end-to-end data flow.
    private static SSLSocketFactory trustAll() throws Exception {
        if (trustAllFactory == null) {
            SSLContext context = SSLContext.getInstance("TLS");
            context.init(null, new TrustManager[] {new X509TrustManager() {
                @Override public void checkClientTrusted(X509Certificate[] chain, String authType) { }
                @Override public void checkServerTrusted(X509Certificate[] chain, String authType) { }
                @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            }}, null);
            trustAllFactory = context.getSocketFactory();
        }
        return trustAllFactory;
    }

    static void closeQuietly(java.io.Closeable closeable) {
        if (closeable != null) try { closeable.close(); } catch (Exception ignored) { }
    }
}



