package com.trelay.android;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.SystemClock;

// Keeps TRELAY alive all day at almost no cost: an idle-friendly alarm every 5 minutes restarts the
// VPN service if Android (or One UI "sleeping apps") killed the process, and otherwise only nudges
// one health probe. No permanent wake lock is held. Android does not always restart a killed
// VpnService by itself, so this alarm is the real recovery path.
public final class Watchdog extends BroadcastReceiver {
    static final String ACTION_CHECK = "com.trelay.android.WATCHDOG_CHECK";
    private static final long INTERVAL_MS = 5 * 60 * 1000L;

    static void schedule(Context context) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms == null) return;
        long at = SystemClock.elapsedRealtime() + INTERVAL_MS;
        PendingIntent pending = pending(context);
        // Android 12+ only lets a background app start a foreground service from an *exact* alarm
        // (or when battery optimisation is off), so an inexact alarm could not bring the VPN back.
        boolean exact = Build.VERSION.SDK_INT < 31 || alarms.canScheduleExactAlarms();
        try {
            if (exact) alarms.setExactAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pending);
            else alarms.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pending);
        } catch (Exception denied) {
            try { alarms.setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, at, pending); } catch (Exception ignored) { }
        }
    }

    static void cancel(Context context) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms != null) alarms.cancel(pending(context));
    }

    private static PendingIntent pending(Context context) {
        Intent intent = new Intent(context, Watchdog.class).setAction(ACTION_CHECK);
        return PendingIntent.getBroadcast(context, 41, intent, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null || !ACTION_CHECK.equals(intent.getAction())) return;
        TrelayApp.init(context);
        try {
            if (TrelayVpnService.engine() != null) {
                context.startService(new Intent(context, TrelayVpnService.class).setAction(ACTION_CHECK));
            } else {
                EngineLog.write("WATCHDOG_RESTART service was not running");
                context.startForegroundService(new Intent(context, TrelayVpnService.class).setAction(TrelayVpnService.START));
            }
        } catch (Exception error) {
            // Android refused a background start (no exact-alarm right and battery optimisation on).
            // Telephony is dead until the user opens TRELAY, so this is a loud alert, not a log line.
            EngineLog.write("WATCHDOG_FAILED " + error.getClass().getSimpleName());
            Alerts.critical(context, "TRELAY остановлен",
                    "Android выгрузил TRELAY и не дал перезапустить его в фоне. Откройте TRELAY, чтобы маршруты и телефония снова заработали. " +
                            "Чтобы это не повторялось, отключите для TRELAY оптимизацию батареи.");
        }
        schedule(context);
    }
}
