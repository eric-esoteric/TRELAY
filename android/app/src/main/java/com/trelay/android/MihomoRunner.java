package com.trelay.android;

import android.content.Context;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

// Bundled Mihomo (libmihomo.so executable from the APK's native library directory) for Hysteria,
// TUIC, WireGuard, AnyTLS, SSR, Shadowsocks plugins and Clash/sing-box nodes — as on Windows.
final class MihomoRunner implements TunnelEngine.Runner {
    private final Process process;
    private final File config;

    private MihomoRunner(Process process, File config) { this.process = process; this.config = config; }

    static MihomoRunner start(Context context, String serverId, String key, int port) throws Exception {
        File binary = new File(context.getApplicationInfo().nativeLibraryDir, "libmihomo.so");
        if (!binary.exists()) throw new IllegalStateException("В этой сборке нет движка Mihomo для этого процессора.");
        String yaml = MihomoConfig.prepareForSocks(key, port);
        File runtime = new File(context.getFilesDir(), "runtime");
        runtime.mkdirs();
        File config = new File(runtime, serverId + "." + System.nanoTime() + ".yaml");
        try (FileOutputStream output = new FileOutputStream(config)) { output.write(yaml.getBytes(StandardCharsets.UTF_8)); }
        File data = new File(context.getFilesDir(), "mihomo/" + serverId.replaceAll("[^A-Za-z0-9_-]", ""));
        data.mkdirs();
        File log = new File(context.getFilesDir(), "mihomo/" + serverId.replaceAll("[^A-Za-z0-9_-]", "") + ".log");
        ProcessBuilder builder = new ProcessBuilder(binary.getAbsolutePath(), "-d", data.getAbsolutePath(), "-f", config.getAbsolutePath());
        builder.redirectErrorStream(true);
        builder.redirectOutput(log);
        builder.environment().put("HOME", data.getAbsolutePath());
        builder.environment().put("GOMEMLIMIT", "48MiB");
        builder.environment().put("GOGC", "50");
        Process process;
        try { process = builder.start(); }
        catch (Exception error) { config.delete(); throw new IllegalStateException("Не удалось запустить Mihomo."); }
        MihomoRunner runner = new MihomoRunner(process, config);
        // The configuration contains credentials and is read once at start.
        Thread cleanup = new Thread(() -> {
            try { Thread.sleep(8000); } catch (InterruptedException ignored) { }
            config.delete();
        }, "trelay-mihomo-cleanup");
        cleanup.setDaemon(true);
        cleanup.start();
        return runner;
    }

    @Override public boolean alive() { return process.isAlive(); }

    @Override public void stop() {
        process.destroy();
        try {
            if (!process.waitFor(1500, java.util.concurrent.TimeUnit.MILLISECONDS)) process.destroyForcibly();
        } catch (InterruptedException ignored) { process.destroyForcibly(); }
        config.delete();
    }
}
