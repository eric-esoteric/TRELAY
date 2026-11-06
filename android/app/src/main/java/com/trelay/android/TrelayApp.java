package com.trelay.android;

import android.app.Application;
import android.content.Context;
import android.net.DnsResolver;
import android.os.Build;
import android.os.CancellationSignal;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class TrelayApp extends Application {
    static final String VERSION = "Beta 0.3.2";
    private static volatile boolean initialised;

    @Override public void onCreate() {
        super.onCreate();
        init(this);
    }

    static void init(Context context) {
        if (initialised) return;
        synchronized (TrelayApp.class) {
            if (initialised) return;
            Context app = context.getApplicationContext();
            // Budget phones (4 GB): keep the in-process Go runtime (Xray) compact. Read by Go when
            // libgojni loads, so this must run before the first Xray call.
            try {
                android.system.Os.setenv("GOMEMLIMIT", "160MiB", false);
                android.system.Os.setenv("GOGC", "50", false);
            } catch (Exception ignored) { }
            EngineLog.init(app);
            ShareLinkResolver.platform = new AndroidResolverPlatform(app);
            initialised = true;
        }
    }

    private static final class AndroidResolverPlatform implements ShareLinkResolver.Platform {
        private final Context context;
        AndroidResolverPlatform(Context context) { this.context = context; }

        @Override public String systemTxt(String host) throws Exception {
            CountDownLatch done = new CountDownLatch(1);
            AtomicReference<byte[]> answer = new AtomicReference<>();
            AtomicReference<Exception> failure = new AtomicReference<>();
            CancellationSignal cancel = new CancellationSignal();
            DnsResolver.getInstance().rawQuery(null, host, DnsResolver.CLASS_IN, 16, DnsResolver.FLAG_EMPTY,
                    Executors.newSingleThreadExecutor(), cancel, new DnsResolver.Callback<byte[]>() {
                        @Override public void onAnswer(byte[] data, int rcode) { answer.set(data); done.countDown(); }
                        @Override public void onError(DnsResolver.DnsException error) { failure.set(error); done.countDown(); }
                    });
            if (!done.await(8, TimeUnit.SECONDS)) { cancel.cancel(); throw new java.io.IOException("System DNS timeout"); }
            if (failure.get() != null) throw failure.get();
            return parseTxt(answer.get());
        }

        @Override public AutoCloseable startFragmentProxy(int[] portOut) throws Exception {
            int port;
            try (ServerSocket socket = new ServerSocket()) {
                socket.bind(new InetSocketAddress("127.0.0.1", 0));
                port = socket.getLocalPort();
            }
            XrayCore.Instance instance = XrayCore.start(context, XrayConfigs.fragmentProxy(port).toString(), 0, null);
            long deadline = System.currentTimeMillis() + 4000;
            boolean open = false;
            while (System.currentTimeMillis() < deadline && !open) {
                try (Socket probe = new Socket()) { probe.connect(new InetSocketAddress("127.0.0.1", port), 150); open = true; }
                catch (Exception retry) { Thread.sleep(50); }
            }
            if (!open) { instance.stop(); throw new java.io.IOException("Xray did not open the temporary proxy."); }
            portOut[0] = port;
            return instance::stop;
        }

        @Override public String userAgent() { return "TRELAY/Android-" + VERSION.replace(' ', '-'); }
        @Override public String deviceOs() { return "Android"; }
        @Override public String deviceModel() { return Build.MODEL == null ? "Phone" : Build.MODEL; }
        @Override public String osVersion() { return Build.VERSION.RELEASE; }
    }

    static String parseTxt(byte[] message) {
        if (message == null || message.length < 12) return null;
        int questions = ((message[4] & 0xff) << 8) | (message[5] & 0xff);
        int answers = ((message[6] & 0xff) << 8) | (message[7] & 0xff);
        int offset = 12;
        for (int i = 0; i < questions; i++) offset = skipName(message, offset) + 4;
        for (int i = 0; i < answers; i++) {
            offset = skipName(message, offset);
            int type = ((message[offset] & 0xff) << 8) | (message[offset + 1] & 0xff);
            int length = ((message[offset + 8] & 0xff) << 8) | (message[offset + 9] & 0xff);
            int data = offset + 10;
            if (type == 16) {
                StringBuilder text = new StringBuilder();
                int position = data;
                while (position < data + length) {
                    int chunk = message[position] & 0xff;
                    text.append(new String(message, position + 1, chunk, StandardCharsets.ISO_8859_1));
                    position += chunk + 1;
                }
                if (text.length() > 0) return text.toString();
            }
            offset = data + length;
        }
        return null;
    }

    private static int skipName(byte[] message, int offset) {
        while (true) {
            int length = message[offset] & 0xff;
            if (length == 0) return offset + 1;
            if ((length & 0xc0) == 0xc0) return offset + 2;
            offset += length + 1;
        }
    }
}
