package com.trelay.android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

// The SIP app (TLINE) asks this when it starts: it has no idea which route carries it until TRELAY
// says so, and waiting for the next heartbeat would leave its status line blank for minutes.
public final class SipQueryReceiver extends BroadcastReceiver {
    static final String ACTION_QUERY = "com.trelay.android.SIP_STATE_QUERY";

    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null || !ACTION_QUERY.equals(intent.getAction())) return;
        TrelayApp.init(context);
        TunnelEngine engine = TrelayVpnService.engine();
        Model.Config config;
        try { config = new ConfigStore(context).load(); } catch (Exception error) { return; }
        if (engine == null) SipPeer.offline(context, config);
        else SipPeer.publish(context, config, engine, true);
    }
}
