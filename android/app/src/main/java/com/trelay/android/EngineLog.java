package com.trelay.android;

import android.content.Context;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

// engine.log in app storage, rotated at 1 MB (as on Windows). Keys and passwords are never written.
final class EngineLog {
    private static final Object LOCK = new Object();
    private static File file;

    private EngineLog() { }

    static void init(Context context) {
        synchronized (LOCK) { if (file == null) file = new File(context.getFilesDir(), "engine.log"); }
    }

    static File file(Context context) { init(context); return file; }

    static void write(String text) {
        synchronized (LOCK) {
            if (file == null) return;
            try {
                if (file.length() >= 1024 * 1024) {
                    File old = new File(file.getPath() + ".1");
                    old.delete();
                    file.renameTo(old);
                }
                String line = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS", Locale.ROOT).format(new Date()) + " " +
                        (text == null ? "" : text.replace('\r', ' ').replace('\n', ' ')) + "\n";
                try (FileOutputStream output = new FileOutputStream(file, true)) { output.write(line.getBytes(StandardCharsets.UTF_8)); }
            } catch (Exception ignored) { }
        }
    }

    static String tail(Context context, int maxBytes) {
        init(context);
        synchronized (LOCK) {
            try (java.io.RandomAccessFile input = new java.io.RandomAccessFile(file, "r")) {
                long length = input.length();
                long start = Math.max(0, length - maxBytes);
                input.seek(start);
                byte[] data = new byte[(int) (length - start)];
                input.readFully(data);
                String text = new String(data, StandardCharsets.UTF_8);
                if (start > 0 && text.indexOf('\n') >= 0) text = text.substring(text.indexOf('\n') + 1);
                return text;
            } catch (Exception error) { return ""; }
        }
    }

    static void clear(Context context) {
        init(context);
        synchronized (LOCK) { file.delete(); new File(file.getPath() + ".1").delete(); }
    }
}
