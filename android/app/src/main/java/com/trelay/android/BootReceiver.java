package com.trelay.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.VpnService;

public final class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        String action = intent == null ? null : intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action) && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) return;
        try {
            TrelayApp.init(context);
            Model.Config config = new ConfigStore(context).load();
            if (!config.settings.autostart || VpnService.prepare(context) != null) return;
            EngineLog.write("AUTOSTART " + action);
            TrelayVpnService.start(context);
        } catch (Exception error) {
            EngineLog.write("AUTOSTART_FAILED " + error.getClass().getSimpleName());
        }
    }
}
