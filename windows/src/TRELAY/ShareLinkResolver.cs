using System;
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
using System.Web;
using System.Web.Script.Serialization;

namespace TRELAY;

internal static class ShareLinkResolver
{
	private sealed class FragmentedHttpsTransport : IDisposable
	{
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

		private Process process;

		private IntPtr job;

		private string configPath;

		internal IWebProxy Proxy { get; private set; }

		private FragmentedHttpsTransport()
		{
		}

		internal static FragmentedHttpsTransport Start()
		{
			FragmentedHttpsTransport fragmentedHttpsTransport = new FragmentedHttpsTransport();
			try
			{
				fragmentedHttpsTransport.StartCore();
				return fragmentedHttpsTransport;
			}
			catch
			{
				fragmentedHttpsTransport.Dispose();
				throw;
			}
		}

		private void StartCore()
		{
			string text = Path.Combine(Path.GetDirectoryName(typeof(ShareLinkResolver).Assembly.Location), "engine", "xray.exe");
			if (!File.Exists(text))
			{
				throw new FileNotFoundException("Bundled xray.exe was not found.", text);
			}
			int num = ReserveFreePort();
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("log", new Dictionary<string, object> { { "loglevel", "warning" } });
			dictionary.Add("inbounds", new object[1]
			{
				new Dictionary<string, object>
				{
					{ "listen", "127.0.0.1" },
					{ "port", num },
					{ "protocol", "http" },
					{
						"settings",
						new Dictionary<string, object> { { "allowTransparent", false } }
					},
					{ "tag", "subscription-http" }
				}
			});
			dictionary.Add("outbounds", new object[1]
			{
				new Dictionary<string, object>
				{
					{ "tag", "fragment" },
					{ "protocol", "freedom" },
					{
						"settings",
						new Dictionary<string, object>
						{
							{ "domainStrategy", "UseIPv4" },
							{
								"fragment",
								new Dictionary<string, object>
								{
									{ "packets", "tlshello" },
									{ "length", "10-20" },
									{ "interval", "1-3" }
								}
							}
						}
					},
					{
						"streamSettings",
						new Dictionary<string, object> { 
						{
							"sockopt",
							new Dictionary<string, object> { { "tcpNoDelay", true } }
						} }
					}
				}
			});
			Dictionary<string, object> obj = dictionary;
			configPath = Path.Combine(Path.GetTempPath(), "trelay-subscription-" + Guid.NewGuid().ToString("N") + ".json");
			JavaScriptSerializer javaScriptSerializer = new JavaScriptSerializer();
			javaScriptSerializer.MaxJsonLength = 2097152;
			JavaScriptSerializer javaScriptSerializer2 = javaScriptSerializer;
			File.WriteAllText(configPath, javaScriptSerializer2.Serialize(obj), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			CreateKillOnCloseJob();
			process = Process.Start(new ProcessStartInfo
			{
				FileName = text,
				Arguments = "run -config \"" + configPath + "\"",
				WorkingDirectory = Path.GetDirectoryName(text),
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			if (process == null)
			{
				throw new InvalidOperationException("Xray process did not start.");
			}
			if (!AssignProcessToJobObject(job, process.Handle))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			if (!WaitForPort(num, process, 4000))
			{
				throw new InvalidOperationException("Xray did not open the temporary proxy.");
			}
			Proxy = new WebProxy("http://127.0.0.1:" + num.ToString(CultureInfo.InvariantCulture), BypassOnLocal: false);
		}

		private static int ReserveFreePort()
		{
			TcpListener tcpListener = new TcpListener(IPAddress.Loopback, 0);
			try
			{
				tcpListener.Server.ExclusiveAddressUse = true;
				tcpListener.Start();
				return ((IPEndPoint)tcpListener.LocalEndpoint).Port;
			}
			finally
			{
				tcpListener.Stop();
			}
		}

		private static bool WaitForPort(int port, Process owner, int timeoutMs)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < timeoutMs)
			{
				if (owner.HasExited)
				{
					return false;
				}
				using (TcpClient tcpClient = new TcpClient())
				{
					try
					{
						Task task = tcpClient.ConnectAsync(IPAddress.Loopback, port);
						if (task.Wait(150) && tcpClient.Connected)
						{
							return true;
						}
					}
					catch
					{
					}
				}
				Thread.Sleep(50);
			}
			return false;
		}

		private void CreateKillOnCloseJob()
		{
			job = CreateJobObject(IntPtr.Zero, null);
			if (job == IntPtr.Zero)
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
			if (!SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf(typeof(JobExtendedLimits))))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
		}

