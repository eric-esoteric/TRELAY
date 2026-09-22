using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TRELAY;

internal static class ProviderEndpointDiscovery
{
	public static List<string> Discover(TunnelProfile profile, ProviderDefinition provider)
	{
		List<string> list = new List<string>();
		Add(list, (profile == null) ? "" : profile.HealthCheckEndpoint);
		if (provider == null)
		{
			return list;
		}
		foreach (string healthEndpoint in provider.HealthEndpoints)
		{
			Add(list, healthEndpoint);
		}
		foreach (string logPath in provider.LogPaths)
		{
			string input = ReadTail(ProviderCatalog.ExpandPath(logPath));
			foreach (string endpointPattern in provider.EndpointPatterns)
			{
				try
				{
					foreach (Match item in new Regex(endpointPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250.0)).Matches(input))
					{
						Add(list, item.Value);
					}
				}
				catch
				{
				}
			}
		}
		return list;
	}

	private static string ReadTail(string path)
	{
		if (!File.Exists(path))
		{
			return "";
		}
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			int num = (int)Math.Min(fileStream.Length, 1048576L);
			fileStream.Position = fileStream.Length - num;
			byte[] array = new byte[num];
			int count = fileStream.Read(array, 0, num);
			return Encoding.UTF8.GetString(array, 0, count);
		}
		catch
		{
			return "";
		}
	}

	private static void Add(List<string> endpoints, string endpoint)
	{
		endpoint = (endpoint ?? "").Trim();
		if (endpoint.Length != 0)
		{
			if (!endpoint.Contains(":"))
			{
				endpoint += ":443";
			}
			if (!endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
			{
				endpoints.Add(endpoint);
			}
		}
	}
}
