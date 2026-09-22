package com.trelay.android;

import android.app.Notification;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;

// Loud alert from outside the VPN service (watchdog, autostart): the Windows build screams when the
// connection is really gone, and a TRELAY that Android refused to restart is exactly that case.
final class Alerts {
    private Alerts() { }

    static void critical(Context context, String title, String message) {
        try {
            TrelayVpnService.createChannels(context);
            Intent open = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            PendingIntent content = PendingIntent.getActivity(context, 3, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            Notification notification = new Notification.Builder(context, TrelayVpnService.CHANNEL_ALERTS)
                    .setSmallIcon(R.drawable.ic_stat_trelay)
                    .setContentTitle(title)
                    .setContentText(message)
                    .setStyle(new Notification.BigTextStyle().bigText(message))
                    .setContentIntent(content)
                    .setAutoCancel(true)
                    .setCategory(Notification.CATEGORY_ALARM)
                    .build();
            context.getSystemService(NotificationManager.class).notify(1999, notification);
            Model.Settings settings = new ConfigStore(context).load().settings;
            if (settings.soundAlerts) AlertSound.play(context, true, settings, false);
        } catch (Exception ignored) { }
    }
}
