using System.Collections.Generic;

namespace TRELAY;

internal sealed class ProviderDefinition
{
	public string Id { get; set; }

	public string DisplayName { get; set; }

	public List<string> Countries { get; set; }

	public List<string> ProcessNames { get; set; }

	public List<string> DomainPatterns { get; set; }

	public List<string> IpRanges { get; set; }

	public List<string> TcpPorts { get; set; }

	public List<string> UdpPorts { get; set; }

	public List<string> LogPaths { get; set; }

	public List<string> EndpointPatterns { get; set; }

	public List<string> HealthEndpoints { get; set; }

	public List<string> SuccessPatterns { get; set; }

	public List<string> FailurePatterns { get; set; }

	public List<string> DocumentationUrls { get; set; }

	public bool CanVerifyConnection
	{
		get
		{
			if (LogPaths.Count > 0)
			{
				if (SuccessPatterns.Count <= 0)
				{
					return FailurePatterns.Count > 0;
				}
				return true;
			}
			return false;
		}
	}

	public ProviderDefinition()
	{
		Countries = new List<string>();
		ProcessNames = new List<string>();
		DomainPatterns = new List<string>();
		IpRanges = new List<string>();
		TcpPorts = new List<string>();
		UdpPorts = new List<string>();
		LogPaths = new List<string>();
		EndpointPatterns = new List<string>();
		HealthEndpoints = new List<string>();
		SuccessPatterns = new List<string>();
		FailurePatterns = new List<string>();
		DocumentationUrls = new List<string>();
	}
}
