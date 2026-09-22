using System;
using System.Collections.Generic;

namespace TRELAY;

public sealed class AppConfig
{
	public string Theme { get; set; }

	public bool StartWithWindows { get; set; }

	public bool StartMinimized { get; set; }

	public bool SoundAlerts { get; set; }

	public string AlertOutputDevice { get; set; }

	public string XrayPath { get; set; }

	public string MihomoPath { get; set; }

	public string ProxiFyrePath { get; set; }

	public List<TunnelProfile> Profiles { get; set; }

	public Dictionary<string, string> TrustedSshHosts { get; set; }

	public List<string> AcknowledgedVpsRecoveryIds { get; set; }

	public AppConfig()
	{
		Theme = "Soft";
		StartWithWindows = false;
		StartMinimized = false;
		SoundAlerts = true;
		AlertOutputDevice = "speakers";
		XrayPath = "";
		MihomoPath = "";
		ProxiFyrePath = "";
		Profiles = new List<TunnelProfile>();
		TrustedSshHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		AcknowledgedVpsRecoveryIds = new List<string>();
	}
}