		public void Dispose()
		{
			Proxy = null;
			if (process != null)
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill();
						process.WaitForExit(1500);
					}
				}
				catch
				{
				}
				finally
				{
					process.Dispose();
					process = null;
				}
			}
			if (job != IntPtr.Zero)
			{
				CloseHandle(job);
				job = IntPtr.Zero;
			}
			if (!string.IsNullOrWhiteSpace(configPath))
			{
				try
				{
					File.Delete(configPath);
				}
				catch
				{
				}
				configPath = null;
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
	}

	private const int MaxDownloadBytes = 2097152;

	private const string KeyDnsSuffix = "config-delivery.dev";

	private static readonly UTF8Encoding StrictUtf8;

	private static readonly JavaScriptSerializer Json;

	static ShareLinkResolver()
	{
		StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		Json = new JavaScriptSerializer
		{
			MaxJsonLength = 2097152
		};
		ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
	}

	public static string Resolve(string value)
	{
		return ResolveMany(value).First();
	}

	public static List<string> ResolveMany(string value)
	{
		return ResolveMany(value, null);
	}

	public static List<string> ResolveMany(string value, string subscriptionClientId)
	{
		List<string> list = new List<string>();
		ResolveInto((value ?? "").Trim(), subscriptionClientId, 0, list);
		if (list.Count == 0)
		{
			throw new ShareLinkResolutionException("Ссылка не содержит поддерживаемых VPN-серверов.");
		}
		return list.Distinct(StringComparer.Ordinal).Take(128).ToList();
	}

	private static void ResolveInto(string value, string subscriptionClientId, int depth, List<string> result)
	{
		if (depth >= 4)
		{
			throw new ShareLinkResolutionException("Ссылка содержит слишком длинную цепочку вложенных конфигураций.");
		}
		if (value.StartsWith("key://", StringComparison.OrdinalIgnoreCase))
		{
			ResolveInto(ResolveKey(value), subscriptionClientId, depth + 1, result);
			return;
		}
		if (value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
		{
			foreach (string item in ResolveSsconfMany(value))
			{
				ResolveInto(item, subscriptionClientId, depth + 1, result);
			}
			return;
		}
		if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			foreach (string item2 in ResolveHttpsSubscription(value, subscriptionClientId))
			{
				ResolveInto(item2, subscriptionClientId, depth + 1, result);
			}
			return;
		}
		if (MihomoConfig.IsDirectLink(value))
		{
			if (!MihomoConfig.TryEncodeDirect(value, out var encoded, out var message))
			{
				throw new ShareLinkResolutionException(message);
			}
			result.Add(encoded);
		}
		else if (IsSupportedLink(value))
		{
			result.Add(value.Trim());
		}
	}

	internal static bool ValidateKeyContainer(string value, out string message)
	{
		try
		{
			DecodeKeyContainer(value, out var _, out var secret);
			Array.Clear(secret, 0, secret.Length);
			message = "KEY-ссылка проверена. Конфигурация обновится при подключении.";
			return true;
		}
		catch
		{
			message = "KEY-ссылка повреждена или не прошла проверку целостности.";
			return false;
		}
	}

	internal static bool ValidateSsconf(string value, out string message)
	{
		try
		{
			BuildSsconfUri(value);
			message = "SSCONF-ссылка проверена. Конфигурация обновится при подключении.";
			return true;
		}
		catch
		{
			message = "SSCONF-ссылка повреждена или содержит небезопасный адрес.";
			return false;
		}
	}

	internal static bool ValidateHttpsSubscription(string value, out string message)
	{
		try
		{
			BuildSubscriptionUri(value);
			message = "HTTPS-подписка проверена. Список серверов загрузится после сохранения.";
			return true;
		}
		catch
		{
			message = "Ссылка подписки повреждена или содержит небезопасный адрес.";
			return false;
		}
	}

	private static string ResolveKey(string value)
	{
		string orderId;
		byte[] secret;
		try
		{
			DecodeKeyContainer(value, out orderId, out secret);
		}
		catch (Exception inner)
		{
			throw new ShareLinkResolutionException("KEY-ссылка повреждена или не прошла проверку целостности.", inner);
		}
		try
		{
			string host = orderId + ".config-delivery.dev";
			string text = null;
			Exception ex = null;
			try
			{
				text = QueryTxtOverHttps(host);
			}
			catch (Exception ex2)
			{
				ex = ex2;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				try
				{
					text = QuerySystemTxt(host);
				}
				catch (Exception ex3)
				{
					throw new ShareLinkResolutionException("Для KEY-ссылки не найден актуальный VPN-конфиг. Проверь интернет или срок действия ключа.", ex ?? ex3);
				}
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				throw new ShareLinkResolutionException("Для KEY-ссылки не найден актуальный VPN-конфиг. Проверь интернет или срок действия ключа.");
			}
			string body = DecryptEnvelope(text, secret);
			return ExtractSupportedLink(body, "KEY-ссылка вернула неподдерживаемую конфигурацию.");
		}
		catch (ShareLinkResolutionException)
		{
			throw;
		}
		catch (Exception inner2)
		{
			throw new ShareLinkResolutionException("Не удалось расшифровать актуальную конфигурацию KEY-ссылки.", inner2);
		}
		finally
		{
			Array.Clear(secret, 0, secret.Length);
		}
	}

	private static List<string> ResolveSsconfMany(string value)
	{
		Uri initial;
		try
		{
			initial = BuildSsconfUri(value);
		}
		catch (Exception inner)
		{
			throw new ShareLinkResolutionException("SSCONF-ссылка повреждена или содержит небезопасный адрес.", inner);
		}
		try
		{
			string body = StrictUtf8.GetString(DownloadHttps(initial));
			return ExtractSupportedLinks(body, "SSCONF-ссылка вернула неподдерживаемую конфигурацию.");
		}
		catch (ShareLinkResolutionException)
		{
			throw;
		}
		catch (WebException ex2)
		{
			HttpWebResponse httpWebResponse = ex2.Response as HttpWebResponse;
			string text = ((httpWebResponse == null) ? "" : (" Код сервера: " + (int)httpWebResponse.StatusCode + "."));
			httpWebResponse?.Dispose();
			throw new ShareLinkResolutionException("Не удалось обновить SSCONF. Проверь интернет и доступность сервера конфигурации." + text, ex2);
		}
		catch (Exception inner2)
		{
			throw new ShareLinkResolutionException("Не удалось прочитать конфигурацию SSCONF.", inner2);
		}
	}

	private static List<string> ResolveHttpsSubscription(string value, string subscriptionClientId)
	{
		Uri uri;
		try
		{
			uri = BuildSubscriptionUri(value);
		}
		catch (Exception inner)
		{
			throw new ShareLinkResolutionException("Ссылка подписки повреждена или содержит небезопасный адрес.", inner);
		}
		try
		{
			string body = StrictUtf8.GetString(DownloadHttps(uri, subscriptionClientId, inspectSubscriptionHeaders: true));
			List<string> list = ExtractSupportedLinks(body, "Подписка не содержит поддерживаемых VPN-серверов.");
			if (list.Count == 1 && IsUnsupportedClientResponse(list[0]))
			{
				if (string.IsNullOrWhiteSpace(subscriptionClientId))
				{
					throw new SubscriptionIdentityRequiredException(uri.Host);
				}
				throw new ShareLinkResolutionException("Сервис вернул заглушку для неизвестного клиента и не принял сохранённый ID TRELAY. По этому ответу нельзя определить причину; это не обязательно лимит устройств.");
			}
			return list;
		}
		catch (ShareLinkResolutionException)
		{
			throw;
		}
		catch (WebException ex2)
		{
			HttpWebResponse httpWebResponse = ex2.Response as HttpWebResponse;
			if (string.IsNullOrWhiteSpace(subscriptionClientId) && httpWebResponse != null && (httpWebResponse.StatusCode == HttpStatusCode.Unauthorized || httpWebResponse.StatusCode == HttpStatusCode.Forbidden))
			{
				httpWebResponse.Dispose();
				throw new SubscriptionIdentityRequiredException(uri.Host);
			}
			if (httpWebResponse != null)
			{
				ShareLinkResolutionException subscriptionHeaderError = GetSubscriptionHeaderError(httpWebResponse.Headers, !string.IsNullOrWhiteSpace(subscriptionClientId), uri.Host, errorResponse: true);
				if (subscriptionHeaderError != null)
				{
					httpWebResponse.Dispose();
					throw subscriptionHeaderError;
				}
			}
			int num = (int)(httpWebResponse?.StatusCode ?? ((HttpStatusCode)0));
			string text = ((num == 0) ? "" : (" Код сервера: " + num + "."));
			httpWebResponse?.Dispose();
			if (num == 429)
			{
				throw new ShareLinkResolutionException("Сервис временно ограничил частоту запросов к подписке." + text, ex2);
			}
			if (!string.IsNullOrWhiteSpace(subscriptionClientId) && (num == 401 || num == 403))
			{
				throw new ShareLinkResolutionException("Сервис отклонил сохранённый ID TRELAY." + text + " Ответ не показывает, связан ли отказ со сроком подписки, привязкой или правилом сервиса.", ex2);
			}
			if (num >= 500)
			{
				throw new ShareLinkResolutionException("Сервис подписки временно недоступен." + text, ex2);
			}
			if (num == 0)
			{
				throw new ShareLinkResolutionException(DescribeSubscriptionTransportError(ex2), ex2);
			}
			throw new ShareLinkResolutionException("Сервис подписки отклонил запрос." + text, ex2);
		}
		catch (Exception inner2)
		{
			throw new ShareLinkResolutionException("Не удалось прочитать HTTPS-подписку.", inner2);
		}
	}

	private static Uri BuildSsconfUri(string value)
	{
		value = (value ?? "").Trim();
		if (!value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
		{
			throw new FormatException();
		}
		if (!Uri.TryCreate("https://" + value.Substring("ssconf://".Length), UriKind.Absolute, out var result) || !result.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.Host) || !string.IsNullOrEmpty(result.UserInfo) || result.IsLoopback || result.Fragment.Length > 0)
		{
			throw new FormatException();
		}
		return result;
	}

	private static Uri BuildSubscriptionUri(string value)
	{
		value = (value ?? "").Trim();
		if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var result) || !result.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.Host) || !string.IsNullOrEmpty(result.UserInfo) || result.IsLoopback || result.Fragment.Length > 0)
		{
			throw new FormatException();
		}
		return result;
	}

	private static byte[] DownloadHttps(Uri initial)
	{
		return DownloadHttps(initial, null, inspectSubscriptionHeaders: false);
	}

	private static byte[] DownloadHttps(Uri initial, string subscriptionClientId)
	{
		return DownloadHttps(initial, subscriptionClientId, inspectSubscriptionHeaders: false);
	}

	private static byte[] DownloadHttps(Uri initial, string subscriptionClientId, bool inspectSubscriptionHeaders)
	{
		WebException ex = null;
		int num = (string.IsNullOrWhiteSpace(subscriptionClientId) ? 3 : 4);
		for (int i = 0; i < num; i++)
		{
			try
			{
				return DownloadHttpsOnce(initial, subscriptionClientId, inspectSubscriptionHeaders, i, null);
			}
			catch (WebException ex2)
			{
				ex = ex2;
				if (ex2.Response == null && ShouldUseFragmentedTransport(ex2.Status))
				{
					break;
				}
				if (ex2.Response != null || !IsTransientTransportError(ex2.Status) || i + 1 >= num)
				{
					throw;
				}
				Thread.Sleep((i == 0) ? 300 : 900);
				continue;
			}
		}
		if (ex == null || !ShouldUseFragmentedTransport(ex.Status))
		{
			throw ex ?? new WebException("Subscription request failed.");
		}
		try
		{
			WebException ex3 = null;
			for (int j = 0; j < num; j++)
			{
				try
				{
					using FragmentedHttpsTransport fragmentedHttpsTransport = FragmentedHttpsTransport.Start();
					return DownloadHttpsOnce(initial, subscriptionClientId, inspectSubscriptionHeaders, j, fragmentedHttpsTransport.Proxy);
				}
				catch (WebException ex4)
				{
					ex3 = ex4;
					if (ex4.Response != null || !IsTransientTransportError(ex4.Status) || j + 1 >= num)
					{
						throw;
					}
					Thread.Sleep(j switch
					{
						1 => 800, 
						0 => 300, 
						_ => 1500, 
					});
				}
			}
			throw ex3 ?? ex;
		}
		catch (ShareLinkResolutionException)
		{
			throw;
		}
		catch (WebException ex6)
		{
			throw new ShareLinkResolutionException(string.Concat("Сервис подписки недоступен даже через встроенный обход блокировки. Код сети: ", ex6.Status, "."), ex6);
		}
		catch (Exception inner)
		{
			throw new ShareLinkResolutionException("Прямое соединение с подпиской заблокировано, а TRELAY не смог запустить встроенный обход через Xray.", inner);
		}
	}

	private static byte[] DownloadHttpsOnce(Uri initial, string subscriptionClientId, bool inspectSubscriptionHeaders, int attempt, IWebProxy proxy)
	{
		Uri uri = initial;
		for (int i = 0; i <= 3; i++)
		{
			if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException();
			}
			HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(uri);
			httpWebRequest.Method = "GET";
			httpWebRequest.AllowAutoRedirect = false;
			httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
			httpWebRequest.Timeout = 12000;
			httpWebRequest.ReadWriteTimeout = 12000;
			httpWebRequest.KeepAlive = false;
			httpWebRequest.Pipelined = false;
			httpWebRequest.ProtocolVersion = HttpVersion.Version11;
			httpWebRequest.ConnectionGroupName = "TRELAY-subscription-" + Guid.NewGuid().ToString("N");
			if (proxy != null)
			{
				httpWebRequest.Proxy = proxy;
			}
			bool flag = !string.IsNullOrWhiteSpace(subscriptionClientId) && uri.Host.Equals(initial.Host, StringComparison.OrdinalIgnoreCase);
			httpWebRequest.UserAgent = ((flag && attempt >= 2) ? "v2rayN/7.15.2" : (flag ? "Happ/4.1.2" : AppInfo.Name + "/" + AppInfo.Version));
			if (flag)
			{
				httpWebRequest.Headers["X-Hwid"] = subscriptionClientId;
				if (attempt == 0)
				{
					httpWebRequest.Headers["X-Device-Os"] = "Windows";
					httpWebRequest.Headers["X-Device-Model"] = "Desktop";
					httpWebRequest.Headers["X-Device-Locale"] = CultureInfo.CurrentUICulture.Name;
					httpWebRequest.Headers["X-Ver-Os"] = Environment.OSVersion.Version.ToString();
				}
			}
			using HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();
			int statusCode = (int)httpWebResponse.StatusCode;
			if (statusCode >= 300 && statusCode <= 399)
			{
				string text = httpWebResponse.Headers[HttpResponseHeader.Location];
				if (i == 3 || string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(uri, text, out var result) || !result.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(result.UserInfo) || result.IsLoopback)
				{
					throw new WebException("Unsafe or excessive redirect.");
				}
				uri = result;
				continue;
			}
			if (httpWebResponse.StatusCode != HttpStatusCode.OK)
			{
				throw new WebException("Unexpected HTTP status.");
			}
			if (httpWebResponse.ContentLength > 2097152)
			{
				throw new InvalidDataException("Response is too large.");
			}
			if (inspectSubscriptionHeaders)
			{
				ShareLinkResolutionException subscriptionHeaderError = GetSubscriptionHeaderError(httpWebResponse.Headers, flag, initial.Host, errorResponse: false);
				if (subscriptionHeaderError != null)
				{
					throw subscriptionHeaderError;
				}
			}
			byte[] array;
			using (Stream stream = httpWebResponse.GetResponseStream())
			{
				array = ReadLimited(stream, 2097152);
			}
			if (inspectSubscriptionHeaders && array.Length == 0)
			{
				bool flag2 = HeaderIsTrue(httpWebResponse.Headers, "x-hwid-limit");
				if (flag && flag2)
				{
					throw new SubscriptionDeviceLimitException("Сервис подписки подтвердил лимит устройств и не выдал серверы. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
				}
				if (!flag && HeaderIsTrue(httpWebResponse.Headers, "x-hwid-not-supported"))
				{
					throw new SubscriptionIdentityRequiredException(initial.Host);
				}
				throw new ShareLinkResolutionException("Сервис подписки ответил без списка серверов.");
			}
			return array;
		}
		throw new WebException("Too many redirects.");
	}

	private static ShareLinkResolutionException GetSubscriptionHeaderError(WebHeaderCollection headers, bool authenticated, string host, bool errorResponse)
	{
		if (headers == null)
		{
			return null;
		}
		if (HeaderIsTrue(headers, "x-hwid-max-devices-reached"))
		{
			return new SubscriptionDeviceLimitException("Сервис подписки подтвердил: исчерпан лимит устройств. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
		}
		if (HeaderIsTrue(headers, "x-hwid-not-supported"))
		{
			if (!authenticated)
			{
				return new SubscriptionIdentityRequiredException(host);
			}
			return new ShareLinkResolutionException("Сервис не распознал сохранённый ID устройства TRELAY. Провайдер использует несовместимые правила подписки.");
		}
		if (authenticated && errorResponse && HeaderIsTrue(headers, "x-hwid-limit"))
		{
			return new SubscriptionDeviceLimitException("Сервис подписки отклонил запрос из-за ограничения устройств. Удали старое устройство в кабинете провайдера либо попроси поддержку освободить слот.");
		}
		return null;
	}

	private static bool HeaderIsTrue(WebHeaderCollection headers, string name)
	{
		if (headers != null)
		{
			return string.Equals((headers[name] ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool IsTransientTransportError(WebExceptionStatus status)
	{
		if (status != WebExceptionStatus.UnknownError && status != WebExceptionStatus.TrustFailure && status != WebExceptionStatus.SecureChannelFailure && status != WebExceptionStatus.Timeout && status != WebExceptionStatus.ConnectFailure && status != WebExceptionStatus.ReceiveFailure && status != WebExceptionStatus.SendFailure && status != WebExceptionStatus.ConnectionClosed && status != WebExceptionStatus.KeepAliveFailure)
		{
			return status == WebExceptionStatus.PipelineFailure;
		}
		return true;
	}

	private static bool ShouldUseFragmentedTransport(WebExceptionStatus status)
	{
		if (status != WebExceptionStatus.UnknownError && status != WebExceptionStatus.TrustFailure && status != WebExceptionStatus.SecureChannelFailure && status != WebExceptionStatus.Timeout && status != WebExceptionStatus.ConnectFailure && status != WebExceptionStatus.ReceiveFailure && status != WebExceptionStatus.SendFailure && status != WebExceptionStatus.ConnectionClosed && status != WebExceptionStatus.KeepAliveFailure)
		{
			return status == WebExceptionStatus.PipelineFailure;
		}
		return true;
	}

	private static string DescribeSubscriptionTransportError(WebException error)
	{
		if (error == null)
		{
			return "Не удалось получить подписку из-за неизвестной сетевой ошибки.";
		}
		switch (error.Status)
		{
		case WebExceptionStatus.NameResolutionFailure:
			return "DNS не смог найти сервис подписки. Проверь подключение и DNS.";
		case WebExceptionStatus.TrustFailure:
		case WebExceptionStatus.SecureChannelFailure:
			return "Windows не приняла сертификат сервиса подписки. Проверь системное время и обновления корневых сертификатов.";
		case WebExceptionStatus.Timeout:
			return "Сервис подписки не ответил за 12 секунд после повторной попытки.";
		case WebExceptionStatus.ConnectFailure:
			return "Не удалось подключиться к сервису подписки после повторной попытки.";
		case WebExceptionStatus.ReceiveFailure:
		case WebExceptionStatus.SendFailure:
		case WebExceptionStatus.PipelineFailure:
		case WebExceptionStatus.ConnectionClosed:
		case WebExceptionStatus.KeepAliveFailure:
			return string.Concat("Сервис подписки оборвал соединение. TRELAY повторил запрос с совместимыми настройками, но сервер снова закрыл его. Код сети: ", error.Status, ".");
		default:
			return string.Concat("Сетевая ошибка при загрузке подписки. Код сети: ", error.Status, ".");
		}
	}

	private static bool IsUnsupportedClientResponse(string link)
	{
		try
		{
			if (!Uri.TryCreate(link, UriKind.Absolute, out var result))
			{
				return false;
			}
			string text = ((result.Fragment.Length > 1) ? Uri.UnescapeDataString(result.Fragment.Substring(1)) : "");
			return (result.Host == "0.0.0.0" && result.Port == 1) || text.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
	}

	private static byte[] ReadLimited(Stream stream, int limit)
	{
		if (stream == null)
		{
			throw new IOException();
		}
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array = new byte[8192];
		int num;
		while ((num = stream.Read(array, 0, array.Length)) > 0)
		{
			if (memoryStream.Length + num > limit)
			{
				throw new InvalidDataException("Response is too large.");
			}
			memoryStream.Write(array, 0, num);
		}
		return memoryStream.ToArray();
	}

	private static string QueryTxtOverHttps(string host)
	{
		Uri requestUri = new Uri("https://cloudflare-dns.com/dns-query?name=" + HttpUtility.UrlEncode(host) + "&type=TXT");
		HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(requestUri);
		httpWebRequest.Method = "GET";
		httpWebRequest.Accept = "application/dns-json";
		httpWebRequest.AllowAutoRedirect = false;
		httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
		httpWebRequest.Timeout = 8000;
		httpWebRequest.ReadWriteTimeout = 8000;
		httpWebRequest.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
		using HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();
		if (httpWebResponse.StatusCode != HttpStatusCode.OK)
		{
			throw new WebException("DNS over HTTPS failed.");
		}
		Dictionary<string, object> dictionary;
		using (Stream stream = httpWebResponse.GetResponseStream())
		{
			byte[] bytes = ReadLimited(stream, 2097152);
			lock (Json)
			{
				dictionary = Json.DeserializeObject(StrictUtf8.GetString(bytes)) as Dictionary<string, object>;
			}
		}
		if (dictionary == null || !dictionary.TryGetValue("Status", out var value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0)
		{
			throw new InvalidDataException("DNS response status is not successful.");
		}
		object value2;
		object[] array = (dictionary.TryGetValue("Answer", out value2) ? (value2 as object[]) : null);
		if (array == null)
		{
			return null;
		}
		object[] array2 = array;
		foreach (object obj in array2)
		{
			if (obj is Dictionary<string, object> dictionary2 && dictionary2.TryGetValue("type", out var value3) && Convert.ToInt32(value3, CultureInfo.InvariantCulture) == 16 && dictionary2.TryGetValue("data", out var value4))
			{
				string text = DecodeDnsJsonTxt(Convert.ToString(value4, CultureInfo.InvariantCulture));
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
		}
		return null;
	}

	private static string DecodeDnsJsonTxt(string value)
	{
		value = (value ?? "").Trim();
		if (value.Length == 0)
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder();
		int i;
		for (i = 0; i < value.Length; i++)
		{
			for (; i < value.Length && char.IsWhiteSpace(value[i]); i++)
			{
			}
			if (i >= value.Length)
			{
				break;
			}
			if (value[i] != '"')
			{
				return value;
			}
			i++;
			while (i < value.Length && value[i] != '"')
			{
				if (value[i] == '\\' && i + 1 < value.Length)
				{
					i++;
					if (value[i] >= '0' && value[i] <= '7')
					{
						int num = 0;
						int num2 = 0;
						while (i < value.Length && num2 < 3 && value[i] >= '0' && value[i] <= '7')
						{
							num = num * 8 + value[i++] - 48;
							num2++;
						}
						stringBuilder.Append((char)num);
						continue;
					}
				}
				stringBuilder.Append(value[i++]);
			}
			if (i >= value.Length)
			{
				throw new FormatException();
			}
		}
		return stringBuilder.ToString();
	}

	private static string QuerySystemTxt(string host)
	{
		IntPtr results;
		int num = DnsQuery(host, 16, 0u, IntPtr.Zero, out results, IntPtr.Zero);
		if (num != 0 || results == IntPtr.Zero)
		{
			throw new IOException("System DNS query failed: " + num);
		}
		try
		{
			IntPtr intPtr = results;
			while (intPtr != IntPtr.Zero)
			{
				int ofs = IntPtr.Size * 2;
				if ((ushort)Marshal.ReadInt16(intPtr, ofs) == 16)
				{
					int num2 = IntPtr.Size * 2 + 16;
					int num3 = Marshal.ReadInt32(intPtr, num2);
					if (num3 >= 1 && num3 <= 128)
					{
						int num4 = num2 + ((IntPtr.Size == 8) ? 8 : 4);
						StringBuilder stringBuilder = new StringBuilder();
						for (int i = 0; i < num3; i++)
						{
							IntPtr intPtr2 = Marshal.ReadIntPtr(intPtr, num4 + i * IntPtr.Size);
							if (intPtr2 != IntPtr.Zero)
							{
								stringBuilder.Append(Marshal.PtrToStringUni(intPtr2));
							}
						}
						if (stringBuilder.Length > 0)
						{
							return stringBuilder.ToString();
						}
					}
				}
				intPtr = Marshal.ReadIntPtr(intPtr);
			}
			return null;
		}
		finally
		{
			DnsRecordListFree(results, 1);
		}
	}

	private static void DecodeKeyContainer(string value, out string orderId, out byte[] secret)
	{
		value = (value ?? "").Trim();
		if (!value.StartsWith("key://", StringComparison.OrdinalIgnoreCase))
		{
			throw new FormatException();
		}
		string bootstrapSecret = BuildSecrets.KeyBootstrapSecret;
		if (string.IsNullOrWhiteSpace(bootstrapSecret))
		{
			throw new InvalidOperationException("Поддержка key:// не настроена в этой сборке.");
		}
		byte[] array = Convert.FromBase64String(bootstrapSecret);
		string input;
		try
		{
			input = DecryptEnvelope(value.Substring("key://".Length), array);
		}
		finally
		{
			Array.Clear(array, 0, array.Length);
		}
		Dictionary<string, object> dictionary;
		lock (Json)
		{
			dictionary = Json.DeserializeObject(input) as Dictionary<string, object>;
		}
		if (dictionary == null || !dictionary.TryGetValue("orderId", out var value2) || !dictionary.TryGetValue("key", out var value3))
		{
			throw new FormatException();
		}
		orderId = Convert.ToString(value2, CultureInfo.InvariantCulture).Trim();
		if (orderId.Length < 1 || orderId.Length > 63)
		{
			throw new FormatException();
		}
		string text = orderId;
		foreach (char c in text)
		{
			if (!char.IsLetterOrDigit(c) && c != '-')
			{
				throw new FormatException();
			}
		}
		secret = Convert.FromBase64String(Convert.ToString(value3, CultureInfo.InvariantCulture));
		if (secret.Length != 32)
		{
			Array.Clear(secret, 0, secret.Length);
			throw new FormatException();
		}
	}

	internal static string DecryptEnvelope(string encoded, byte[] key)
	{
		if (key == null || key.Length != 32)
		{
			throw new CryptographicException();
		}
		string input = StrictUtf8.GetString(DecodeBase64(encoded));
		Dictionary<string, object> dictionary;
		lock (Json)
		{
			dictionary = Json.DeserializeObject(input) as Dictionary<string, object>;
		}
		if (dictionary == null || !dictionary.TryGetValue("iv", out var value) || !dictionary.TryGetValue("value", out var value2))
		{
			throw new FormatException();
		}
		string text = Convert.ToString(value, CultureInfo.InvariantCulture);
		string text2 = Convert.ToString(value2, CultureInfo.InvariantCulture);
		if (!dictionary.TryGetValue("mac", out var value3) || string.IsNullOrWhiteSpace(Convert.ToString(value3, CultureInfo.InvariantCulture)))
		{
			throw new CryptographicException();
		}
		byte[] array;
		using (HMACSHA256 hMACSHA = new HMACSHA256(key))
		{
			array = hMACSHA.ComputeHash(Encoding.UTF8.GetBytes(text + text2));
		}
		byte[] array2;
		try
		{
			array2 = HexToBytes(Convert.ToString(value3, CultureInfo.InvariantCulture));
		}
		catch
		{
			Array.Clear(array, 0, array.Length);
			throw new CryptographicException();
		}
		bool flag = FixedTimeEquals(array, array2);
		Array.Clear(array, 0, array.Length);
		Array.Clear(array2, 0, array2.Length);
		if (!flag)
		{
			throw new CryptographicException();
		}
		byte[] array3 = Convert.FromBase64String(text);
		byte[] array4 = Convert.FromBase64String(text2);
		if (array3.Length != 16 || array4.Length == 0 || array4.Length % 16 != 0)
		{
			throw new CryptographicException();
		}
		try
		{
			using Aes aes = Aes.Create();
			aes.KeySize = 256;
			aes.BlockSize = 128;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			aes.Key = key;
			aes.IV = array3;
			using ICryptoTransform cryptoTransform = aes.CreateDecryptor();
			byte[] array5 = cryptoTransform.TransformFinalBlock(array4, 0, array4.Length);
			try
			{
				return StrictUtf8.GetString(array5);
			}
			finally
			{
				Array.Clear(array5, 0, array5.Length);
			}
		}
		finally
		{
			Array.Clear(array3, 0, array3.Length);
			Array.Clear(array4, 0, array4.Length);
		}
	}

	private static string ExtractSupportedLink(string body, string error)
	{
		return ExtractSupportedLinks(body, error).First();
	}

	private static List<string> ExtractSupportedLinks(string body, string error)
	{
		List<string> list = new List<string>();
		body = (body ?? "").Trim().TrimStart('\ufeff');
		if (MihomoConfig.TryExtractClash(body, out var links))
		{
			list.AddRange(links);
		}
		list.AddRange(MihomoConfig.ExtractClashProviderUrls(body));
		if (body.IndexOfAny(new char[2] { '\r', '\n' }) < 0 && IsSupportedLink(body))
		{
			list.Add(body);
		}
		if (body.StartsWith("\"", StringComparison.Ordinal))
		{
			try
			{
				string value;
				lock (Json)
				{
					value = Json.Deserialize<string>(body);
				}
				CollectLinks(value, list);
			}
			catch
			{
			}
		}
		if (body.StartsWith("{", StringComparison.Ordinal) || body.StartsWith("[", StringComparison.Ordinal))
		{
			try
			{
				object obj2;
				lock (Json)
				{
					obj2 = Json.DeserializeObject(body);
				}
				if (MihomoConfig.TryExtractSingBox(obj2, out var links2))
				{
					list.AddRange(links2);
				}
				CollectJsonLinks(obj2, list, 0);
			}
			catch
			{
			}
		}
		string[] array = body.Replace("\r", "\n").Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string value2 in array)
		{
			CollectLinks(value2, list);
		}
		try
		{
			string text = StrictUtf8.GetString(DecodeBase64(body));
			if (MihomoConfig.TryExtractClash(text, out links))
			{
				list.AddRange(links);
			}
			list.AddRange(MihomoConfig.ExtractClashProviderUrls(text));
			if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
			{
				try
				{
					object parsed;
					lock (Json)
					{
						parsed = Json.DeserializeObject(text);
					}
					if (MihomoConfig.TryExtractSingBox(parsed, out var links3))
					{
						list.AddRange(links3);
					}
				}
				catch
				{
				}
			}
			string[] array2 = text.Replace("\r", "\n").Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string value3 in array2)
			{
				CollectLinks(value3, list);
			}
		}
		catch
		{
		}
		list = (from x in list
			where IsSupportedLink(x) || IsSafeNestedSubscription(x)
			select x.Trim()).Distinct(StringComparer.Ordinal).Take(128).ToList();
		if (list.Count > 0)
		{
			return list;
		}
		throw new ShareLinkResolutionException(error);
	}

	private static void CollectLinks(string value, List<string> links)
	{
		value = (value ?? "").Trim().Trim('"');
		if (IsSupportedLink(value))
		{
			if (links.Count < 128)
			{
				links.Add(value);
			}
		}
		else
		{
			if (value.Length < 8 || value.Length > 2097152)
			{
				return;
			}
			try
			{
				string text = StrictUtf8.GetString(DecodeBase64(value));
				if (string.Equals(text, value, StringComparison.Ordinal))
				{
					return;
				}
				string[] array = text.Replace("\r", "\n").Split(new char[1] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string text2 in array)
				{
					if (IsSupportedLink(text2.Trim()) && links.Count < 128)
					{
						links.Add(text2.Trim());
					}
				}
			}
			catch
			{
			}
		}
	}

	private static void CollectJsonLinks(object value, List<string> links, int depth)
	{
		if (value == null || depth > 8 || links.Count >= 128)
		{
			return;
		}
		if (value is string value2)
		{
			CollectLinks(value2, links);
		}
		else if (value is Dictionary<string, object> dictionary)
		{
			if (!EmbeddedXrayConfig.Validate(dictionary, out var _))
			{
				string text = LinkFromJson(dictionary);
				if (IsSupportedLink(text))
				{
					links.Add(text);
				}
				{
					foreach (object value4 in dictionary.Values)
					{
						CollectJsonLinks(value4, links, depth + 1);
					}
					return;
				}
			}
			links.Add(EmbeddedXrayConfig.Encode(dictionary));
		}
		else if (value is object[] array)
		{
			object[] array2 = array;
			foreach (object value3 in array2)
			{
				CollectJsonLinks(value3, links, depth + 1);
			}
		}
	}

	private static string LinkFromJson(Dictionary<string, object> root)
	{
		if (root == null)
		{
			return "";
		}
		string[] array = new string[4] { "url", "key", "accessKey", "config" };
		foreach (string key in array)
		{
			if (root.TryGetValue(key, out var value) && value is string && IsSupportedLink((string)value))
			{
				return ((string)value).Trim();
			}
		}
		if (root.ContainsKey("plugin") || root.ContainsKey("plugin_opts"))
		{
			return "";
		}
		if (!root.TryGetValue("server", out var value2) || !root.TryGetValue("server_port", out var value3) || !root.TryGetValue("method", out var value4) || !root.TryGetValue("password", out var value5))
		{
			return "";
		}
		string text = Convert.ToString(value2, CultureInfo.InvariantCulture).Trim();
		if (Uri.CheckHostName(text) == UriHostNameType.Unknown || !int.TryParse(Convert.ToString(value3, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 1 || result > 65535)
		{
			return "";
		}
		string s = Convert.ToString(value4, CultureInfo.InvariantCulture) + ":" + Convert.ToString(value5, CultureInfo.InvariantCulture);
		string text2 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-')
			.Replace('/', '_');
		if (Uri.CheckHostName(text) == UriHostNameType.IPv6)
		{
			text = "[" + text + "]";
		}
		return "ss://" + text2 + "@" + text + ":" + result;
	}

	private static bool IsSupportedLink(string value)
	{
		value = (value ?? "").Trim();
		if (!value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase) && !MihomoConfig.IsDirectLink(value) && !value.StartsWith("key://", StringComparison.OrdinalIgnoreCase))
		{
			return value.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsSafeNestedSubscription(string value)
	{
		if (Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var result) && result.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(result.Host) && string.IsNullOrEmpty(result.UserInfo) && !result.IsLoopback)
		{
			return result.Fragment.Length == 0;
		}
		return false;
	}

	private static byte[] DecodeBase64(string value)
	{
		value = (value ?? "").Trim().Replace('-', '+').Replace('_', '/');
		while (value.Length % 4 != 0)
		{
			value += "=";
		}
		return Convert.FromBase64String(value);
	}

	private static byte[] HexToBytes(string value)
	{
		value = (value ?? "").Trim();
		if (value.Length == 0 || value.Length % 2 != 0)
		{
			throw new FormatException();
		}
		byte[] array = new byte[value.Length / 2];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		}
		return array;
	}

	private static bool FixedTimeEquals(byte[] left, byte[] right)
	{
		int num = left.Length ^ right.Length;
		int num2 = Math.Min(left.Length, right.Length);
		for (int i = 0; i < num2; i++)
		{
			num |= left[i] ^ right[i];
		}
		return num == 0;
	}

	[DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W", SetLastError = true)]
	private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

	[DllImport("dnsapi.dll")]
	private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
