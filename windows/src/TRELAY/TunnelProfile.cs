using System;
using System.Collections.Generic;

namespace TRELAY;

public sealed class TunnelProfile
{
	public string Id { get; set; }

	public string Name { get; set; }

	public string XrayConfigPath { get; set; }

	public string ConnectionType { get; set; }

	public string KeyKind { get; set; }

	public string Country { get; set; }

	public string ProviderId { get; set; }

	public bool TelephonyOptimized { get; set; }

	public string HealthCheckEndpoint { get; set; }

	public int SocksPort { get; set; }

	public bool Enabled { get; set; }

	public string FailoverGroupId { get; set; }

	public int FailoverPriority { get; set; }

	public List<string> AppNames { get; set; }

	public List<string> AppPaths { get; set; }

	public TunnelProfile()
	{
		Id = Guid.NewGuid().ToString("N");
		Name = "Новый профиль";
		XrayConfigPath = "";
		ConnectionType = "file";
		KeyKind = "";
		Country = "Авто";
		ProviderId = "auto";
		TelephonyOptimized = false;
		HealthCheckEndpoint = "";
		SocksPort = 10810;
		Enabled = true;
		FailoverGroupId = "";
		FailoverPriority = 0;
		AppNames = new List<string>();
		AppPaths = new List<string>();
	}

	public override string ToString()
	{
		return Name + (Enabled ? "" : "  [выключен]");
	}
}
