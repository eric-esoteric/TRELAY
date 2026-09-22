using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TRELAY;

internal sealed class ProviderCatalog
{
	private readonly List<ProviderDefinition> definitions;

	public IList<ProviderDefinition> Definitions => definitions.AsReadOnly();

	private ProviderCatalog(List<ProviderDefinition> definitions)
	{
		this.definitions = definitions;
	}

	public static ProviderCatalog Load(SettingsStore store)
	{
		Dictionary<string, ProviderDefinition> dictionary = new Dictionary<string, ProviderDefinition>(StringComparer.OrdinalIgnoreCase);
		LoadDirectory(Path.Combine(Path.GetDirectoryName(typeof(ProviderCatalog).Assembly.Location), "providers"), dictionary);
		LoadDirectory(Path.Combine(store.StateDirectory, "providers"), dictionary);
		return new ProviderCatalog(dictionary.Values.OrderBy((ProviderDefinition x) => x.DisplayName).ToList());
	}

	public ProviderDefinition Resolve(TunnelProfile profile)
	{
		return Resolve(profile, null);
	}

	public ProviderDefinition Resolve(TunnelProfile profile, IEnumerable<string> observedHosts)
	{
		if (profile == null)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(profile.ProviderId) && !profile.ProviderId.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return definitions.FirstOrDefault((ProviderDefinition x) => x.Id.Equals(profile.ProviderId, StringComparison.OrdinalIgnoreCase));
		}
		List<string> names = (profile.AppNames ?? new List<string>()).Select(NormalizeProcess).ToList();
		ProviderDefinition providerDefinition = definitions.FirstOrDefault((ProviderDefinition provider) => provider.ProcessNames.Any((string name) => names.Contains(NormalizeProcess(name), StringComparer.OrdinalIgnoreCase)));
		if (providerDefinition != null)
		{
			return providerDefinition;
		}
		foreach (string item in observedHosts ?? Enumerable.Empty<string>())
		{
			foreach (ProviderDefinition definition in definitions)
			{
				foreach (string domainPattern in definition.DomainPatterns)
				{
					try
					{
						if (new Regex(domainPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250.0)).IsMatch(item))
						{
							return definition;
						}
					}
					catch
					{
					}
				}
			}
		}
		return null;
	}

	private static void LoadDirectory(string directory, IDictionary<string, ProviderDefinition> destination)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}
		JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
		javaScriptSerializer.MaxJsonLength = 4194304;
		JavaScriptSerializer javaScriptSerializer2 = javaScriptSerializer;
		foreach (string item in Directory.GetFiles(directory, "*.json").OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				ProviderDefinition providerDefinition = Parse(javaScriptSerializer2.DeserializeObject(File.ReadAllText(item)) as Dictionary<string, object>);
				Normalize(providerDefinition);
				if (providerDefinition != null && !string.IsNullOrWhiteSpace(providerDefinition.Id) && !string.IsNullOrWhiteSpace(providerDefinition.DisplayName))
				{
					destination[providerDefinition.Id] = providerDefinition;
				}
			}
			catch
			{
			}
		}
	}

	private static ProviderDefinition Parse(Dictionary<string, object> raw)
	{
		if (raw == null)
		{
			return null;
		}
		ProviderDefinition providerDefinition = new ProviderDefinition();
		providerDefinition.Id = StringValue(raw, "id");
		providerDefinition.DisplayName = StringValue(raw, "displayName");
		providerDefinition.Countries = StringList(raw, "countries");
		providerDefinition.ProcessNames = StringList(raw, "processNames");
		providerDefinition.DomainPatterns = StringList(raw, "domainPatterns");
		providerDefinition.IpRanges = StringList(raw, "ipRanges");
		providerDefinition.TcpPorts = StringList(raw, "tcpPorts");
		providerDefinition.UdpPorts = StringList(raw, "udpPorts");
		providerDefinition.LogPaths = StringList(raw, "logPaths");
		providerDefinition.EndpointPatterns = StringList(raw, "endpointPatterns");
		providerDefinition.HealthEndpoints = StringList(raw, "healthEndpoints");
		providerDefinition.SuccessPatterns = StringList(raw, "successPatterns");
		providerDefinition.FailurePatterns = StringList(raw, "failurePatterns");
		providerDefinition.DocumentationUrls = StringList(raw, "documentationUrls");
		return providerDefinition;
	}

	private static object Value(Dictionary<string, object> raw, string key)
	{
		KeyValuePair<string, object> keyValuePair = raw.FirstOrDefault((KeyValuePair<string, object> x) => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
		if (keyValuePair.Key != null)
		{
			return keyValuePair.Value;
		}
		return null;
	}

	private static string StringValue(Dictionary<string, object> raw, string key)
	{
		object obj = Value(raw, key);
		if (obj != null)
		{
			return obj.ToString().Trim();
		}
		return "";
	}

	private static List<string> StringList(Dictionary<string, object> raw, string key)
	{
		if (!(Value(raw, key) is IEnumerable enumerable) || enumerable is string)
		{
			return new List<string>();
		}
		return (from object x in enumerable
			where x != null && !string.IsNullOrWhiteSpace(x.ToString())
			select x.ToString().Trim()).ToList();
	}

	private static void Normalize(ProviderDefinition item)
	{
		if (item != null)
		{
			item.Countries = item.Countries ?? new List<string>();
			item.ProcessNames = item.ProcessNames ?? new List<string>();
			item.DomainPatterns = item.DomainPatterns ?? new List<string>();
			item.IpRanges = item.IpRanges ?? new List<string>();
			item.TcpPorts = item.TcpPorts ?? new List<string>();
			item.UdpPorts = item.UdpPorts ?? new List<string>();
			item.LogPaths = item.LogPaths ?? new List<string>();
			item.EndpointPatterns = item.EndpointPatterns ?? new List<string>();
			item.HealthEndpoints = item.HealthEndpoints ?? new List<string>();
			item.SuccessPatterns = item.SuccessPatterns ?? new List<string>();
			item.FailurePatterns = item.FailurePatterns ?? new List<string>();
			item.DocumentationUrls = item.DocumentationUrls ?? new List<string>();
			item.DocumentationUrls = item.DocumentationUrls.Where(IsHttpsUrl).ToList();
			item.LogPaths = item.LogPaths.Where((string path) => !string.IsNullOrWhiteSpace(ExpandPath(path))).ToList();
			item.DomainPatterns = item.DomainPatterns.Where(IsSafeRegex).ToList();
			item.EndpointPatterns = item.EndpointPatterns.Where(IsSafeRegex).ToList();
		}
	}

	internal static string NormalizeProcess(string name)
	{
		name = (name ?? "").Trim();
		if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			return name;
		}
		return name.Substring(0, name.Length - 4);
	}

	internal static string ExpandPath(string path)
	{
		try
		{
			string text = Environment.ExpandEnvironmentVariables(path ?? "");
			if (text.StartsWith("\\\\", StringComparison.Ordinal))
			{
				return "";
			}
			string full = Path.GetFullPath(text);
			string[] source = new string[3]
			{
				Path.GetFullPath(Path.GetTempPath()),
				Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
				Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
			};
			return source.Any((string root) => full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? full : "";
		}
		catch
		{
			return "";
		}
	}

	private static bool IsSafeRegex(string pattern)
	{
		try
		{
			new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250.0));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsHttpsUrl(string value)
	{
		if (Uri.TryCreate(value, UriKind.Absolute, out var result))
		{
			return result.Scheme == Uri.UriSchemeHttps;
		}
		return false;
	}
}
