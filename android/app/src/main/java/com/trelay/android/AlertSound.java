package com.trelay.android;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.os.VibratorManager;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;

// Port of Windows AlertSound: a loud, distinctive alarm generated in memory and a soft chime.
// By default the alarm goes to the phone's own speaker even when a headset is connected.
final class AlertSound {
    private static final int SAMPLE_RATE = 22050;
    private static final Object SYNC = new Object();
    private static short[] alarm, chime;
    private static long lastAlarm;
    private static AudioTrack current;

    private AlertSound() { }

    static final class Output {
        final String id; final String label;
        Output(String id, String label) { this.id = id; this.label = label; }
        @Override public String toString() { return label; }
    }

    static List<Output> outputs(Context context) {
        List<Output> result = new ArrayList<>();
        result.add(new Output("speaker", "Динамик телефона (рекомендуется)"));
        result.add(new Output("system", "Системный выход (как у будильника)"));
        AudioManager audio = context.getSystemService(AudioManager.class);
        for (AudioDeviceInfo device : audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS)) {
            if (!device.isSink() || device.getType() == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER || device.getType() == AudioDeviceInfo.TYPE_BUILTIN_EARPIECE ||
                    device.getType() == AudioDeviceInfo.TYPE_TELEPHONY) continue;
            String name = typeName(device.getType()) + (device.getProductName() == null ? "" : ": " + device.getProductName());
            String id = "device:" + device.getType() + ":" + device.getProductName();
            boolean duplicate = false;
            for (Output output : result) if (output.id.equals(id)) duplicate = true;
            if (!duplicate) result.add(new Output(id, name));
        }
        return result;
    }

    private static String typeName(int type) {
        switch (type) {
            case AudioDeviceInfo.TYPE_BLUETOOTH_A2DP: case AudioDeviceInfo.TYPE_BLUETOOTH_SCO: case AudioDeviceInfo.TYPE_BLE_HEADSET: case AudioDeviceInfo.TYPE_BLE_SPEAKER: return "Bluetooth";
            case AudioDeviceInfo.TYPE_WIRED_HEADPHONES: case AudioDeviceInfo.TYPE_WIRED_HEADSET: return "Проводные наушники";
            case AudioDeviceInfo.TYPE_USB_HEADSET: case AudioDeviceInfo.TYPE_USB_DEVICE: return "USB";
            default: return "Выход";
        }
    }

    private static AudioDeviceInfo resolve(Context context, String output) {
        AudioManager audio = context.getSystemService(AudioManager.class);
        for (AudioDeviceInfo device : audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS)) {
            if ("speaker".equals(output) || output == null || output.isEmpty()) {
                if (device.getType() == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER) return device;
            } else if (output.equals("device:" + device.getType() + ":" + device.getProductName())) return device;
        }
        return null;
    }

    static void play(Context context, boolean critical, Model.Settings settings, boolean force) {
        Context app = context.getApplicationContext();
        synchronized (SYNC) {
            long now = System.currentTimeMillis();
            // Never cut an alarm short, and do not stack alarms that arrive together.
            if (!force && now - lastAlarm < 2000) return;
            if (critical) lastAlarm = now;
            if (alarm == null) { alarm = buildAlarm(); chime = buildChime(); }
        }
        short[] data = critical ? alarm : chime;
        if (critical && settings.vibrate) vibrate(app);
        new Thread(() -> playBlocking(app, data, critical, settings), "trelay-alert").start();
    }

    private static void playBlocking(Context context, short[] data, boolean critical, Model.Settings settings) {
        AudioManager audio = context.getSystemService(AudioManager.class);
        int previousVolume = -1;
        AudioTrack track = null;
        try {
            if (critical && settings.maxAlarmVolume) {
                try {
                    previousVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM);
                    audio.setStreamVolume(AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0);
                } catch (SecurityException ignored) { previousVolume = -1; }
            }
            AudioAttributes attributes = new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build();
            track = new AudioTrack.Builder().setAudioAttributes(attributes)
                    .setAudioFormat(new AudioFormat.Builder().setSampleRate(SAMPLE_RATE).setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setTransferMode(AudioTrack.MODE_STATIC).setBufferSizeInBytes(data.length * 2).build();
            track.write(data, 0, data.length);
            if (!"system".equals(settings.alertOutput)) {
                AudioDeviceInfo device = resolve(context, settings.alertOutput);
                // Never silently move the alarm into headphones when the chosen speaker is unavailable.
                if (device == null && !"speaker".equals(settings.alertOutput)) device = resolve(context, "speaker");
                if (device != null) track.setPreferredDevice(device);
            }
            synchronized (SYNC) {
                if (current != null) { try { current.stop(); current.release(); } catch (Exception ignored) { } }
                current = track;
            }
            track.play();
            Thread.sleep(data.length * 1000L / SAMPLE_RATE + 150);
        } catch (Exception ignored) {
        } finally {
            synchronized (SYNC) {
                if (current == track && track != null) { try { track.stop(); } catch (Exception ignored) { } track.release(); current = null; }
            }
            if (previousVolume >= 0) {
                int restore = previousVolume;
                new Handler(Looper.getMainLooper()).postDelayed(() -> {
                    try { audio.setStreamVolume(AudioManager.STREAM_ALARM, restore, 0); } catch (Exception ignored) { }
                }, 300);
            }
        }
    }

    private static void vibrate(Context context) {
        try {
            Vibrator vibrator = Build.VERSION.SDK_INT >= 31
                    ? context.getSystemService(VibratorManager.class).getDefaultVibrator()
                    : context.getSystemService(Vibrator.class);
            if (vibrator != null) vibrator.vibrate(VibrationEffect.createWaveform(new long[] {0, 400, 150, 400, 150, 600}, -1));
        } catch (Exception ignored) { }
    }

    private static short[] buildAlarm() {
        ByteArrayOutputStream samples = new ByteArrayOutputStream();
        for (int repeat = 0; repeat < 3; repeat++) {
            tone(samples, 988, 150, 0.85, true);
            silence(samples, 40);
            tone(samples, 1319, 150, 0.85, true);
            silence(samples, 170);
        }
        return toShorts(samples.toByteArray());
    }

    private static short[] buildChime() {
        ByteArrayOutputStream samples = new ByteArrayOutputStream();
        tone(samples, 784, 120, 0.4, false);
        tone(samples, 1175, 260, 0.4, false);
        return toShorts(samples.toByteArray());
    }

    private static void tone(ByteArrayOutputStream output, double frequency, int milliseconds, double volume, boolean rich) {
        int count = SAMPLE_RATE * milliseconds / 1000;
        int edge = SAMPLE_RATE * 8 / 1000;
        for (int i = 0; i < count; i++) {
            double t = (double) i / SAMPLE_RATE;
            double envelope = Math.min(1.0, Math.min((double) i / edge, (double) (count - i) / edge));
            if (!rich) envelope *= Math.exp(-3.0 * i / count);
            double value = Math.sin(2 * Math.PI * frequency * t);
            if (rich) value = (value + 0.3 * Math.sin(2 * Math.PI * frequency * 3 * t)) / 1.3;
            short sample = (short) Math.max(Short.MIN_VALUE, Math.min(Short.MAX_VALUE, value * envelope * volume * Short.MAX_VALUE));
            output.write(sample & 0xff);
            output.write((sample >> 8) & 0xff);
        }
    }

    private static void silence(ByteArrayOutputStream output, int milliseconds) {
        int bytes = SAMPLE_RATE * milliseconds / 1000 * 2;
        for (int i = 0; i < bytes; i++) output.write(0);
    }

    private static short[] toShorts(byte[] pcm) {
        short[] result = new short[pcm.length / 2];
        for (int i = 0; i < result.length; i++) result[i] = (short) ((pcm[i * 2] & 0xff) | (pcm[i * 2 + 1] << 8));
        return result;
    }
}
