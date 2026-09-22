using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TRELAY;

internal sealed class AppConnectionWatcher : IDisposable
{
	private sealed class TailFile
	{
		internal string Path;

		internal long Position = -1L;

		internal string Partial = "";
	}

	private sealed class Watched
	{
		internal ProviderDefinition Provider;

		internal List<TailFile> Files = new List<TailFile>();

		internal List<Regex> EndpointPatterns = new List<Regex>();

		internal ProviderConnectionState State;

		internal string Detail = "";

		internal DateTime ChangedUtc = DateTime.MinValue;

		internal readonly List<string> Endpoints = new List<string>();
	}

	private const int PollMilliseconds = 250;

	private const int InitialTailBytes = 524288;

	private const int MaxReadBytes = 1048576;

	private readonly object sync = new object();

	private readonly Dictionary<string, Watched> watched = new Dictionary<string, Watched>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, List<string>> endpointMemory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

	private readonly AutoResetEvent wake = new AutoResetEvent(initialState: false);

	private readonly Thread thread;

	private volatile bool disposed;

	internal event Action<ProviderDefinition, ProviderConnectionState, string> Changed;

	internal AppConnectionWatcher()
	{
		thread = new Thread(Loop)
		{
			IsBackground = true,
			Name = "TRELAYAppWatcher"
		};
		thread.Start();
	}

