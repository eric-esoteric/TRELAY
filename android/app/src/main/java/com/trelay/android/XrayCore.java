package com.trelay.android;

import android.content.Context;
import libv2ray.CoreCallbackHandler;
import libv2ray.CoreController;
import libv2ray.Libv2ray;
import libv2ray.ProcessFinder;

// In-process Xray instances (AndroidLibXrayLite). StartLoop sets a process-wide TUN fd variable,
// so every start and stop is serialised.
final class XrayCore {
    private static final Object LOCK = new Object();
    private static boolean environment;

    private XrayCore() { }

    static final class Instance implements TunnelEngine.Runner {
        private final CoreController controller;
        private volatile boolean stopped;
        Instance(CoreController controller) { this.controller = controller; }
        @Override public boolean alive() { try { return !stopped && controller.getIsRunning(); } catch (Throwable error) { return false; } }
        @Override public void stop() {
            if (stopped) return;
            stopped = true;
            synchronized (LOCK) { try { controller.stopLoop(); } catch (Throwable ignored) { } }
        }
    }

    static Instance start(Context context, String config, int tunFd, ProcessFinder finder) throws Exception {
        synchronized (LOCK) {
            if (!environment) {
                // The asset directory is where Xray would look for geoip.dat and geosite.dat. TRELAY
                // ships neither: a route sends its applications through the server, so the country
                // and site lists a provider configuration carries are stripped before the start.
                Libv2ray.initCoreEnv(context.getFilesDir().getAbsolutePath(), "");
                environment = true;
            }
            CoreController controller = Libv2ray.newCoreController(new CoreCallbackHandler() {
                @Override public long startup() { return 0; }
                @Override public long shutdown() { return 0; }
                @Override public long onEmitStatus(long code, String message) { return 0; }
            });
            if (finder != null) controller.registerProcessFinder(finder);
            try { controller.startLoop(config, tunFd); }
            catch (Exception error) {
                String message = error.getMessage() == null ? "" : error.getMessage();
                // Go errors quote the configuration, so the reason is scrubbed of anything secret
                // before it reaches the log — without it "bad configuration" is undebuggable.
                String reason = reason(message);
                EngineLog.write("XRAY_START_FAILED " + reason);
                String category = message.contains(".dat: no such file") || message.contains("geoip") || message.contains("geosite")
                        ? "в ключе есть правила по странам и спискам сайтов"
                        : message.contains("config") ? "некорректная конфигурация"
                        : message.contains("startup") ? "не удалось открыть порт или соединение" : "ошибка запуска";
                throw new IllegalStateException("Xray не запустился: " + category + " (" + reason + ")");
            }
            if (!controller.getIsRunning()) throw new IllegalStateException("Xray не запустился.");
            return new Instance(controller);
        }
    }

    /** The last, most specific part of a Go error, with keys, hosts and ids removed. */
    static String reason(String message) {
        String text = message == null ? "" : message.trim();
        int cut = text.lastIndexOf(" > ");
        if (cut >= 0 && cut + 3 < text.length()) text = text.substring(cut + 3);
        text = text.replaceAll("(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<id>");
        text = text.replaceAll("[A-Za-z0-9+/=_-]{16,}", "<secret>");
        text = text.replaceAll("\\b\\d{1,3}(\\.\\d{1,3}){3}\\b", "<ip>");
        text = text.replaceAll("(?i)\\b[a-z0-9-]+(\\.[a-z0-9-]+)+\\b", "<host>");
        text = text.replaceAll("\\s+", " ").trim();
        if (text.length() > 160) text = text.substring(0, 160) + "…";
        return text.isEmpty() ? "без подробностей" : text;
    }

    static String version() {
        try { return Libv2ray.checkVersionX(); } catch (Throwable error) { return "Xray"; }
    }
}
