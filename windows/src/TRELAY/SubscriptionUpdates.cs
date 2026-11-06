using System;
using System.Collections.Generic;
using System.Linq;

namespace TRELAY;

internal static class SubscriptionUpdates
{
	private const int MaxReplacements = 8;

	internal static string SaveSource(SecretStore secrets, string url)
	{
		string id = "subscription-" + Guid.NewGuid().ToString("N");
		secrets.Save(id, url);
		return id;
	}

	internal static string ConnectionKey(string link)
	{
		int fragment = link.IndexOf('#');
		return fragment < 0 ? link : link.Substring(0, fragment);
	}

	internal static List<string> Candidates(IEnumerable<string> links)
	{
		return links.Where(link => !link.StartsWith("key://", StringComparison.OrdinalIgnoreCase) &&
			!link.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase) &&
			!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
			ConnectionValidator.ValidateKey(link, out _, out _))
			.GroupBy(ConnectionKey, StringComparer.Ordinal).Select(group => group.First()).ToList();
	}

	internal static string Replacement(string previous, bool singleServerSubscription, List<string> candidates)
	{
		if (singleServerSubscription && candidates.Count == 1) return candidates[0];
		string name = ConnectionValidator.SuggestProfileName(previous);
		string endpoint = Endpoint(previous);
		var matches = candidates.Where(link =>
			link.Split(':')[0].Equals(previous.Split(':')[0], StringComparison.OrdinalIgnoreCase) &&
			((string.Equals(name, ConnectionValidator.SuggestProfileName(link), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name)) ||
			(string.Equals(endpoint, Endpoint(link), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(endpoint))))
			.Take(2).ToList();
		return matches.Count == 1 ? matches[0] : null;
	}

	private static string Endpoint(string link)
	{
		if (link.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase)) return EmbeddedXrayConfig.GetEndpoint(link);
		if (link.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase)) return MihomoConfig.GetEndpoint(link);
		if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Port < 1 || link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return "";
		return uri.Authority;
	}

	internal static List<TunnelProfile> Stage(List<TunnelProfile> profiles, string source, List<string> candidates,
		Func<string, string> loadKey, Action<string, string> saveKey, List<TunnelProfile> additions)
	{
		var next = new List<TunnelProfile>(profiles);
		var existing = profiles.Where(profile => profile.SubscriptionId == source).ToList();
		var keys = new HashSet<string>(candidates.Select(ConnectionKey), StringComparer.Ordinal);
		var known = new HashSet<string>(existing.Select(profile => loadKey(profile.Id)).Where(link => link != null)
			.Select(ConnectionKey), StringComparer.Ordinal);
		var proposals = new List<(TunnelProfile Profile, string Link)>();
		foreach (TunnelProfile profile in existing.Where(profile => profile.Enabled && !profile.SubscriptionReplacement))
		{
			string previous = loadKey(profile.Id);
			if (previous == null || keys.Contains(ConnectionKey(previous))) continue;
			string link = Replacement(previous, profile.SingleServerSubscription, candidates);
			if (link != null && !known.Contains(ConnectionKey(link))) proposals.Add((profile, link));
		}
		int count = existing.Count(profile => profile.SubscriptionReplacement);
		foreach (var proposal in proposals.GroupBy(item => ConnectionKey(item.Link)).Where(group => group.Count() == 1).Select(group => group.First()))
		{
			if (count >= MaxReplacements) break;
			TunnelProfile profile = proposal.Profile;
			ConnectionValidator.ValidateKey(proposal.Link, out string kind, out _);
			var reserve = new TunnelProfile
			{
				Name = profile.Name + " · из подписки",
				ConnectionType = "key",
				KeyKind = kind,
				Country = profile.Country,
				ProviderId = profile.ProviderId,
				TelephonyOptimized = profile.TelephonyOptimized,
				HealthCheckEndpoint = profile.HealthCheckEndpoint,
				SocksPort = profile.SocksPort,
				FailoverGroupId = profile.FailoverGroupId,
				FailoverPriority = profile.FailoverPriority,
				SubscriptionId = source,
				SubscriptionReplacement = true,
				AppNames = new List<string>(profile.AppNames),
				AppPaths = new List<string>(profile.AppPaths)
			};
			saveKey(reserve.Id, proposal.Link);
			additions.Add(reserve);
			next.Insert(next.IndexOf(profile) + 1, reserve);
			count++;
		}
		return next;
	}
}