	internal void SetProviders(IEnumerable<ProviderDefinition> providers)
	{
		List<ProviderDefinition> list = (from g in (providers ?? Enumerable.Empty<ProviderDefinition>()).Where((ProviderDefinition p) => p?.CanVerifyConnection ?? false).GroupBy((ProviderDefinition p) => p.Id, StringComparer.OrdinalIgnoreCase)
			select g.First()).ToList();
		lock (sync)
		{
			foreach (string id in this.watched.Keys.ToList())
			{
				if (!list.Any((ProviderDefinition p) => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
				{
					this.watched.Remove(id);
				}
			}
			foreach (ProviderDefinition item in list)
			{
				if (this.watched.ContainsKey(item.Id))
				{
					this.watched[item.Id].Provider = item;
					continue;
				}
				Watched watched = new Watched();
				watched.Provider = item;
				Watched watched2 = watched;
				if (endpointMemory.TryGetValue(item.Id, out var value))
				{
					watched2.Endpoints.AddRange(value);
				}
				foreach (string item2 in (from x in item.LogPaths.Select(ProviderCatalog.ExpandPath)
					where x.Length > 0
					select x).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					watched2.Files.Add(new TailFile
					{
						Path = item2
					});
				}
				foreach (string endpointPattern in item.EndpointPatterns)
				{
					try
					{
						watched2.EndpointPatterns.Add(new Regex(endpointPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250.0)));
					}
					catch
					{
					}
				}
				this.watched.Add(item.Id, watched2);
			}
		}
		wake.Set();
	}

	internal ProviderConnectionState GetState(string providerId, out string detail, out DateTime changedUtc)
	{
		lock (sync)
		{
			if (providerId == null || !watched.TryGetValue(providerId, out var value))
			{
				detail = "";
				changedUtc = DateTime.MinValue;
				return ProviderConnectionState.Unknown;
			}
			detail = value.Detail;
			changedUtc = value.ChangedUtc;
			return value.State;
		}
	}

	internal List<string> GetEndpoints(string providerId)
	{
		lock (sync)
		{
			Watched value;
			return (providerId != null && watched.TryGetValue(providerId, out value)) ? value.Endpoints.ToList() : new List<string>();
		}
	}

	private void Loop()
	{
		while (!disposed)
		{
			List<Watched> list;
			lock (sync)
			{
				list = watched.Values.ToList();
			}
			foreach (Watched item in list)
			{
				foreach (TailFile file in item.Files)
				{
					if (disposed)
					{
						return;
					}
					try
					{
						Poll(item, file);
					}
					catch (Exception)
					{
					}
				}
			}
			try
			{
				wake.WaitOne(250);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
		}
	}

	private void Poll(Watched item, TailFile file)
	{
		long length;
		try
		{
			FileInfo fileInfo = new FileInfo(file.Path);
			if (!fileInfo.Exists)
			{
				if (file.Position > 0)
				{
					file.Position = 0L;
				}
				return;
			}
			length = fileInfo.Length;
		}
		catch (IOException)
		{
			return;
		}
		catch (UnauthorizedAccessException)
		{
			return;
		}
		bool flag = file.Position < 0;
		if (flag)
		{
			file.Position = Math.Max(0L, length - 524288);
		}
		else if (length < file.Position)
		{
			file.Position = 0L;
			file.Partial = "";
		}
		if (length == file.Position)
		{
			return;
		}
		string text;
		using (FileStream fileStream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			length = fileStream.Length;
			if (length < file.Position)
			{
				file.Position = 0L;
				file.Partial = "";
			}
			int num = (int)Math.Min(length - file.Position, 1048576L);
			if (num <= 0)
			{
				return;
			}
			fileStream.Position = file.Position;
			byte[] array = new byte[num];
			int i;
			int num2;
			for (i = 0; i < num; i += num2)
			{
				num2 = fileStream.Read(array, i, num - i);
				if (num2 <= 0)
				{
					break;
				}
			}
			file.Position += i;
			text = Encoding.UTF8.GetString(array, 0, i);
		}
		text = file.Partial + text;
		int num3 = text.LastIndexOf('\n');
		if (num3 < 0)
		{
			file.Partial = ((text.Length > 65536) ? "" : text);
			return;
		}
		file.Partial = text.Substring(num3 + 1);
		if (file.Partial.Length > 65536)
		{
			file.Partial = "";
		}
		string[] array2 = text.Substring(0, num3).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		if (flag && file.Position > 0 && array2.Length > 0 && length > 524288)
		{
			array2 = array2.Skip(1).ToArray();
		}
		string[] array3 = array2;
		foreach (string raw in array3)
		{
			Process(item, raw, !flag);
		}
	}

	private void Process(Watched item, string raw, bool live)
	{
		string line = raw.ToLowerInvariant();
		ProviderDefinition provider = item.Provider;
		foreach (Regex endpointPattern in item.EndpointPatterns)
		{
			try
			{
				foreach (Match item2 in endpointPattern.Matches(raw))
				{
					lock (sync)
					{
						AddEndpoint(item, item2.Value);
					}
				}
			}
			catch (RegexMatchTimeoutException)
			{
			}
		}
		ProviderConnectionState providerConnectionState;
		string text;
		if (provider.SuccessPatterns.Any((string x) => !string.IsNullOrEmpty(x) && line.Contains(x.ToLowerInvariant())))
		{
			providerConnectionState = ProviderConnectionState.Connected;
			text = provider.DisplayName + ": соединение установлено";
		}
		else
		{
			if (!provider.FailurePatterns.Any((string x) => !string.IsNullOrEmpty(x) && line.Contains(x.ToLowerInvariant())))
			{
				return;
			}
			providerConnectionState = ProviderConnectionState.Disconnected;
			text = (line.Contains("timeout") ? "тайм-аут подключения" : "соединение разорвано");
		}
		bool flag;
		lock (sync)
		{
			flag = item.State != providerConnectionState;
			item.State = providerConnectionState;
			item.Detail = text;
			if (flag && live)
			{
				item.ChangedUtc = DateTime.UtcNow;
			}
		}
		if (!flag || !live)
		{
			return;
		}
		Action<ProviderDefinition, ProviderConnectionState, string> changed = this.Changed;
		if (changed == null)
		{
			return;
		}
		Delegate[] invocationList = changed.GetInvocationList();
		for (int num = 0; num < invocationList.Length; num++)
		{
			Action<ProviderDefinition, ProviderConnectionState, string> action = (Action<ProviderDefinition, ProviderConnectionState, string>)invocationList[num];
			try
			{
				action(provider, providerConnectionState, text);
			}
			catch
			{
			}
		}
	}

	private void AddEndpoint(Watched item, string value)
	{
		value = (value ?? "").Trim().ToLowerInvariant();
		if (value.Length != 0 && value.Length <= 253)
		{
			if (!value.Contains(":"))
			{
				value += ":443";
			}
			item.Endpoints.Remove(value);
			item.Endpoints.Insert(0, value);
			if (item.Endpoints.Count > 16)
			{
				item.Endpoints.RemoveAt(item.Endpoints.Count - 1);
			}
			if (!endpointMemory.TryGetValue(item.Provider.Id, out var value2))
			{
				value2 = new List<string>();
				endpointMemory[item.Provider.Id] = value2;
			}
			value2.Remove(value);
			value2.Insert(0, value);
			if (value2.Count > 16)
			{
				value2.RemoveAt(value2.Count - 1);
			}
		}
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			try
			{
				wake.Set();
			}
			catch
			{
			}
			if (Thread.CurrentThread != thread)
			{
				thread.Join(2000);
			}
			wake.Dispose();
		}
	}
}
