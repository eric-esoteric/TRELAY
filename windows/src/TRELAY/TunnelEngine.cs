using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TRELAY;

internal sealed class TunnelEngine : IDisposable
{
	private enum Health
	{
		Starting,
		Healthy,
		Unhealthy,
		Down
	}

	private sealed class Request
	{
		internal readonly AppConfig Config;

		internal readonly int Generation;

		internal Request(AppConfig config, int generation)
		{
			Config = config;
			Generation = generation;
		}
	}

	private sealed class ConfigurationException : Exception
	{
		internal ConfigurationException(string message)
			: base(message)
		{
		}
	}

	private sealed class EngineFailureException : IOException
	{
		internal EngineFailureException(string message)
			: base(message)
		{
		}
	}

	private sealed class EngineLaunch
	{
		internal string Executable;

		internal string Arguments;

		internal string WorkingDirectory;

		internal string ConfigPath;
	}

	private sealed class Backend
	{
		internal readonly string Id;

		internal readonly string Fingerprint;

		internal readonly RelayTarget Target;

		internal readonly CancellationTokenSource Life = new CancellationTokenSource();

		internal readonly AutoResetEvent ProbeSignal = new AutoResetEvent(initialState: false);

		internal readonly object Gate = new object();

		internal TunnelProfile Profile;

		internal ProviderDefinition Provider;

		internal DateTime ProviderCheckedUtc;

		internal Process Process;

		internal Health Health;

		internal int Failures;

		internal int Successes;

		internal DateTime HealthySinceUtc = DateTime.MinValue;

		internal DateTime LastFailureUtc = DateTime.MinValue;

		internal DateTime FirstFailureUtc = DateTime.MinValue;

		internal DateTime LastSuccessUtc = DateTime.MinValue;

		internal string Detail = "запуск";

		internal volatile bool IsActive;

		internal volatile bool ProbesService;

		internal volatile bool ServiceCheckFailed;

		internal int ServiceFailures;

		internal DateTime LastServiceFailureUtc = DateTime.MinValue;

		internal long LastProbeRequestTicks;

		internal long TraceAppFailureTicks;

		internal int Priority;

		internal string Name
		{
			get
			{
				lock (Gate)
				{
					return Profile.Name;
				}
			}
		}

		internal Health CurrentHealth
		{
			get
			{
				lock (Gate)
				{
					return Health;
				}
			}
		}

		internal Backend(TunnelProfile profile, string fingerprint)
		{
			Id = profile.Id;
			Profile = profile;
			Fingerprint = fingerprint;
			Target = new RelayTarget(profile.Id, profile.Name, 0);
		}
	}

	private sealed class Group
	{
		internal string Key;

		internal List<string> Apps = new List<string>();

		internal List<Backend> Candidates = new List<Backend>();

		internal RouteRelay Relay;

		internal volatile Backend Active;

		internal volatile RelayTarget[] Targets = new RelayTarget[0];

		internal ProviderDefinition Provider;

		internal readonly DateTime CreatedUtc = DateTime.UtcNow;

		internal bool LostAlerted;

		internal DateTime LostSinceUtc = DateTime.MinValue;

		internal DateTime LastFailoverUtc = DateTime.MinValue;

		internal string Label
		{
			get
			{
				if (Apps.Count > 2)
				{
					return string.Join(", ", Apps.Take(2)) + " и ещё " + (Apps.Count - 2);
				}
				return string.Join(", ", Apps);
			}
		}
	}

	private struct JobLimits
	{
		internal long PerProcessUserTimeLimit;

		internal long PerJobUserTimeLimit;

		internal uint LimitFlags;

		internal UIntPtr MinimumWorkingSetSize;

		internal UIntPtr MaximumWorkingSetSize;

		internal uint ActiveProcessLimit;

		internal UIntPtr Affinity;

		internal uint PriorityClass;

		internal uint SchedulingClass;
	}

	private struct JobExtendedLimits
	{
		internal JobLimits BasicLimitInformation;

		internal ulong ReadOperationCount;

		internal ulong WriteOperationCount;

		internal ulong OtherOperationCount;

		internal ulong ReadTransferCount;

		internal ulong WriteTransferCount;

		internal ulong OtherTransferCount;

		internal UIntPtr ProcessMemoryLimit;

		internal UIntPtr JobMemoryLimit;

		internal UIntPtr PeakProcessMemoryUsed;

		internal UIntPtr PeakJobMemoryUsed;
	}

	private const int ActiveProbeIntervalMs = 4000;

	private const int ConfirmProbeDelayMs = 1000;

	private const int StandbyProbeIntervalMs = 5000;

	private const int StartingProbeIntervalMs = 1000;

	private const int ActiveProbeTimeoutMs = 6000;

	private const int StandbyProbeTimeoutMs = 8000;

	private const int FailureThreshold = 2;

	private const int StartingFailureThreshold = 3;

	private const int RecoverySuccesses = 2;

	private const int ConfirmProbeTimeoutMs = 4000;

	private const int ServiceProbeIntervalMs = 12000;

	private static readonly TimeSpan FreshBackupWindow = TimeSpan.FromSeconds(12.0);

	private static readonly TimeSpan RecoveryHold = TimeSpan.FromMinutes(2.0);

	private static readonly TimeSpan RouteLossAlertHold = TimeSpan.FromSeconds(5.0);

	private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(12.0);

	private static readonly TimeSpan AppAlertQuietAfterRouterStart = TimeSpan.FromSeconds(15.0);

	private static readonly TimeSpan AppAlertQuietAfterFailover = TimeSpan.FromSeconds(30.0);

	private readonly object sync = new object();

	private readonly object logSync = new object();

	private readonly object processGate = new object();

	private readonly object filesGate = new object();

	private readonly AutoResetEvent wake = new AutoResetEvent(initialState: false);

	private readonly JavaScriptSerializer json = new JavaScriptSerializer
	{
		MaxJsonLength = 33554432
	};

	private readonly SettingsStore store;

	private readonly SecretStore secrets;

	private readonly ProviderCatalog providerCatalog;

	private readonly bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

	private readonly Dictionary<string, Backend> backends = new Dictionary<string, Backend>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, Group> groups = new Dictionary<string, Group>(StringComparer.Ordinal);

	private readonly List<string> skipped = new List<string>();

	private readonly Dictionary<string, string> skippedProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private readonly List<string> uncovered = new List<string>();

	private readonly List<DateTime> proxyCrashes = new List<DateTime>();

	private readonly HashSet<int> reservedPorts = new HashSet<int>();

	private readonly List<string> runtimeFiles = new List<string>();

	private volatile Dictionary<string, Backend> backendIndex = new Dictionary<string, Backend>(StringComparer.OrdinalIgnoreCase);

	private AppConfig config;

	private Process proxyProcess;

	private string proxyMapping;

	private DateTime nextProxyStartUtc = DateTime.MinValue;

	private DateTime lastRouterAlertUtc = DateTime.MinValue;

	private long proxyStartedTicks;

	private Mutex runtimeLease;

	private IntPtr processJob;

	private bool runtimeActive;

	private AppConnectionWatcher watcher;

	private DateTime nextProviderRefreshUtc = DateTime.MinValue;

	private string runtimeSignature = "";

	private readonly Dictionary<string, DateTime> appAlerted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, DateTime> providerFailoverUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, List<string>> knownEndpoints = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

	private readonly object endpointGate = new object();

	private volatile Dictionary<string, RouteRelay> relayByBackend = new Dictionary<string, RouteRelay>(StringComparer.OrdinalIgnoreCase);

	private Thread worker;

	private volatile bool disposed;

	private Request requested;

	private int generation;

	private EngineState state;

	private string statusText;

	private Dictionary<string, ProfileRuntimeInfo> runtimeInfo = new Dictionary<string, ProfileRuntimeInfo>(StringComparer.OrdinalIgnoreCase);

	public EngineState State
	{
		get
		{
			lock (sync)
			{
				return state;
			}
		}
	}

	public string StatusText
	{
		get
		{
			lock (sync)
			{
				return statusText;
			}
		}
	}

	public event Action StateChanged;

	public event Action<RouteSwitchInfo> RouteSwitched;

	public event Action<EngineAlert> Alert;

	public TunnelEngine(SettingsStore store)
	{
		if (store == null)
		{
			throw new ArgumentNullException("store");
		}
		this.store = store;
		secrets = new SecretStore(store);
		providerCatalog = ProviderCatalog.Load(store);
		LoadKnownEndpoints();
		state = EngineState.Off;
		statusText = "Маршрутизация выключена";
		ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
		ThreadPool.SetMinThreads(Math.Max(workerThreads, 32), Math.Max(completionPortThreads, 32));
	}

	public Dictionary<string, ProfileRuntimeInfo> GetRuntimeInfo()
	{
		lock (sync)
		{
			return runtimeInfo.ToDictionary((KeyValuePair<string, ProfileRuntimeInfo> x) => x.Key, (KeyValuePair<string, ProfileRuntimeInfo> x) => new ProfileRuntimeInfo
			{
				Status = x.Value.Status,
				Detail = x.Value.Detail
			}, StringComparer.OrdinalIgnoreCase);
		}
	}

	public void Start(AppConfig newConfig)
	{
		if (newConfig == null)
		{
			throw new ArgumentNullException("newConfig");
		}
		JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
		javaScriptSerializer.MaxJsonLength = 33554432;
		JavaScriptSerializer javaScriptSerializer2 = javaScriptSerializer;
		AppConfig appConfig = javaScriptSerializer2.Deserialize<AppConfig>(javaScriptSerializer2.Serialize(newConfig));
		lock (sync)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("TunnelEngine");
			}
			bool flag = requested != null && state != EngineState.Off && state != EngineState.Stopping;
			requested = new Request(appConfig, ++generation);
			if (!flag)
			{
				state = EngineState.Starting;
				statusText = "Запускаю маршрутизацию…";
			}
			if (worker == null)
			{
				worker = new Thread(WorkerLoop)
				{
					IsBackground = true,
					Name = "TRELAYEngine"
				};
				worker.Start();
			}
			wake.Set();
		}
		NotifyStateChanged();
	}

	public void Restart(AppConfig newConfig)
	{
		Start(newConfig);
	}

	public void Stop()
	{
		lock (sync)
		{
			if (disposed)
			{
				return;
			}
			requested = null;
			generation++;
			state = ((worker != null) ? EngineState.Stopping : EngineState.Off);
			statusText = ((worker == null) ? "Маршрутизация выключена" : "Останавливаю маршрутизацию…");
			wake.Set();
		}
		NotifyStateChanged();
	}

	private void WorkerLoop()
	{
		Request request = null;
		bool flag = false;
		try
		{
			while (!disposed)
			{
				Request request2;
				lock (sync)
				{
					request2 = requested;
					wake.WaitOne(0);
				}
				if (request2 == null)
				{
					if (request != null || runtimeActive)
					{
						TearDown();
					}
					request = null;
					flag = false;
					SetStoppedState();
					if (!disposed)
					{
						wake.WaitOne();
						continue;
					}
					break;
				}
				if (!object.ReferenceEquals(request2, request))
				{
					request = request2;
					flag = false;
					try
					{
						Apply(request2);
					}
					catch (ConfigurationException ex)
					{
						TearDown();
						SetState(request2.Generation, EngineState.Error, ex.Message);
						flag = true;
					}
					catch (Exception error)
					{
						TearDown();
						string text = SafeError(error);
						AppendLog("APPLY_FAILED " + text);
						SetState(request2.Generation, EngineState.Error, text + " Повтор через 5 с.");
						request = null;
						if (!disposed)
						{
							wake.WaitOne(5000);
						}
						continue;
					}
				}
				if (flag)
				{
					wake.WaitOne();
					continue;
				}
				try
				{
					Supervise(request.Generation);
				}
				catch (Exception ex2)
				{
					AppendLog("SUPERVISE_ERROR " + ex2.GetType().Name + " " + SafeError(ex2));
				}
				if (!disposed)
				{
					wake.WaitOne(250);
				}
			}
		}
		finally
		{
			try
			{
				TearDown();
			}
			catch
			{
			}
			SetStoppedState();
			wake.Dispose();
		}
	}

	private void Wake()
	{
		try
		{
			if (!disposed)
			{
				wake.Set();
			}
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void Apply(Request request)
	{
		string a = ((config == null) ? null : config.ProxiFyrePath);
		config = request.Config;
		if (runtimeActive && !string.Equals(a, config.ProxiFyrePath, StringComparison.OrdinalIgnoreCase))
		{
			TearDown();
		}
		ValidateConfig(!runtimeActive);
		if (!runtimeActive)
		{
			AcquireRuntimeLease();
			PurgeRuntimeDirectory();
			lock (processGate)
			{
				if (isWindows)
				{
					EnsureProcessJob();
				}
				runtimeActive = true;
			}
			watcher = new AppConnectionWatcher();
			watcher.Changed += OnAppStateChanged;
			proxyCrashes.Clear();
			nextProxyStartUtc = DateTime.MinValue;
			AppendLog("RUNTIME_STARTED");
		}
		Reconcile(request.Generation);
	}

	private List<TunnelProfile> ActiveProfiles()
	{
		return config.Profiles.Where((TunnelProfile p) => p.Enabled && p.AppNames != null && p.AppNames.Any((string a) => !string.IsNullOrWhiteSpace(a))).ToList();
	}

	private void Reconcile(int runGeneration)
	{
		skipped.Clear();
		skippedProfiles.Clear();
		uncovered.Clear();
		List<Tuple<TunnelProfile, string>> list = new List<Tuple<TunnelProfile, string>>();
		foreach (TunnelProfile item in ActiveProfiles())
		{
			try
			{
				ValidateProfile(item);
				list.Add(Tuple.Create(item, Fingerprint(item)));
			}
			catch (Exception ex)
			{
				string text = ((ex is ConfigurationException) ? ex.Message : SafeError(ex));
				skipped.Add("«" + SafeProfileName(item.Name) + "»: " + text);
				skippedProfiles[item.Id] = text;
				AppendLog("PROFILE_SKIPPED name=" + SafeProfileName(item.Name) + " " + text);
			}
		}
		foreach (Backend existing in backends.Values.ToList())
		{
			Func<Tuple<TunnelProfile, string>, bool> predicate = (Tuple<TunnelProfile, string> d) => d.Item1.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase);
			Tuple<TunnelProfile, string> tuple = list.FirstOrDefault(predicate);
			if (tuple == null || !(tuple.Item2 == existing.Fingerprint))
			{
				StopBackend(existing, (tuple == null) ? "маршрут выключен" : "настройки изменены");
			}
		}
		foreach (Tuple<TunnelProfile, string> item2 in list)
		{
			if (backends.TryGetValue(item2.Item1.Id, out var backend))
			{
				lock (backend.Gate)
				{
					backend.Profile = item2.Item1;
				}
			}
			else
			{
				backend = new Backend(item2.Item1, item2.Item2);
				backends.Add(backend.Id, backend);
				StartBackend(backend);
			}
			backend.Priority = config.Profiles.FindIndex((TunnelProfile p) => p.Id.Equals(backend.Id, StringComparison.OrdinalIgnoreCase));
			ResolveBackendProvider(backend, force: true);
		}
		backendIndex = new Dictionary<string, Backend>(backends, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<Backend>> dictionary = new Dictionary<string, List<Backend>>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> appDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (TunnelProfile item3 in ActiveProfiles())
		{
			foreach (string item4 in (from a in item3.AppNames.Select(NormalizeProcessName)
				where a.Length > 0
				select a).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (!dictionary.ContainsKey(item4))
				{
					dictionary[item4] = new List<Backend>();
					appDisplay[item4] = item4;
				}
				if (backends.TryGetValue(item3.Id, out var value) && !dictionary[item4].Contains(value))
				{
					dictionary[item4].Add(value);
				}
			}
		}
		foreach (string item5 in (from x in dictionary
			where x.Value.Count == 0
			select x.Key).ToList())
		{
			uncovered.Add(appDisplay[item5]);
			dictionary.Remove(item5);
		}
		if (dictionary.Count == 0)
		{
			throw new ConfigurationException((skipped.Count > 0) ? ("Нет рабочих маршрутов. " + string.Join("; ", skipped.Take(3))) : "Нет включённых профилей с приложениями.");
		}
		var list2 = (from g in dictionary.GroupBy((KeyValuePair<string, List<Backend>> x) => string.Join("|", x.Value.Select((Backend b) => b.Id).OrderBy((string id) => id, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)
			select new
			{
				Apps = g.Select((KeyValuePair<string, List<Backend>> x) => appDisplay[x.Key]).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList(),
				Candidates = g.First().Value.OrderBy((Backend b) => b.Priority).ToList()
			}).ToList();
		HashSet<string> keep = new HashSet<string>(list2.Select(g => string.Join("|", g.Apps.Select((string a) => a.ToLowerInvariant()))), StringComparer.Ordinal);
		foreach (string item6 in groups.Keys.Where((string k) => !keep.Contains(k)).ToList())
		{
			Group obj = groups[item6];
			groups.Remove(item6);
			lock (processGate)
			{
				reservedPorts.Remove(obj.Relay.Port);
			}
			obj.Relay.Dispose();
			AppendLog("GROUP_REMOVED apps=" + item6);
		}
		foreach (var item7 in list2)
		{
			string key = string.Join("|", item7.Apps.Select((string a) => a.ToLowerInvariant()));
			if (!groups.TryGetValue(key, out var value2))
			{
				Group obj2 = new Group();
				obj2.Key = key;
				value2 = obj2;
				int num = item7.Candidates.Select((Backend b) => b.Profile.SocksPort).FirstOrDefault((int p) => p >= 1025 && p <= 65535);
				value2.Relay = CreateRelay(value2, (num == 0) ? 10810 : num);
				groups.Add(key, value2);
				AppendLog("GROUP_CREATED apps=" + value2.Key + " port=" + value2.Relay.Port);
			}
			value2.Apps = item7.Apps;
			value2.Candidates = item7.Candidates;
			if (value2.Active != null && !value2.Candidates.Contains(value2.Active))
			{
				value2.Active = null;
			}
			value2.Provider = value2.Candidates.Select((Backend b) => b.Provider).FirstOrDefault((ProviderDefinition p) => p != null);
			RefreshTargets(value2);
		}
		UpdateActiveFlags();
		watcher.SetProviders(groups.Values.Select((Group g) => g.Provider));
		string text2 = string.Join(";", from g in groups.Values.OrderBy((Group g) => g.Key, StringComparer.Ordinal)
			select g.Key + "=" + g.Relay.Port);
		if (text2 != proxyMapping || proxyProcess == null)
		{
			if (proxyProcess != null)
			{
				AppendLog("PROXY_RECONFIGURE reason=application-mapping-changed activeConnections=" + groups.Values.Sum((Group g) => g.Relay.TotalConnections));
			}
			proxyMapping = text2;
			WriteProxiFyreConfig();
			StopProcess(proxyProcess);
			proxyProcess = null;
			nextProxyStartUtc = DateTime.MinValue;
			EnsureProxy(runGeneration, supervising: false);
		}
		UpdateStatus(runGeneration);
	}

	private RouteRelay CreateRelay(Group group, int preferred)
	{
		Exception ex = null;
		for (int i = 0; i < 60; i++)
		{
			int num = ((i < 50) ? (preferred + i) : 0);
			if (num > 65535)
			{
				num = 0;
			}
			lock (processGate)
			{
				if (num != 0 && reservedPorts.Contains(num))
				{
					continue;
				}
			}
			try
			{
				RouteRelay routeRelay = new RouteRelay(group.Key, num, () => group.Targets, OnRelayFailure, AppendLog);
				lock (processGate)
				{
					reservedPorts.Add(routeRelay.Port);
				}
				return routeRelay;
			}
			catch (SocketException ex2)
			{
				ex = ex2;
			}
		}
		throw new EngineFailureException("Не удалось открыть локальный вход маршрута: " + ((ex == null) ? "нет свободного порта" : "порт занят"));
	}

	private string Fingerprint(TunnelProfile profile)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(profile.ConnectionType).Append('\n').Append(profile.KeyKind)
			.Append('\n');
		stringBuilder.Append(profile.TelephonyOptimized).Append('\n');
		stringBuilder.Append(UsesMihomo(profile) ? config.MihomoPath : config.XrayPath).Append('\n');
		if (string.Equals(profile.ConnectionType, "key", StringComparison.OrdinalIgnoreCase))
		{
			string s = secrets.Load(profile.Id) ?? "";
			using SHA256 sHA = SHA256.Create();
			stringBuilder.Append(Convert.ToBase64String(sHA.ComputeHash(Encoding.UTF8.GetBytes(s))));
		}
		else
		{
			FileInfo fileInfo = new FileInfo(profile.XrayConfigPath);
			stringBuilder.Append(fileInfo.FullName).Append('\n').Append(fileInfo.Length)
				.Append('\n')
				.Append(fileInfo.LastWriteTimeUtc.Ticks);
		}
		return stringBuilder.ToString();
	}

	private void ResolveBackendProvider(Backend backend, bool force)
	{
		if (!force && DateTime.UtcNow - backend.ProviderCheckedUtc < TimeSpan.FromSeconds(30.0))
		{
			return;
		}
		TunnelProfile profile;
		lock (backend.Gate)
		{
			profile = backend.Profile;
		}
		backend.ProviderCheckedUtc = DateTime.UtcNow;
		ProviderDefinition provider;
		try
		{
			provider = providerCatalog.Resolve(profile, ProviderTrafficObserver.ReadHosts(store, profile));
		}
		catch
		{
			provider = null;
		}
		lock (backend.Gate)
		{
			backend.Provider = provider;
		}
	}

	private void StartBackend(Backend backend)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			BackendLoop(backend);
		});
		thread.IsBackground = true;
		thread.Name = "TRELAYBackend";
		Thread thread2 = thread;
		thread2.Start();
		AppendLog("BACKEND_ADDED name=" + SafeProfileName(backend.Name));
	}

	private void StopBackend(Backend backend, string reason)
	{
		backends.Remove(backend.Id);
		foreach (Group value in groups.Values)
		{
			if (value.Candidates.Contains(backend))
			{
				value.Candidates = value.Candidates.Where((Backend b) => b != backend).ToList();
				if (value.Active == backend)
				{
					value.Active = null;
				}
				RefreshTargets(value);
				value.Relay.Evacuate(backend.Target);
			}
		}
		backend.Life.Cancel();
		try
		{
			backend.ProbeSignal.Set();
		}
		catch (ObjectDisposedException)
		{
		}
		Process process;
		lock (backend.Gate)
		{
			process = backend.Process;
			backend.Process = null;
			backend.Health = Health.Down;
		}
		StopProcess(process);
		lock (processGate)
		{
			reservedPorts.Remove(backend.Target.Port);
		}
		AppendLog("BACKEND_STOPPED name=" + SafeProfileName(backend.Name) + " reason=" + reason);
	}

	private void BackendLoop(Backend backend)
	{
		int num = 0;
		CancellationToken token = backend.Life.Token;
		while (!token.IsCancellationRequested)
		{
			Process process = null;
			string text = null;
			DateTime utcNow = DateTime.UtcNow;
			try
			{
				SetHealth(backend, Health.Starting, (num == 0) ? "запуск" : "перезапуск", wakeEngine: false);
				TunnelProfile profile;
				lock (backend.Gate)
				{
					profile = backend.Profile;
				}
				int num2 = AllocateBackendPort(backend.Target.Port);
				backend.Target.Port = num2;
				EngineLaunch engineLaunch = BuildEngineLaunch(profile, num2);
				text = engineLaunch.ConfigPath;
				process = StartOwnedProcess(engineLaunch, backend);
				if (!WaitForPort(num2, process, 10000, token))
				{
					throw new EngineFailureException(ProcessAlive(process) ? "движок не открыл локальный порт за 10 с" : "движок завершился при запуске");
				}
				DeleteRuntimeFile(text);
				text = null;
				AppendLog("BACKEND_READY name=" + SafeProfileName(profile.Name) + " port=" + num2 + " pid=" + process.Id + " engine=" + Path.GetFileName(engineLaunch.Executable));
				HealthLoop(backend, process, token);
				if (token.IsCancellationRequested)
				{
					break;
				}
				SetHealth(backend, Health.Down, "процесс движка завершился", wakeEngine: true);
				AppendLog("BACKEND_EXITED name=" + SafeProfileName(profile.Name) + " pid=" + process.Id + " exitCode=" + process.ExitCode);
				goto IL_0237;
			}
			catch (OperationCanceledException)
			{
			}
			catch (ConfigurationException ex2)
			{
				SetHealth(backend, Health.Down, ex2.Message, wakeEngine: true);
				AppendLog("BACKEND_CONFIG_ERROR name=" + SafeProfileName(backend.Name) + " " + ex2.Message);
				token.WaitHandle.WaitOne();
			}
			catch (Exception error)
			{
				if (token.IsCancellationRequested)
				{
					break;
				}
				string text2 = SafeError(error);
				SetHealth(backend, Health.Down, text2, wakeEngine: true);
				AppendLog("BACKEND_FAILED name=" + SafeProfileName(backend.Name) + " " + text2);
				goto IL_0237;
			}
			finally
			{
				lock (backend.Gate)
				{
					if (backend.Process == process)
					{
						backend.Process = null;
					}
				}
				StopProcess(process);
				if (text != null)
				{
					DeleteRuntimeFile(text);
				}
			}
			break;
			IL_0237:
			if (DateTime.UtcNow - utcNow > TimeSpan.FromSeconds(60.0))
			{
				num = 0;
			}
			int num3 = ((num == 0) ? 500 : Math.Min(30000, 1000 * (1 << Math.Min(num - 1, 5))));
			num++;
			lock (backend.Gate)
			{
				backend.Detail = backend.Detail + " · повтор через " + Math.Max(1, num3 / 1000) + " с";
			}
			Wake();
			if (token.WaitHandle.WaitOne(num3))
			{
				break;
			}
		}
		lock (backend.Gate)
		{
			backend.Health = Health.Down;
		}
		try
		{
			backend.ProbeSignal.Dispose();
		}
		catch
		{
		}
	}

	private Process StartOwnedProcess(EngineLaunch launch, Backend backend)
	{
		lock (processGate)
		{
			if (!runtimeActive || backend.Life.IsCancellationRequested)
			{
				throw new OperationCanceledException();
			}
			Process process = StartHidden(launch.Executable, launch.Arguments, launch.WorkingDirectory);
			try
			{
				process.EnableRaisingEvents = true;
				process.Exited += delegate
				{
					try
					{
						backend.ProbeSignal.Set();
					}
					catch (ObjectDisposedException)
					{
					}
					Wake();
				};
			}
			catch
			{
			}
			lock (backend.Gate)
			{
				backend.Process = process;
			}
			return process;
		}
	}

	private void HealthLoop(Backend backend, Process process, CancellationToken token)
	{
		int num = 0;
		while (!token.IsCancellationRequested && ProcessAlive(process))
		{
			long appFailureTicks = Interlocked.Read(ref backend.TraceAppFailureTicks);
			string failure;
			bool flag = ProbeBackend(backend, num++, token, out failure);
			if (token.IsCancellationRequested || !ProcessAlive(process))
			{
				break;
			}
			if (!flag && backend.IsActive && backend.CurrentHealth == Health.Healthy)
			{
				int failures;
				lock (backend.Gate)
				{
					failures = backend.Failures;
				}
				if (failures == 1)
				{
					if (ProbeBackend(backend, num++, token, out var failure2, 4000))
					{
						flag = true;
						failure = "";
						AppendLog("PROBE_CONFIRMED_ALIVE name=" + SafeProfileName(backend.Name));
					}
					else if (!token.IsCancellationRequested)
					{
						AppendLog("PROBE_CONFIRMED_DOWN name=" + SafeProfileName(backend.Name) + " detail=" + failure2);
					}
				}
			}
			if (token.IsCancellationRequested || !ProcessAlive(process))
			{
				break;
			}
			if (appFailureTicks != 0 && Interlocked.CompareExchange(ref backend.TraceAppFailureTicks, 0, appFailureTicks) == appFailureTicks)
			{
				AppendLog("APP_FAILURE_PROBE name=" + SafeProfileName(backend.Name) + " ok=" + flag + " detail=" + (flag ? "работает" : failure));
			}
			RecordProbe(backend, flag, failure);
			Health health;
			int failures2;
			lock (backend.Gate)
			{
				health = backend.Health;
				failures2 = backend.Failures;
			}
			int num2 = ((failures2 > 0 && health == Health.Healthy) ? 1000 : (backend.IsActive ? 4000 : ((health == Health.Starting) ? 1000 : 5000)));
			if (backend.ProbesService && health != Health.Starting && num2 > 1000 && num2 < 12000)
			{
				num2 = 12000;
			}
			try
			{
				WaitHandle.WaitAny(new WaitHandle[2] { token.WaitHandle, backend.ProbeSignal }, num2);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
		}
	}

	private void RecordProbe(Backend backend, bool ok, string failure)
	{
		bool flag = false;
		Health health;
		lock (backend.Gate)
		{
			DateTime utcNow = DateTime.UtcNow;
			if (ok)
			{
				backend.Failures = 0;
				backend.FirstFailureUtc = DateTime.MinValue;
				backend.Successes++;
				backend.LastSuccessUtc = utcNow;
				if (backend.Health == Health.Starting || (backend.Health == Health.Unhealthy && backend.Successes >= 2))
				{
					backend.Health = Health.Healthy;
					backend.HealthySinceUtc = utcNow;
					flag = true;
				}
				if (backend.Health == Health.Healthy)
				{
					backend.Detail = "работает";
				}
			}
			else
			{
				backend.Successes = 0;
				if (backend.Failures == 0)
				{
					backend.FirstFailureUtc = utcNow;
				}
				backend.Failures++;
				backend.LastFailureUtc = utcNow;
				backend.Detail = (string.IsNullOrEmpty(failure) ? "нет ответа" : failure);
				if ((backend.Health == Health.Healthy && backend.Failures >= 2) || (backend.Health == Health.Starting && backend.Failures >= 3))
				{
					backend.Health = Health.Unhealthy;
					flag = true;
				}
			}
			health = backend.Health;
		}
		if (Environment.GetEnvironmentVariable("TRELAY_TRACE_PROBES") == "1")
		{
			AppendLog("PROBE name=" + SafeProfileName(backend.Name) + " ok=" + ok + " active=" + backend.IsActive + " " + failure);
		}
		if (flag)
		{
			AppendLog(string.Concat("HEALTH name=", SafeProfileName(backend.Name), " state=", health, ok ? "" : (" " + failure)));
			Wake();
		}
	}

	private void SetHealth(Backend backend, Health health, string detail, bool wakeEngine)
	{
		lock (backend.Gate)
		{
			backend.Health = health;
			backend.Detail = detail;
			backend.Failures = 0;
			backend.Successes = 0;
			backend.FirstFailureUtc = DateTime.MinValue;
			if (health == Health.Down || health == Health.Unhealthy)
			{
				backend.LastFailureUtc = DateTime.UtcNow;
			}
		}
		if (wakeEngine)
		{
			Wake();
		}
	}

	private void RequestProbe(Backend backend)
	{
		long ticks = DateTime.UtcNow.Ticks;
		long num = Interlocked.Read(ref backend.LastProbeRequestTicks);
		if (ticks - num < 10000000)
		{
			return;
		}
		Interlocked.Exchange(ref backend.LastProbeRequestTicks, ticks);
		try
		{
			backend.ProbeSignal.Set();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void OnRelayFailure(RelayTarget target, RelayFailureKind kind)
	{
		if (backendIndex.TryGetValue(target.Id, out var value) && value.Target == target)
		{
			RequestProbe(value);
			if (kind == RelayFailureKind.LocalConnect)
			{
				Wake();
			}
		}
	}

	private List<string> ProbeTargets(Backend backend)
	{
		TunnelProfile profile;
		ProviderDefinition provider;
		lock (backend.Gate)
		{
			profile = backend.Profile;
			provider = backend.Provider;
		}
		List<string> targets = new List<string>();
		Action<string> action = delegate(string value)
		{
			value = (value ?? "").Trim();
			if (value.Length != 0)
			{
				if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && value.IndexOf(':') < 0)
				{
					value += ":443";
				}
				if (!targets.Contains(value, StringComparer.OrdinalIgnoreCase))
				{
					targets.Add(value);
				}
			}
		};
		action(profile.HealthCheckEndpoint);
		if (provider != null)
		{
			foreach (string healthEndpoint in provider.HealthEndpoints)
			{
				action(healthEndpoint);
			}
			List<string> list = ((watcher == null) ? new List<string>() : watcher.GetEndpoints(provider.Id));
			RememberEndpoints(provider.Id, list);
			foreach (string item in list.Take(3))
			{
				action(item);
			}
			foreach (string item2 in RememberedEndpoints(provider.Id).Take(3))
			{
				action(item2);
			}
		}
		if (targets.Count == 0)
		{
			foreach (string item3 in RelayDestinations(backend).Take(3))
			{
				action(item3);
			}
		}
		backend.ProbesService = targets.Count > 0;
		if (targets.Count == 0)
		{
			targets.AddRange(SocksHealthProbe.InternetTargets);
		}
		if (targets.Count <= 4)
		{
			return targets;
		}
		return targets.Take(4).ToList();
	}

	private List<string> RememberedEndpoints(string providerId)
	{
		if (string.IsNullOrEmpty(providerId))
		{
			return new List<string>();
		}
		lock (endpointGate)
		{
			List<string> value;
			return knownEndpoints.TryGetValue(providerId, out value) ? new List<string>(value) : new List<string>();
		}
	}

	private void RememberEndpoints(string providerId, List<string> observed)
	{
		if (string.IsNullOrEmpty(providerId) || observed == null || observed.Count == 0)
		{
			return;
		}
		bool flag = false;
		lock (endpointGate)
		{
			if (!knownEndpoints.TryGetValue(providerId, out var value))
			{
				value = new List<string>();
				knownEndpoints[providerId] = value;
			}
			foreach (string item in observed.Take(3))
			{
				if (!string.IsNullOrEmpty(item) && !value.Contains(item, StringComparer.OrdinalIgnoreCase))
				{
					value.Insert(0, item);
					flag = true;
				}
			}
			while (value.Count > 4)
			{
				value.RemoveAt(value.Count - 1);
				flag = true;
			}
		}
		if (flag)
		{
			SaveKnownEndpoints();
		}
	}

	private string EndpointsPath()
	{
		return Path.Combine(store.StateDirectory, "endpoints.json");
	}

	private void LoadKnownEndpoints()
	{
		try
		{
			string path = EndpointsPath();
			if (!File.Exists(path))
			{
				return;
			}
			JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
			javaScriptSerializer.MaxJsonLength = 1048576;
			if (!(javaScriptSerializer.DeserializeObject(File.ReadAllText(path)) is Dictionary<string, object> dictionary))
			{
				return;
			}
			lock (endpointGate)
			{
				foreach (KeyValuePair<string, object> item in dictionary)
				{
					if (!(item.Value is IEnumerable enumerable) || string.IsNullOrWhiteSpace(item.Key))
					{
						continue;
					}
					List<string> list = new List<string>();
					foreach (object item2 in enumerable)
					{
						string text = ((item2 == null) ? "" : item2.ToString().Trim());
						if (text.Length != 0 && text.Length <= 253 && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
						{
							list.Add(text);
							if (list.Count >= 4)
							{
								break;
							}
						}
					}
					if (list.Count > 0)
					{
						knownEndpoints[item.Key] = list;
					}
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private void SaveKnownEndpoints()
	{
		try
		{
			Dictionary<string, List<string>> obj;
			lock (endpointGate)
			{
				obj = knownEndpoints.ToDictionary((KeyValuePair<string, List<string>> pair) => pair.Key, (KeyValuePair<string, List<string>> pair) => new List<string>(pair.Value), StringComparer.OrdinalIgnoreCase);
			}
			File.WriteAllText(EndpointsPath(), new JavaScriptSerializer
			{
				MaxJsonLength = 1048576
			}.Serialize(obj));
		}
		catch (Exception)
		{
		}
	}

	private List<string> RelayDestinations(Backend backend)
	{
		List<string> list = new List<string>();
		try
		{
			Dictionary<string, RouteRelay> dictionary = relayByBackend;
			if (dictionary == null || !dictionary.TryGetValue(backend.Id, out var value) || value == null)
			{
				return list;
			}
			foreach (string item in value.RecentDestinations())
			{
				if (IsUsableProbeDestination(item))
				{
					list.Add(item);
				}
			}
			return list;
		}
		catch (Exception)
		{
			return new List<string>();
		}
	}

	private static bool IsUsableProbeDestination(string destination)
	{
		if (string.IsNullOrEmpty(destination))
		{
			return false;
		}
		int num = destination.LastIndexOf(':');
		if (num <= 0 || num == destination.Length - 1)
		{
			return false;
		}
		if (!int.TryParse(destination.Substring(num + 1), out var result) || (result != 443 && result != 80))
		{
			return false;
		}
		string text = destination.Substring(0, num).Trim('[', ']');
		if (text.Length == 0 || text.Length > 253)
		{
			return false;
		}
		if (!IPAddress.TryParse(text, out var address))
		{
			return text.IndexOf('.') > 0;
		}
		if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
		{
			return false;
		}
		byte[] addressBytes = address.GetAddressBytes();
		if (addressBytes[0] == 0 || addressBytes[0] == 10 || addressBytes[0] >= 224)
		{
			return false;
		}
		if (addressBytes[0] == 172 && addressBytes[1] >= 16 && addressBytes[1] <= 31)
		{
			return false;
		}
		if (addressBytes[0] == 192 && addressBytes[1] == 168)
		{
			return false;
		}
		if (addressBytes[0] == 169 && addressBytes[1] == 254)
		{
			return false;
		}
		if (addressBytes[0] == 100 && addressBytes[1] >= 64 && addressBytes[1] <= 127)
		{
			return false;
		}
		return true;
	}

	private bool ProbeBackend(Backend backend, int cycle, CancellationToken token, out string failure, int timeoutOverrideMs = 0)
	{
		List<string> list = ProbeTargets(backend);
		int timeout = timeoutOverrideMs > 0 ? timeoutOverrideMs : (backend.IsActive ? 6000 : 8000);
		bool serviceOk = ProbeEndpoints(backend.Target.Port, list, cycle, timeout, token, out failure);
		if (token.IsCancellationRequested) return false;
		bool serviceFailed = backend.ProbesService && !serviceOk;
		bool changed;
		lock (backend.Gate)
		{
			changed = backend.ServiceCheckFailed != serviceFailed;
			backend.ServiceCheckFailed = serviceFailed;
			backend.ServiceFailures = serviceFailed ? backend.ServiceFailures + 1 : 0;
			if (serviceFailed) backend.LastServiceFailureUtc = DateTime.UtcNow;
		}
		if (changed)
		{
			AppendLog((serviceFailed ? "SERVICE_CHECK_FAILED" : "SERVICE_CHECK_RESTORED") + " name=" + SafeProfileName(backend.Name) + (serviceFailed ? " detail=" + failure : ""));
			Wake();
		}
		if (serviceOk || !backend.ProbesService) return serviceOk;
		// A failed destination check does not prove the VPN itself is down.
		string serviceFailure = failure;
		bool tunnelOk = ProbeEndpoints(backend.Target.Port, SocksHealthProbe.InternetTargets.ToList(), cycle, 4000, token, out string tunnelFailure);
		failure = tunnelOk ? "" : "сервис: " + serviceFailure + "; контроль туннеля: " + tunnelFailure;
		return tunnelOk;
	}

	private static bool ProbeEndpoints(int port, List<string> list, int cycle, int timeout, CancellationToken token, out string failure)
	{
		List<string> list2 = new List<string>();
		for (int i = 0; i < Math.Min(2, list.Count); i++)
		{
			list2.Add(list[(cycle + i) % list.Count]);
		}
		failure = "нет ответа";
		if (list2.Count == 1)
		{
			try
			{
				return SocksHealthProbe.ProbeService(port, list2[0], timeout, token, out failure);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
		}
		List<Task<string>> collection = list2.Select((string target) => Task.Factory.StartNew(delegate
		{
			try
			{
				string failure2;
				return SocksHealthProbe.ProbeService(port, target, timeout, token, out failure2) ? "" : (failure2 ?? "нет ответа");
			}
			catch (OperationCanceledException)
			{
				return "отменено";
			}
			catch (Exception ex3)
			{
				return ex3.GetType().Name;
			}
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();
		List<Task<string>> list3 = new List<Task<string>>(collection);
		string text = null;
		while (list3.Count > 0)
		{
			int index = Task.WaitAny(list3.Cast<Task>().ToArray());
			Task<string> task = list3[index];
			list3.RemoveAt(index);
			if (task.Result.Length == 0)
			{
				failure = "";
				return true;
			}
			if (text == null)
			{
				text = task.Result;
			}
		}
		failure = text ?? "нет ответа";
		return false;
	}

	private int AllocateBackendPort(int previous)
	{
		lock (processGate)
		{
			if (previous > 0 && CanBindPort(previous))
			{
				return previous;
			}
			reservedPorts.Remove(previous);
			for (int i = 0; i < 64; i++)
			{
				int port;
				using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
				{
					socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
					port = ((IPEndPoint)socket.LocalEndPoint).Port;
				}
				if (!reservedPorts.Contains(port) && CanBindPort(port))
				{
					reservedPorts.Add(port);
					return port;
				}
			}
		}
		throw new EngineFailureException("Не нашёлся свободный внутренний порт.");
	}

	private void Supervise(int runGeneration)
	{
		EnsureProxy(runGeneration, supervising: true);
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow >= nextProviderRefreshUtc)
		{
			nextProviderRefreshUtc = utcNow.AddSeconds(30.0);
			bool flag = false;
			foreach (Backend item in backends.Values.Where((Backend b) => b.Provider == null))
			{
				ResolveBackendProvider(item, force: false);
				flag |= item.Provider != null;
			}
			if (flag)
			{
				foreach (Group value in groups.Values)
				{
					value.Provider = value.Candidates.Select((Backend b) => b.Provider).FirstOrDefault((ProviderDefinition p) => p != null);
				}
				watcher.SetProviders(groups.Values.Select((Group g) => g.Provider));
			}
		}
		foreach (Group value2 in groups.Values)
		{
			Select(value2);
		}
		UpdateActiveFlags();
		CheckAppAlerts();
		UpdateStatus(runGeneration);
	}

	private static bool IsAlive(Backend backend)
	{
		lock (backend.Gate)
		{
			return backend.Health != Health.Down && backend.Process != null && ProcessAlive(backend.Process);
		}
	}

	private void Select(Group group)
	{
		DateTime now = DateTime.UtcNow;
		List<Backend> candidates = group.Candidates;
		Backend active = group.Active;
		if (active != null && !candidates.Contains(active))
		{
			active = (group.Active = null);
		}
		DateTime failureStarted = ((active == null) ? DateTime.MinValue : FirstFailure(active));
		Backend backend = candidates.Where((Backend b) => IsFreshHealthy(b, now) && (active == null || active.CurrentHealth != Health.Unhealthy || b == active || CheckedSince(b, failureStarted))).OrderBy(b => b.ServiceCheckFailed).FirstOrDefault();
		if (active != null && HasPendingFailure(active))
		{
			foreach (Backend item in candidates.Where((Backend b) => b != active && IsAlive(b)))
			{
				RequestProbe(item);
			}
		}
		if (active == null)
		{
			if (backend != null)
			{
				Activate(group, backend, "initial");
			}
			else if (now - group.CreatedUtc > StartupGrace)
			{
				Backend backend2 = candidates.FirstOrDefault(IsAlive);
				if (backend2 != null)
				{
					Activate(group, backend2, "degraded");
				}
			}
		}
		else
		{
			Health currentHealth = active.CurrentHealth;
			if (currentHealth == Health.Down || currentHealth == Health.Unhealthy || (currentHealth == Health.Starting && !IsAlive(active)))
			{
				if (backend != null && backend != active)
				{
					Failover(group, active, backend);
				}
				else if (currentHealth == Health.Down)
				{
					Backend backend3 = candidates.FirstOrDefault((Backend b) => b != active && IsAlive(b));
					if (backend3 != null)
					{
						Failover(group, active, backend3);
					}
				}
			}
			else if (currentHealth == Health.Healthy && active.ServiceCheckFailed && active.ServiceFailures >= 2 && group.Provider != null && watcher != null && watcher.GetState(group.Provider.Id, out var _, out var _) == ProviderConnectionState.Disconnected && backend != null && backend != active && !backend.ServiceCheckFailed)
			{
				Failover(group, active, backend);
			}
			else if (currentHealth == Health.Healthy && !HasPendingFailure(active) && backend != null && backend != active && backend.Priority < active.Priority && now - group.LastFailoverUtc >= RecoveryHold && ReadyForRecovery(backend, now))
			{
				Recover(group, active, backend, NeverFailed(backend));
			}
		}
		RefreshTargets(group);
		Backend active2 = group.Active;
		if (active2 != null && active2.CurrentHealth == Health.Healthy)
		{
			foreach (Backend item2 in candidates)
			{
				if (item2 != active2)
				{
					Health currentHealth2 = item2.CurrentHealth;
					if ((currentHealth2 == Health.Down || currentHealth2 == Health.Unhealthy) && group.Relay.ConnectionCount(item2.Target) > 0 && BreaksConnections(group, item2))
					{
						int num = group.Relay.Evacuate(item2.Target);
						AppendLog("EVACUATED name=" + SafeProfileName(item2.Name) + " connections=" + num);
					}
				}
			}
		}
		bool flag = (active2 != null && active2.CurrentHealth == Health.Healthy) || HasApplicationConnection(group);
		if (flag)
		{
			group.LostSinceUtc = DateTime.MinValue;
			if (group.LostAlerted)
			{
				group.LostAlerted = false;
				RaiseAlert(EngineAlertKind.RouteRestored, critical: false, "TRELAY: связь восстановлена", group.Label + ": работает через «" + SafeProfileName(active2.Name) + "».");
			}
		}
		else if (!flag && !group.LostAlerted && now - group.CreatedUtc > StartupGrace && (active2 == null || active2.CurrentHealth != Health.Starting) && (active2 != null || !(now - group.CreatedUtc < TimeSpan.FromSeconds(30.0)) || !candidates.Any((Backend b) => b.CurrentHealth == Health.Starting)))
		{
			if (group.LostSinceUtc == DateTime.MinValue)
			{
				group.LostSinceUtc = now;
			}
			if (now - group.LostSinceUtc >= RouteLossAlertHold)
			{
				group.LostAlerted = true;
				string text = ((active2 == null) ? "ни один сервер не запустился" : ("«" + SafeProfileName(active2.Name) + "»: " + BackendDetail(active2)));
				RaiseAlert(EngineAlertKind.RouteLost, critical: true, "TRELAY: нет рабочего маршрута", group.Label + ": " + text + ". Проверяю резервы.");
			}
		}
	}

	private static bool HasPendingFailure(Backend backend)
	{
		lock (backend.Gate)
		{
			return backend.Health == Health.Unhealthy || (backend.Health == Health.Healthy && backend.Failures > 0);
		}
	}

	private static DateTime FirstFailure(Backend backend)
	{
		lock (backend.Gate)
		{
			return backend.FirstFailureUtc;
		}
	}

	private bool HasApplicationConnection(Group group)
	{
		return proxyProcess != null && ProcessAlive(proxyProcess) && group.Provider != null && watcher != null && watcher.GetState(group.Provider.Id, out var _, out var _) == ProviderConnectionState.Connected && group.Candidates.Any(b => IsAlive(b) && group.Relay != null && group.Relay.ConnectionCount(b.Target) > 0);
	}

	private static bool CheckedSince(Backend backend, DateTime since)
	{
		lock (backend.Gate)
		{
			return since == DateTime.MinValue || backend.LastSuccessUtc >= since;
		}
	}

	private static bool IsFreshHealthy(Backend backend, DateTime now)
	{
		lock (backend.Gate)
		{
			return backend.Health == Health.Healthy && backend.Failures == 0 && now - backend.LastSuccessUtc <= FreshBackupWindow;
		}
	}

	private static bool ReadyForRecovery(Backend backend, DateTime now)
	{
		lock (backend.Gate)
		{
			if (backend.Health != Health.Healthy || backend.ServiceCheckFailed || (backend.LastServiceFailureUtc != DateTime.MinValue && now - backend.LastServiceFailureUtc < RecoveryHold))
			{
				return false;
			}
			if (backend.LastFailureUtc == DateTime.MinValue)
			{
				return true;
			}
			return backend.Successes >= 2 && now - backend.HealthySinceUtc >= RecoveryHold && now - backend.LastFailureUtc >= RecoveryHold;
		}
	}

	private static string BackendDetail(Backend backend)
	{
		lock (backend.Gate)
		{
			return backend.Detail;
		}
	}

	private void Activate(Group group, Backend backend, string reason)
	{
		group.Active = backend;
		RefreshTargets(group);
		backend.IsActive = true;
		RequestProbe(backend);
		AppendLog("ACTIVATED apps=" + group.Key + " name=" + SafeProfileName(backend.Name) + " reason=" + reason);
	}

	private void Failover(Group group, Backend from, Backend to)
	{
		string text = BackendDetail(from);
		int num = text.IndexOf(" · повтор", StringComparison.Ordinal);
		if (num > 0)
		{
			text = text.Substring(0, num);
		}
		group.Active = to;
		RefreshTargets(group);
		to.IsActive = true;
		RequestProbe(to);
		bool flag = BreaksConnections(group, from);
		int num2 = ((!flag) ? group.Relay.ConnectionCount(from.Target) : 0);
		int num3 = (flag ? group.Relay.Evacuate(from.Target) : 0);
		DateTime value = (group.LastFailoverUtc = DateTime.UtcNow);
		if (group.Provider != null)
		{
			lock (providerFailoverUtc)
			{
				providerFailoverUtc[group.Provider.Id] = value;
			}
		}
		AppendLog("FAILOVER apps=" + group.Key + " from=" + SafeProfileName(from.Name) + " to=" + SafeProfileName(to.Name) + " moved=" + num3 + ((num2 > 0) ? (" kept=" + num2) : "") + " reason=" + text);
		NotifyRouteSwitched(new RouteSwitchInfo
		{
			Application = group.Label,
			FromProfile = from.Name,
			ToProfile = to.Name,
			IsRecovery = false
		});
		string text2 = group.Label + ": «" + SafeProfileName(from.Name) + "» — " + text + ". Теперь через «" + SafeProfileName(to.Name) + "».";
		if (num3 > 0)
		{
			text2 += " Соединения приложения перезапущены — при необходимости перезайдите.";
		}
		RaiseAlert(EngineAlertKind.Failover, num3 > 0, "TRELAY переключил сервер", text2);
	}

	private static bool NeverFailed(Backend backend)
	{
		lock (backend.Gate)
		{
			return backend.LastFailureUtc == DateTime.MinValue && backend.LastServiceFailureUtc == DateTime.MinValue;
		}
	}

	private bool BreaksConnections(Group group, Backend backend)
	{
		if (group.Relay == null)
		{
			return false;
		}
		if (backend.CurrentHealth == Health.Down || !IsAlive(backend))
		{
			return true;
		}
		// A silent connection can be healthy (for example an idle WebSocket).
		// Probe timeouts only redirect new connections; the live backend and app
		// own the lifetime of established connections.
		return false;
	}

	private void Recover(Group group, Backend from, Backend to, bool quiet)
	{
		group.Active = to;
		RefreshTargets(group);
		to.IsActive = true;
		RequestProbe(to);
		AppendLog("RECOVERY apps=" + group.Key + " from=" + SafeProfileName(from.Name) + " to=" + SafeProfileName(to.Name) + (quiet ? " (priority order at startup)" : ""));
		if (!quiet)
		{
			NotifyRouteSwitched(new RouteSwitchInfo
			{
				Application = group.Label,
				FromProfile = from.Name,
				ToProfile = to.Name,
				IsRecovery = true
			});
			RaiseAlert(EngineAlertKind.Recovery, critical: false, "TRELAY вернул основной сервер", group.Label + ": новые соединения снова идут через «" + SafeProfileName(to.Name) + "». Текущие соединения не прерывались.");
		}
	}

	private void RefreshTargets(Group group)
	{
		Backend active = group.Active;
		if (active == null)
		{
			group.Targets = new RelayTarget[0];
			return;
		}
		List<RelayTarget> list = new List<RelayTarget>();
		list.Add(active.Target);
		List<RelayTarget> list2 = list;
		list2.AddRange(from b in @group.Candidates
			where b != active && b.CurrentHealth == Health.Healthy
			select b.Target);
		list2.AddRange(from b in @group.Candidates
			where b != active && b.CurrentHealth != Health.Healthy && IsAlive(b)
			select b.Target);
		group.Targets = list2.ToArray();
	}

	private void UpdateActiveFlags()
	{
		Dictionary<string, RouteRelay> dictionary = new Dictionary<string, RouteRelay>(StringComparer.OrdinalIgnoreCase);
		foreach (Group value in groups.Values)
		{
			if (value.Relay == null)
			{
				continue;
			}
			foreach (Backend candidate in value.Candidates)
			{
				if (!dictionary.ContainsKey(candidate.Id))
				{
					dictionary[candidate.Id] = value.Relay;
				}
			}
		}
		relayByBackend = dictionary;
		HashSet<Backend> hashSet = new HashSet<Backend>(from g in groups.Values
			where g.Active != null
			select g.Active);
		foreach (Backend value2 in backends.Values)
		{
			bool flag = hashSet.Contains(value2);
			if (flag != value2.IsActive)
			{
				value2.IsActive = flag;
				if (flag)
				{
					RequestProbe(value2);
				}
			}
		}
	}

	private void EnsureProxy(int runGeneration, bool supervising)
	{
		if (proxyProcess != null && ProcessAlive(proxyProcess))
		{
			return;
		}
		if (proxyProcess != null)
		{
			StopProcess(proxyProcess);
			proxyProcess = null;
			DateTime now = DateTime.UtcNow;
			proxyCrashes.Add(now);
			proxyCrashes.RemoveAll((DateTime x) => now - x > TimeSpan.FromMinutes(2.0));
			int num = ((proxyCrashes.Count > 1) ? Math.Min(30, 2 * (1 << Math.Min(proxyCrashes.Count - 2, 4))) : 0);
			nextProxyStartUtc = now.AddSeconds(num);
			AppendLog("PROXY_EXITED restarts=" + proxyCrashes.Count + " delay=" + num);
			if (supervising && now - lastRouterAlertUtc > TimeSpan.FromSeconds(60.0))
			{
				lastRouterAlertUtc = now;
				RaiseAlert(EngineAlertKind.RouterRestarted, critical: true, "TRELAY: маршрутизатор перезапускается", "Процесс перехвата приложений завершился — текущие соединения прерваны. Перезайдите в приложение после восстановления.");
			}
		}
		if (DateTime.UtcNow < nextProxyStartUtc)
		{
			return;
		}
		try
		{
			lock (processGate)
			{
				if (!runtimeActive)
				{
					return;
				}
				proxyProcess = StartHidden(config.ProxiFyrePath, "--allow-not-admin", Path.GetDirectoryName(config.ProxiFyrePath));
			}
			Interlocked.Exchange(ref proxyStartedTicks, DateTime.UtcNow.Ticks);
			AppendLog("PROXY_STARTED");
		}
		catch (Exception error)
		{
			proxyProcess = null;
			nextProxyStartUtc = DateTime.UtcNow.AddSeconds(5.0);
			AppendLog("PROXY_START_FAILED " + SafeError(error));
			SetState(runGeneration, EngineState.Error, "Не удалось запустить маршрутизатор приложений: " + SafeError(error) + " Повтор через 5 с.");
		}
	}

	private void OnAppStateChanged(ProviderDefinition provider, ProviderConnectionState connection, string detail)
	{
		AppendLog("APP_" + connection.ToString().ToUpperInvariant() + " provider=" + provider.Id + " " + detail);
		switch (connection)
		{
		case ProviderConnectionState.Disconnected:
			foreach (Backend value in backendIndex.Values)
			{
				ProviderDefinition provider2;
				lock (value.Gate)
				{
					provider2 = value.Provider;
				}
				if (value.IsActive && provider2 != null && provider2.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
				{
					Interlocked.Exchange(ref value.TraceAppFailureTicks, DateTime.UtcNow.Ticks);
					RequestProbe(value);
				}
			}
			break;
		case ProviderConnectionState.Connected:
		{
			bool flag;
			lock (appAlerted)
			{
				flag = appAlerted.Remove(provider.Id);
			}
			if (flag)
			{
				RaiseAlert(EngineAlertKind.AppReconnected, critical: false, provider.DisplayName + ": связь восстановлена", provider.DisplayName + " снова подключён.");
			}
			break;
		}
		}
		Wake();
	}

	private bool TryRaiseAppDisconnected(ProviderDefinition provider, string detail, bool delayed)
	{
		DateTime utcNow = DateTime.UtcNow;
		DateTime dateTime = new DateTime(Interlocked.Read(ref proxyStartedTicks), DateTimeKind.Utc);
		if (utcNow - dateTime < AppAlertQuietAfterRouterStart)
		{
			return false;
		}
		lock (providerFailoverUtc)
		{
			if (providerFailoverUtc.TryGetValue(provider.Id, out var value) && utcNow - value < AppAlertQuietAfterFailover)
			{
				return false;
			}
		}
		lock (appAlerted)
		{
			if (appAlerted.ContainsKey(provider.Id))
			{
				return false;
			}
			appAlerted[provider.Id] = utcNow;
		}
		RaiseAlert(EngineAlertKind.AppDisconnected, critical: true, provider.DisplayName + ": обрыв связи", provider.DisplayName + " потерял соединение (" + detail + "). Автоматическое переподключение пока не завершилось.");
		return true;
	}

	private void CheckAppAlerts()
	{
		if (watcher == null)
		{
			return;
		}
		foreach (ProviderDefinition item in (from g in groups.Values
			select g.Provider into p
			where p != null
			select p).Distinct())
		{
			string detail;
			DateTime changedUtc;
			ProviderConnectionState providerConnectionState = watcher.GetState(item.Id, out detail, out changedUtc);
			if (providerConnectionState == ProviderConnectionState.Disconnected && !(changedUtc == DateTime.MinValue) && !(DateTime.UtcNow - changedUtc < TimeSpan.FromSeconds(30.0)))
			{
				DateTime dateTime = new DateTime(Interlocked.Read(ref proxyStartedTicks), DateTimeKind.Utc);
				if (!(changedUtc < dateTime) || !(DateTime.UtcNow - dateTime < AppAlertQuietAfterRouterStart))
				{
					TryRaiseAppDisconnected(item, detail, delayed: true);
				}
			}
		}
	}

	private void RaiseAlert(EngineAlertKind kind, bool critical, string title, string message)
	{
		AppendLog(string.Concat("ALERT ", kind, critical ? " critical " : " ", message));
		Action<EngineAlert> alert = this.Alert;
		if (alert == null)
		{
			return;
		}
		EngineAlert engineAlert = new EngineAlert();
		engineAlert.Kind = kind;
		engineAlert.Critical = critical;
		engineAlert.Title = title;
		engineAlert.Message = message;
		EngineAlert obj = engineAlert;
		Delegate[] invocationList = alert.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<EngineAlert> action = (Action<EngineAlert>)invocationList[i];
			try
			{
				action(obj);
			}
			catch (Exception ex)
			{
				AppendLog("Alert subscriber failed: " + ex.GetType().Name);
			}
		}
	}

	private void UpdateStatus(int runGeneration)
	{
		Dictionary<string, ProfileRuntimeInfo> dictionary = new Dictionary<string, ProfileRuntimeInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, string> skippedProfile in skippedProfiles)
		{
			dictionary[skippedProfile.Key] = new ProfileRuntimeInfo
			{
				Status = ProfileRuntimeStatus.Skipped,
				Detail = skippedProfile.Value
			};
		}
		foreach (Backend value in backends.Values)
		{
			Health health;
			string detail;
			lock (value.Gate)
			{
				health = value.Health;
				detail = value.Detail;
			}
			ProfileRuntimeStatus status = health switch
			{
				Health.Starting => ProfileRuntimeStatus.Starting, 
				Health.Healthy => value.ServiceCheckFailed ? ProfileRuntimeStatus.ServiceUnverified : (value.IsActive ? ProfileRuntimeStatus.Active : ProfileRuntimeStatus.Standby),
				Health.Unhealthy => ProfileRuntimeStatus.Unhealthy, 
				_ => ProfileRuntimeStatus.Down, 
			};
			dictionary[value.Id] = new ProfileRuntimeInfo
			{
				Status = status,
				Detail = value.ServiceCheckFailed && health == Health.Healthy ? "Туннель отвечает; проверка сервиса не прошла" : detail
			};
		}
		DateTime utcNow = DateTime.UtcNow;
		List<string> list = new List<string>();
		bool flag = false;
		bool flag2 = false;
		bool degraded = false;
		foreach (Group item in groups.Values.OrderBy((Group g) => (g.Candidates.Count != 0) ? g.Candidates.Min((Backend b) => b.Priority) : int.MaxValue))
		{
			Backend active = item.Active;
			int num = item.Candidates.Count((Backend b) => b != active && b.CurrentHealth == Health.Healthy && !b.ServiceCheckFailed);
			int num2 = item.Candidates.Count((Backend b) => b != active && b.CurrentHealth == Health.Starting);
			if (active == null)
			{
				if (utcNow - item.CreatedUtc <= StartupGrace || item.Candidates.Any((Backend b) => b.CurrentHealth == Health.Starting))
				{
					flag = true;
					list.Add(item.Label + ": проверяю серверов — " + item.Candidates.Count);
				}
				else
				{
					flag2 = true;
					list.Add(item.Label + ": ни один сервер не отвечает");
				}
				continue;
			}
			switch (active.CurrentHealth)
			{
			case Health.Healthy:
			{
				if (active.ServiceCheckFailed)
				{
					degraded = true;
					list.Add(item.Label + (HasApplicationConnection(item) ? " подключён" : ": подключение не подтверждено") + " через «" + SafeProfileName(active.Name) + "»; туннель отвечает, проверка сервиса не прошла");
					continue;
				}
				string text = ((num > 0) ? ("резерв готов: " + num) : ((num2 > 0) ? "резервы проверяются" : ((item.Candidates.Count > 1) ? "резервы недоступны" : "без резерва")));
				list.Add(item.Label + " → «" + SafeProfileName(active.Name) + "», " + text);
				continue;
			}
			case Health.Starting:
				if (utcNow - item.CreatedUtc <= TimeSpan.FromSeconds(30.0))
				{
					flag = true;
					list.Add(item.Label + " → «" + SafeProfileName(active.Name) + "»: проверяю");
					continue;
				}
				break;
			}
			if (HasApplicationConnection(item))
			{
				degraded = true;
				list.Add(item.Label + " подключён; контрольные проверки не проходят — проверяю маршрут");
				continue;
			}
			flag2 = true;
			list.Add(item.Label + ": «" + SafeProfileName(active.Name) + "» не отвечает (" + BackendDetail(active) + ")" + ((num == 0) ? ", рабочих резервов нет" : ""));
		}
		if (uncovered.Count > 0)
		{
			flag2 = true;
			list.Add("нет рабочего маршрута для " + string.Join(", ", uncovered));
		}
		if (skipped.Count > 0)
		{
			list.Add("пропущено: " + string.Join("; ", skipped.Take(2)) + ((skipped.Count > 2) ? (" и ещё " + (skipped.Count - 2)) : ""));
		}
		if (proxyProcess == null)
		{
			flag2 = true;
			list.Insert(0, (proxyCrashes.Count >= 3) ? "ProxiFyre завершается сразу после запуска — проверьте системный компонент маршрутизации и журнал engine\\logs" : "маршрутизатор приложений перезапускается");
		}
		EngineState newState;
		if (flag2)
		{
			newState = EngineState.Error;
		}
		else if (flag)
		{
			newState = EngineState.Starting;
		}
		else if (degraded)
		{
			newState = EngineState.Degraded;
		}
		else
		{
			List<ProviderDefinition> list2 = (from g in groups.Values
				select g.Provider into p
				where p != null
				select p).Distinct().ToList();
			newState = ((list2.Count > 0 && list2.All((ProviderDefinition p) => watcher != null && watcher.GetState(p.Id, out var _, out var _) == ProviderConnectionState.Connected) && list2.Count == groups.Count) ? EngineState.On : EngineState.TunnelReady);
		}
		string text2 = ((list.Count == 0) ? "Маршрутизация работает" : string.Join(" · ", list));
		if (text2.Length > 0)
		{
			text2 = char.ToUpperInvariant(text2[0]) + text2.Substring(1);
		}
		lock (sync)
		{
			runtimeInfo = dictionary;
		}
		string text3 = string.Join("|", from x in dictionary
			orderby x.Key
			select x.Key + ":" + x.Value.Status);
		bool flag3 = text3 != runtimeSignature;
		runtimeSignature = text3;
		if (!SetState(runGeneration, newState, text2) && flag3)
		{
			NotifyStateChanged();
		}
	}

	private void ValidateConfig(bool initial)
	{
		if (config == null || config.Profiles == null || config.Profiles.Any((TunnelProfile p) => p == null))
		{
			throw new ConfigurationException("Некорректный список профилей.");
		}
		if (!File.Exists(config.ProxiFyrePath))
		{
			throw new ConfigurationException("Не найден ProxiFyre.exe. Нужна отдельная копия движка для TRELAY.");
		}
		if (initial && HasForeignProxiFyre(config.ProxiFyrePath))
		{
			throw new EngineFailureException("Уже работает ProxiFyre, запущенный другим экземпляром или VPN-клиентом.");
		}
		List<TunnelProfile> list = ActiveProfiles();
		if (list.Count == 0)
		{
			throw new ConfigurationException("Нет включённых профилей с приложениями.");
		}
		if (list.Any(UsesMihomo) && !File.Exists(config.MihomoPath))
		{
			throw new ConfigurationException("Не найден встроенный mihomo.exe. Переустанови полную сборку TRELAY.");
		}
		if (list.Any((TunnelProfile p) => !UsesMihomo(p)) && !File.Exists(config.XrayPath))
		{
			throw new ConfigurationException("Не найден xray.exe. Укажи путь в настройках.");
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (TunnelProfile item in list)
		{
			if (!SecretStore.IsValidProfileId(item.Id) || !hashSet.Add(item.Id))
			{
				throw new ConfigurationException("Некорректный или повторяющийся идентификатор профиля.");
			}
		}
	}

	private void ValidateProfile(TunnelProfile profile)
	{
		if (string.IsNullOrWhiteSpace(profile.Name))
		{
			throw new ConfigurationException("у маршрута нет названия");
		}
		if (!SocksHealthProbe.ValidateEndpoint(profile.HealthCheckEndpoint, out var message))
		{
			throw new ConfigurationException(message);
		}
		foreach (string item in (from a in profile.AppNames.Select(NormalizeProcessName)
			where a.Length > 0
			select a).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (item.IndexOfAny(new char[9] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
			{
				throw new ConfigurationException("укажи имя приложения без пути и масок");
			}
		}
		if (string.Equals(profile.ConnectionType, "key", StringComparison.OrdinalIgnoreCase))
		{
			if (!secrets.Exists(profile.Id))
			{
				throw new ConfigurationException("не найден сохранённый ключ");
			}
		}
		else if (!File.Exists(profile.XrayConfigPath))
		{
			throw new ConfigurationException("не найден файл конфигурации");
		}
	}

	private string BuildRuntimeXrayConfig(TunnelProfile profile, int port)
	{
		Dictionary<string, object> dictionary;
		try
		{
			if (string.Equals(profile.ConnectionType, "key", StringComparison.OrdinalIgnoreCase))
			{
				string text = secrets.Load(profile.Id);
				if (string.IsNullOrWhiteSpace(text))
				{
					throw new InvalidDataException();
				}
				dictionary = ShareLinkConverter.Build(text, port);
			}
			else
			{
				if (new FileInfo(profile.XrayConfigPath).Length > 8388608)
				{
					throw new InvalidDataException();
				}
				lock (json)
				{
					dictionary = json.DeserializeObject(File.ReadAllText(profile.XrayConfigPath)) as Dictionary<string, object>;
				}
			}
			if (dictionary == null)
			{
				throw new InvalidDataException();
			}
			if (!dictionary.TryGetValue("inbounds", out var value))
			{
				throw new InvalidDataException();
			}
			if (!(value is object[] array) || array.Length == 0)
			{
				throw new InvalidDataException();
			}
			if (!(array[0] is Dictionary<string, object> dictionary2))
			{
				throw new InvalidDataException();
			}
			Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
			dictionary3.Add("listen", "127.0.0.1");
			dictionary3.Add("port", port);
			dictionary3.Add("protocol", "socks");
			dictionary3.Add("settings", new Dictionary<string, object>
			{
				{ "udp", true },
				{ "ip", "127.0.0.1" }
			});
			Dictionary<string, object> dictionary4 = dictionary3;
			if (dictionary2.TryGetValue("tag", out var value2) && value2 is string)
			{
				dictionary4["tag"] = value2;
			}
			if (dictionary2.TryGetValue("sniffing", out var value3) && value3 is Dictionary<string, object>)
			{
				dictionary4["sniffing"] = value3;
			}
			dictionary["inbounds"] = new object[1] { dictionary4 };
			if (dictionary.TryGetValue("api", out var value4) && value4 is Dictionary<string, object>)
			{
				((Dictionary<string, object>)value4).Remove("listen");
			}
		}
		catch (ShareLinkResolutionException ex)
		{
			throw new EngineFailureException("Профиль «" + SafeProfileName(profile.Name) + "»: " + ex.Message);
		}
		catch (Exception ex2)
		{
			if (ex2 is IOException && !(ex2 is InvalidDataException))
			{
				throw;
			}
			throw new ConfigurationException("Некорректный Xray-конфиг или ключ профиля «" + SafeProfileName(profile.Name) + "».");
		}
		string path = RuntimeDirectory();
		Dictionary<string, object> dictionary5 = new Dictionary<string, object>();
		dictionary5.Add("loglevel", "warning");
		Dictionary<string, object> dictionary6 = dictionary5;
		string errorLog = PrepareXrayErrorLog(profile);
		if (errorLog != null)
		{
			dictionary6["error"] = errorLog;
			AppendLog("XRAY_ERROR_LOG name=" + SafeProfileName(profile.Name) + " path=" + errorLog);
		}
		if (providerCatalog.Resolve(profile) == null)
		{
			string text2 = ProviderTrafficObserver.AccessLogPath(store, profile);
			try
			{
				if (File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
			catch
			{
			}
			dictionary6["access"] = text2;
		}
		dictionary["log"] = dictionary6;
		EnsureLongIdlePolicy(dictionary);
		if (profile.TelephonyOptimized && EnsureTelephonyDns(dictionary))
		{
			AppendLog("TELEPHONY_DNS name=" + SafeProfileName(profile.Name) + " resolver=system targetStrategy=UseIPv4" );
		}
		string text3 = Path.Combine(path, profile.Id + "." + Guid.NewGuid().ToString("N") + ".json");
		TrackRuntimeFile(text3);
		string contents;
		lock (json)
		{
			contents = json.Serialize(dictionary);
		}
		try
		{
			File.WriteAllText(text3, contents);
			return text3;
		}
		catch
		{
			DeleteRuntimeFile(text3);
			throw;
		}
	}

	private string PrepareXrayErrorLog(TunnelProfile profile)
	{
		try
		{
			string directory = Path.Combine(store.StateDirectory, "diagnostics");
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, profile.Id + ".xray-error.log");
			if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
			{
				try
				{
					string previous = Path.Combine(directory, profile.Id + ".xray-error.previous.log");
					File.Delete(previous);
					File.Move(path, previous);
				}
				catch (IOException)
				{
					AppendLog("XRAY_ERROR_LOG_ROTATION_DEFERRED name=" + SafeProfileName(profile.Name));
				}
			}
			return path;
		}
		catch (Exception error)
		{
			AppendLog("XRAY_ERROR_LOG_UNAVAILABLE name=" + SafeProfileName(profile.Name) + " reason=" + error.GetType().Name);
			return null;
		}
	}

	internal static bool EnsureTelephonyDns(Dictionary<string, object> root)
	{
		if (!root.TryGetValue("outbounds", out var rawOutbounds) || !(rawOutbounds is object[] outbounds))
		{
			return false;
		}
		var vlessOutbounds = outbounds.OfType<Dictionary<string, object>>()
			.Where(o => o.TryGetValue("protocol", out var protocol) && string.Equals(protocol as string, "vless", StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (vlessOutbounds.Count == 0)
		{
			return false;
		}
		// Resolve call-service names locally, then carry their current IPv4 address
		// through the same VPN server. Remote resolver stalls must not delay calls.
		foreach (var outbound in vlessOutbounds)
		{
			outbound["targetStrategy"] = "UseIPv4";
		}
		if (!root.TryGetValue("dns", out var rawDns) || !(rawDns is Dictionary<string, object> dns))
		{
			dns = new Dictionary<string, object>();
			root["dns"] = dns;
		}
		dns["servers"] = new object[] { "localhost" };
		dns["queryStrategy"] = "UseIPv4";
		return true;
	}

	internal static void EnsureLongIdlePolicy(Dictionary<string, object> root)
	{
		object value;
		Dictionary<string, object> dictionary = (root.TryGetValue("policy", out value) ? (value as Dictionary<string, object>) : null);
		if (dictionary == null)
		{
			dictionary = (Dictionary<string, object>)(root["policy"] = new Dictionary<string, object>());
		}
		object value2;
		Dictionary<string, object> dictionary3 = (dictionary.TryGetValue("levels", out value2) ? (value2 as Dictionary<string, object>) : null);
		if (dictionary3 == null)
		{
			dictionary3 = (Dictionary<string, object>)(dictionary["levels"] = new Dictionary<string, object>());
		}
		if (!dictionary3.ContainsKey("0"))
		{
			dictionary3["0"] = new Dictionary<string, object>();
		}
		foreach (string item in dictionary3.Keys.ToList())
		{
			if (dictionary3[item] is Dictionary<string, object> dictionary5 && (!dictionary5.TryGetValue("connIdle", out var value3) || !int.TryParse(Convert.ToString(value3, CultureInfo.InvariantCulture), out var result) || result < 86400))
			{
				dictionary5["connIdle"] = 86400;
			}
		}
	}

	private EngineLaunch BuildEngineLaunch(TunnelProfile profile, int port)
	{
		if (!UsesMihomo(profile))
		{
			string text = BuildRuntimeXrayConfig(profile, port);
			EngineLaunch engineLaunch = new EngineLaunch();
			engineLaunch.Executable = config.XrayPath;
			engineLaunch.Arguments = "run -config \"" + text + "\"";
			engineLaunch.WorkingDirectory = Path.GetDirectoryName(config.XrayPath);
			engineLaunch.ConfigPath = text;
			return engineLaunch;
		}
		string contents;
		try
		{
			string text2;
			if (string.Equals(profile.ConnectionType, "key", StringComparison.OrdinalIgnoreCase))
			{
				text2 = secrets.Load(profile.Id);
			}
			else
			{
				string text3 = File.ReadAllText(profile.XrayConfigPath);
				if (!MihomoConfig.TryExtractClash(text3, out var links))
				{
					object parsed;
					lock (json)
					{
						parsed = json.DeserializeObject(text3);
					}
					if (!MihomoConfig.TryExtractSingBox(parsed, out links) && !MihomoConfig.TryExtractSip008(parsed, out links))
					{
						throw new InvalidDataException();
					}
				}
				text2 = links.FirstOrDefault();
			}
			if (string.IsNullOrWhiteSpace(text2))
			{
				throw new InvalidDataException();
			}
			contents = MihomoConfig.PrepareForSocks(text2, port);
		}
		catch (Exception ex)
		{
			if (ex is IOException && !(ex is InvalidDataException))
			{
				throw;
			}
			throw new ConfigurationException("Некорректная Clash/sing-box конфигурация профиля «" + SafeProfileName(profile.Name) + "».");
		}
		string text4 = Path.Combine(RuntimeDirectory(), profile.Id + "." + Guid.NewGuid().ToString("N") + ".yaml");
		TrackRuntimeFile(text4);
		try
		{
			File.WriteAllText(text4, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
			DeleteRuntimeFile(text4);
			throw;
		}
		string text5 = Path.Combine(store.StateDirectory, "mihomo", profile.Id);
		Directory.CreateDirectory(text5);
		EngineLaunch engineLaunch2 = new EngineLaunch();
		engineLaunch2.Executable = config.MihomoPath;
		engineLaunch2.Arguments = "-d \"" + text5 + "\" -f \"" + text4 + "\"";
		engineLaunch2.WorkingDirectory = Path.GetDirectoryName(config.MihomoPath);
		engineLaunch2.ConfigPath = text4;
		return engineLaunch2;
	}

	private static bool UsesMihomo(TunnelProfile profile)
	{
		if (profile != null)
		{
			return string.Equals(profile.KeyKind, "mihomo", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private void WriteProxiFyreConfig()
	{
		List<object> list = new List<object>();
		foreach (Group item in groups.Values.OrderBy((Group g) => g.Key, StringComparer.Ordinal))
		{
			list.Add(new Dictionary<string, object>
			{
				{
					"appNames",
					item.Apps.ToArray()
				},
				{
					"socks5ProxyEndpoint",
					"127.0.0.1:" + item.Relay.Port
				},
				{ "socks5Transport", "TCP" },
				{
					"supportedProtocols",
					new string[2] { "TCP", "UDP" }
				},
				{
					"supportedAddressFamilies",
					new string[1] { "IPv4" }
				}
			});
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("logLevel", "Info");
		dictionary.Add("bypassLan", true);
		dictionary.Add("proxies", list);
		dictionary.Add("excludes", new object[0]);
		Dictionary<string, object> obj = dictionary;
		string path = Path.Combine(Path.GetDirectoryName(config.ProxiFyrePath), "app-config.json");
		string s;
		lock (json)
		{
			s = json.Serialize(obj);
		}
		SettingsStore.WriteAtomic(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(s), null);
	}

	private string RuntimeDirectory()
	{
		string text = Path.Combine(store.StateDirectory, "runtime");
		Directory.CreateDirectory(text);
		return text;
	}

	private void TrackRuntimeFile(string path)
	{
		lock (filesGate)
		{
			runtimeFiles.Add(path);
		}
	}

	private void DeleteRuntimeFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			lock (filesGate)
			{
				runtimeFiles.Remove(path);
			}
		}
		catch
		{
			AppendLog("Runtime config cleanup deferred.");
		}
	}

	private void PurgeRuntimeDirectory()
	{
		try
		{
			string[] files = Directory.GetFiles(RuntimeDirectory());
			foreach (string path in files)
			{
				string fileName = Path.GetFileName(path);
				if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						File.Delete(path);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	private void TearDown()
	{
		StopProcess(proxyProcess);
		proxyProcess = null;
		proxyMapping = null;
		foreach (Group value in groups.Values)
		{
			try
			{
				value.Relay.Dispose();
			}
			catch
			{
			}
		}
		groups.Clear();
		List<Backend> list;
		lock (processGate)
		{
			runtimeActive = false;
			list = backends.Values.ToList();
			foreach (Backend item in list)
			{
				item.Life.Cancel();
			}
		}
		foreach (Backend item2 in list)
		{
			try
			{
				item2.ProbeSignal.Set();
			}
			catch (ObjectDisposedException)
			{
			}
			Process process;
			lock (item2.Gate)
			{
				process = item2.Process;
				item2.Process = null;
			}
			StopProcess(process);
		}
		backends.Clear();
		backendIndex = new Dictionary<string, Backend>(StringComparer.OrdinalIgnoreCase);
		lock (processGate)
		{
			reservedPorts.Clear();
			if (processJob != IntPtr.Zero)
			{
				CloseHandle(processJob);
				processJob = IntPtr.Zero;
			}
		}
		if (watcher != null)
		{
			watcher.Changed -= OnAppStateChanged;
			watcher.Dispose();
			watcher = null;
		}
		lock (appAlerted)
		{
			appAlerted.Clear();
		}
		lock (providerFailoverUtc)
		{
			providerFailoverUtc.Clear();
		}
		string[] array;
		lock (filesGate)
		{
			array = runtimeFiles.ToArray();
		}
		string[] array2 = array;
		foreach (string path in array2)
		{
			DeleteRuntimeFile(path);
		}
		skipped.Clear();
		skippedProfiles.Clear();
		uncovered.Clear();
		runtimeSignature = "";
		lock (sync)
		{
			runtimeInfo = new Dictionary<string, ProfileRuntimeInfo>(StringComparer.OrdinalIgnoreCase);
		}
		if (runtimeLease != null)
		{
			try
			{
				runtimeLease.ReleaseMutex();
			}
			catch
			{
			}
			runtimeLease.Dispose();
			runtimeLease = null;
		}
	}

	private Process StartHidden(string file, string args, string directory)
	{
		ProcessStartInfo processStartInfo = new ProcessStartInfo();
		processStartInfo.FileName = file;
		processStartInfo.Arguments = args;
		processStartInfo.WorkingDirectory = directory;
		processStartInfo.UseShellExecute = false;
		processStartInfo.CreateNoWindow = true;
		processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
		Process process = Process.Start(processStartInfo);
		if (process == null)
		{
			throw new EngineFailureException("Не удалось запустить дочерний движок.");
		}
		if (!isWindows)
		{
			return process;
		}
		try
		{
			if (processJob == IntPtr.Zero || !AssignProcessToJobObject(processJob, process.Handle))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			return process;
		}
		catch
		{
			StopProcess(process);
			throw;
		}
	}

	public bool HasForeignProxiFyre(string dedicatedPath)
	{
		string text = Path.GetFileNameWithoutExtension(dedicatedPath ?? "");
		if (string.IsNullOrWhiteSpace(text))
		{
			text = "ProxiFyre";
		}
		Process process = proxyProcess;
		int num = -1;
		try
		{
			if (process != null && ProcessAlive(process))
			{
				num = process.Id;
			}
		}
		catch
		{
		}
		Process[] processesByName = Process.GetProcessesByName(text);
		foreach (Process process2 in processesByName)
		{
			try
			{
				if (!ProcessAlive(process2) || process2.Id == num)
				{
					continue;
				}
				return true;
			}
			finally
			{
				process2.Dispose();
			}
		}
		return false;
	}

	private static bool ProcessAlive(Process process)
	{
		try
		{
			return process != null && !process.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private bool WaitForPort(int port, Process owner, int timeoutMs, CancellationToken token)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.ElapsedMilliseconds < timeoutMs)
		{
			if (token.IsCancellationRequested)
			{
				throw new OperationCanceledException();
			}
			if (!ProcessAlive(owner))
			{
				return false;
			}
			if (OwnsListeningPort(port, owner) && IsPortOpen(port, 350) && ProcessAlive(owner))
			{
				return true;
			}
			if (token.WaitHandle.WaitOne(100))
			{
				throw new OperationCanceledException();
			}
		}
		return false;
	}

	private static bool CanBindPort(int port)
	{
		if (port < 1 || port > 65535)
		{
			return false;
		}
		try
		{
			using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			using Socket socket2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			try
			{
				socket.ExclusiveAddressUse = true;
				socket2.ExclusiveAddressUse = true;
			}
			catch (SocketException)
			{
			}
			catch (PlatformNotSupportedException)
			{
			}
			socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
			socket2.Bind(new IPEndPoint(IPAddress.Loopback, port));
			return true;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	private static bool IsPortOpen(int port, int timeoutMs)
	{
		using TcpClient tcpClient = new TcpClient(AddressFamily.InterNetwork);
		try
		{
			return tcpClient.ConnectAsync(IPAddress.Loopback, port).Wait(timeoutMs) && tcpClient.Connected;
		}
		catch
		{
			return false;
		}
	}

	private void AcquireRuntimeLease()
	{
		if (string.IsNullOrWhiteSpace(config.ProxiFyrePath))
		{
			return;
		}
		string text;
		using (SHA256 sHA = SHA256.Create())
		{
			text = BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(config.ProxiFyrePath).ToUpperInvariant()))).Replace("-", "");
		}
		Mutex mutex = new Mutex(initiallyOwned: false, (isWindows ? "Local\\" : "") + "TRELAYEngine_" + text);
		bool flag = false;
		try
		{
			try
			{
				flag = mutex.WaitOne(0);
			}
			catch (AbandonedMutexException)
			{
				flag = true;
			}
			if (!flag)
			{
				throw new EngineFailureException("Этот движок уже используется другим экземпляром TRELAY.");
			}
			runtimeLease = mutex;
		}
		finally
		{
			if (!flag)
			{
				mutex.Dispose();
			}
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimits information, uint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("iphlpapi.dll")]
	private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int tableClass, uint reserved);

	private void EnsureProcessJob()
	{
		if (!(processJob != IntPtr.Zero))
		{
			IntPtr intPtr = CreateJobObject(IntPtr.Zero, null);
			if (intPtr == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			JobExtendedLimits information = new JobExtendedLimits
			{
				BasicLimitInformation = 
				{
					LimitFlags = 8192u
				}
			};
			if (!SetInformationJobObject(intPtr, 9, ref information, (uint)Marshal.SizeOf(typeof(JobExtendedLimits))))
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				CloseHandle(intPtr);
				throw new Win32Exception(lastWin32Error);
			}
			processJob = intPtr;
		}
	}

	private bool OwnsListeningPort(int port, Process owner)
	{
		if (!ProcessAlive(owner))
		{
			return false;
		}
		if (!isWindows)
		{
			return true;
		}
		int length = 0;
		GetExtendedTcpTable(IntPtr.Zero, ref length, order: false, 2, 3, 0u);
		for (int i = 0; i < 3; i++)
		{
			if (length < 4 || length > 16777216)
			{
				return false;
			}
			IntPtr intPtr = Marshal.AllocHGlobal(length);
			try
			{
				switch (GetExtendedTcpTable(intPtr, ref length, order: false, 2, 3, 0u))
				{
				case 122u:
					break;
				default:
					return false;
				case 0u:
				{
					int num = Marshal.ReadInt32(intPtr);
					for (int j = 0; j < num; j++)
					{
						int num2 = 4 + j * 24;
						int num3 = Marshal.ReadInt32(intPtr, num2 + 4);
						int num4 = Marshal.ReadByte(intPtr, num2 + 8) * 256 + Marshal.ReadByte(intPtr, num2 + 9);
						if (num4 == port && (num3 == 16777343 || num3 == 0) && Marshal.ReadInt32(intPtr, num2 + 20) == owner.Id)
						{
							return true;
						}
					}
					return false;
				}
				}
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}
		return false;
	}

	private void StopProcess(Process process)
	{
		if (process == null)
		{
			return;
		}
		try
		{
			if (!process.HasExited)
			{
				process.Kill();
				if (!process.WaitForExit(1500))
				{
					AppendLog("Owned child did not confirm termination within 1500 ms.");
				}
			}
		}
		catch (Exception ex)
		{
			AppendLog("Owned child cleanup: " + ex.GetType().Name);
		}
		finally
		{
			process.Dispose();
		}
	}

	private bool SetState(int runGeneration, EngineState newState, string text)
	{
		lock (sync)
		{
			if (disposed || requested == null || requested.Generation != runGeneration)
			{
				return false;
			}
			if (state == newState && string.Equals(statusText, text, StringComparison.Ordinal))
			{
				return false;
			}
			state = newState;
			statusText = text;
		}
		AppendLog(newState.ToString().ToUpperInvariant() + " " + text);
		NotifyStateChanged();
		return true;
	}

	private void SetStoppedState()
	{
		lock (sync)
		{
			if ((!disposed && requested != null) || state == EngineState.Off)
			{
				return;
			}
			state = EngineState.Off;
			statusText = "Маршрутизация выключена";
		}
		AppendLog("OFF Маршрутизация выключена");
		NotifyStateChanged();
	}

	private void NotifyStateChanged()
	{
		Action stateChanged = this.StateChanged;
		if (stateChanged == null)
		{
			return;
		}
		Delegate[] invocationList = stateChanged.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action action = (Action)invocationList[i];
			try
			{
				action();
			}
			catch (Exception ex)
			{
				AppendLog("State subscriber failed: " + ex.GetType().Name);
			}
		}
	}

	private void NotifyRouteSwitched(RouteSwitchInfo info)
	{
		AppendLog((info.IsRecovery ? "ROUTE_RESTORED" : "ROUTE_SWITCHED") + " app=" + info.Application + " from=" + SafeProfileName(info.FromProfile) + " to=" + SafeProfileName(info.ToProfile));
		Action<RouteSwitchInfo> routeSwitched = this.RouteSwitched;
		if (routeSwitched == null)
		{
			return;
		}
		Delegate[] invocationList = routeSwitched.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			Action<RouteSwitchInfo> action = (Action<RouteSwitchInfo>)invocationList[i];
			try
			{
				action(info);
			}
			catch (Exception ex)
			{
				AppendLog("Route switch subscriber failed: " + ex.GetType().Name);
			}
		}
	}

	private static string SafeProfileName(string name)
	{
		name = (name ?? "Маршрут").Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (name.Length > 80)
		{
			return name.Substring(0, 80);
		}
		return name;
	}

	private static string NormalizeProcessName(string name)
	{
		name = (name ?? "").Trim();
		if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			name = name.Substring(0, name.Length - 4);
		}
		return name;
	}

	private static string SafeError(Exception error)
	{
		if (error is ConfigurationException)
		{
			return error.Message;
		}
		if (error is EngineFailureException)
		{
			return error.Message;
		}
		if (error is IOException)
		{
			return "Ошибка чтения или записи файлов движка.";
		}
		if (error is UnauthorizedAccessException)
		{
			return "Нет доступа к файлам или запуску движка.";
		}
		if (error is Win32Exception)
		{
			return "Windows не смогла запустить или проверить движок.";
		}
		if (error is CryptographicException)
		{
			return "Не удалось расшифровать ключ профиля.";
		}
		return "Ошибка движка (" + error.GetType().Name + ").";
	}

	private void AppendLog(string text)
	{
		lock (logSync)
		{
			try
			{
				string text2 = Path.Combine(store.StateDirectory, "engine.log");
				if (File.Exists(text2) && new FileInfo(text2).Length >= 1048576)
				{
					File.Delete(text2 + ".1");
					File.Move(text2, text2 + ".1");
				}
				File.AppendAllText(text2, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + " " + (text ?? "").Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
			}
			catch
			{
			}
		}
	}

	public void Dispose()
	{
		Thread thread;
		lock (sync)
		{
			if (disposed)
			{
				return;
			}
			disposed = true;
			requested = null;
			generation++;
			thread = worker;
			if (thread != null)
			{
				wake.Set();
			}
		}
		if (thread != null && thread != Thread.CurrentThread && !thread.Join(8000))
		{
			AppendLog("Engine shutdown did not finish within 8000 ms.");
		}
		if (thread == null)
		{
			SetStoppedState();
			wake.Dispose();
		}
	}
}
