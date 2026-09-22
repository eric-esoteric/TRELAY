using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace TRELAY;

internal static class SocksHealthProbe
{
	private enum ProbeMode
	{
		Connect,
		Tls,
		Http,
		Hold
	}

	internal static readonly string[] InternetTargets = new string[3] { "http://cp.cloudflare.com/generate_204", "http://www.gstatic.com/generate_204", "www.cloudflare.com:443" };

	public static bool ValidateEndpoint(string value, out string message)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			message = "";
			return true;
		}
		if (!TryParse(value, out var _, out var _))
		{
			message = "Адрес проверки должен быть в формате host:port";
			return false;
		}
		message = "";
		return true;
	}

	public static bool Probe(int socksPort, string endpoint, out string failure)
	{
		return Probe(socksPort, endpoint, 10000, out failure);
	}

	public static bool Probe(int socksPort, string endpoint, int timeoutMs, out string failure)
	{
		return Probe(socksPort, endpoint, timeoutMs, CancellationToken.None, out failure);
	}

	public static bool ProbeTcp(int socksPort, string endpoint, int timeoutMs, out string failure)
	{
		return ProbeTcp(socksPort, endpoint, timeoutMs, CancellationToken.None, out failure);
	}

	public static bool Probe(int socksPort, string endpoint, int timeoutMs, CancellationToken cancellation, out string failure)
	{
		return ProbeService(socksPort, endpoint, timeoutMs, cancellation, out failure);
	}

	public static bool ProbeTcp(int socksPort, string endpoint, int timeoutMs, CancellationToken cancellation, out string failure)
	{
		if (!TryParse(endpoint, out var host, out var port))
		{
			failure = "некорректный адрес";
			return false;
		}
		return ProbeCore(socksPort, host, port, ProbeMode.Connect, null, timeoutMs, cancellation, out failure);
	}

	public static bool ProbeService(int socksPort, string endpoint, int timeoutMs, CancellationToken cancellation, out string failure)
	{
		endpoint = (endpoint ?? "").Trim();
		if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var result) || string.IsNullOrEmpty(result.Host))
			{
				failure = "некорректный адрес";
				return false;
			}
			return ProbeCore(socksPort, result.IdnHost, result.Port, ProbeMode.Http, result.PathAndQuery, timeoutMs, cancellation, out failure);
		}
		if (!TryParse(endpoint, out var host, out var port))
		{
			failure = "некорректный адрес";
			return false;
		}
		return ProbeCore(socksPort, host, port, port switch
		{
			80 => ProbeMode.Http, 
			443 => ProbeMode.Tls, 
			_ => ProbeMode.Hold, 
		}, "/", timeoutMs, cancellation, out failure);
	}

	private static bool ProbeCore(int socksPort, string host, int port, ProbeMode mode, string path, int timeoutMs, CancellationToken cancellation, out string failure)
	{
		cancellation.ThrowIfCancellationRequested();
		if (socksPort < 1 || socksPort > 65535 || port < 1 || port > 65535 || timeoutMs < 1 || string.IsNullOrEmpty(host))
		{
			failure = "некорректный адрес или тайм-аут";
			return false;
		}
		int expired = 0;
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			TcpClient client = new TcpClient(AddressFamily.InterNetwork);
			try
			{
				using (cancellation.Register(delegate
				{
					try
					{
						client.Close();
					}
					catch
					{
					}
				}))
				{
					using (new Timer(delegate
					{
						Interlocked.Exchange(ref expired, 1);
						try
						{
							client.Close();
						}
						catch
						{
						}
					}, null, timeoutMs, -1))
					{
						client.NoDelay = true;
						client.ReceiveTimeout = timeoutMs;
						client.SendTimeout = timeoutMs;
						if (!client.ConnectAsync(IPAddress.Loopback, socksPort).Wait(timeoutMs))
						{
							throw new IOException("локальный SOCKS не отвечает");
						}
						cancellation.ThrowIfCancellationRequested();
						NetworkStream stream = client.GetStream();
						stream.Write(new byte[3] { 5, 1, 0 }, 0, 3);
						byte[] array = ReadExact(stream, 2);
						if (array[0] != 5 || array[1] != 0)
						{
							throw new IOException("SOCKS отклонил подключение");
						}
						byte[] array2 = BuildConnectRequest(host, port);
						stream.Write(array2, 0, array2.Length);
						byte[] array3 = ReadExact(stream, 4);
						if (array3[0] != 5 || array3[1] != 0 || array3[2] != 0)
						{
							throw new IOException("сервер вернул SOCKS-ошибку (код " + array3[1] + ")");
						}
						if (array3[3] == 1)
						{
							ReadExact(stream, 4);
						}
						else if (array3[3] == 4)
						{
							ReadExact(stream, 16);
						}
						else
						{
							if (array3[3] != 3)
							{
								throw new IOException("неизвестный тип SOCKS-адреса");
							}
							byte b = ReadExact(stream, 1)[0];
							if (b == 0)
							{
								throw new IOException("SOCKS вернул пустой адрес");
							}
							ReadExact(stream, b);
						}
						ReadExact(stream, 2);
						switch (mode)
						{
						case ProbeMode.Tls:
						{
							IPAddress address;
							using (SslStream sslStream = (IPAddress.TryParse(host, out address) ? new SslStream(stream, leaveInnerStreamOpen: true, (object param0, X509Certificate param1, X509Chain param2, SslPolicyErrors param3) => true) : new SslStream(stream, leaveInnerStreamOpen: true)))
							{
								sslStream.AuthenticateAsClient(host, null, SslProtocols.Tls12, checkCertificateRevocation: false);
							}
							break;
						}
						case ProbeMode.Http:
						{
							string text = ((port == 80) ? host : (host + ":" + port.ToString(CultureInfo.InvariantCulture)));
							byte[] bytes = Encoding.ASCII.GetBytes("HEAD " + (string.IsNullOrEmpty(path) ? "/" : path) + " HTTP/1.1\r\nHost: " + text + "\r\nUser-Agent: TRELAY-health\r\nConnection: close\r\n\r\n");
							stream.Write(bytes, 0, bytes.Length);
							byte[] bytes2 = ReadExact(stream, 5);
							if (Encoding.ASCII.GetString(bytes2) != "HTTP/")
							{
								throw new IOException("сервис вернул не HTTP-ответ");
							}
							break;
						}
						case ProbeMode.Hold:
						{
							int num = (int)Math.Min(1500L, timeoutMs - stopwatch.ElapsedMilliseconds - 250);
							if (num < 200)
							{
								break;
							}
							client.ReceiveTimeout = num;
							try
							{
								byte[] buffer = new byte[1];
								if (stream.Read(buffer, 0, 1) <= 0)
								{
									throw new EndOfStreamException("удалённый узел закрыл соединение");
								}
							}
							catch (IOException ex)
							{
								if (!(ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) || Volatile.Read(ref expired) != 0)
								{
									throw;
								}
							}
							break;
						}
						}
					}
				}
			}
			finally
			{
				if (client != null)
				{
					((IDisposable)client).Dispose();
				}
			}
			cancellation.ThrowIfCancellationRequested();
			if (Volatile.Read(ref expired) != 0)
			{
				failure = "истёк общий тайм-аут проверки";
				return false;
			}
			failure = "";
			return true;
		}
		catch (Exception error)
		{
			cancellation.ThrowIfCancellationRequested();
			if (Volatile.Read(ref expired) != 0)
			{
				failure = "истёк общий тайм-аут проверки";
			}
			else
			{
				failure = ((mode == ProbeMode.Tls) ? "TLS-сервис не ответил: " : "сервис не ответил: ") + SafeFailure(error);
			}
			return false;
		}
	}

	internal static byte[] BuildConnectRequest(string host, int port)
	{
		IPAddress address;
		bool flag = IPAddress.TryParse(host, out address);
		byte[] array = (flag ? address.GetAddressBytes() : Encoding.ASCII.GetBytes(host));
		if (!flag && (array.Length == 0 || array.Length > 255))
		{
			throw new IOException("некорректное имя узла");
		}
		byte[] array2 = new byte[6 + array.Length + ((!flag) ? 1 : 0)];
		array2[0] = 5;
		array2[1] = 1;
		array2[2] = 0;
		array2[3] = (byte)((!flag) ? 3 : ((address.AddressFamily == AddressFamily.InterNetwork) ? 1 : 4));
		int dstOffset = (flag ? 4 : 5);
		if (!flag)
		{
			array2[4] = (byte)array.Length;
		}
		Buffer.BlockCopy(array, 0, array2, dstOffset, array.Length);
		array2[array2.Length - 2] = (byte)(port >> 8);
		array2[array2.Length - 1] = (byte)port;
		return array2;
	}

	private static string SafeFailure(Exception error)
	{
		if (error is AggregateException ex)
		{
			error = ex.GetBaseException();
		}
		if (error is AuthenticationException)
		{
			return "проверка сертификата или TLS не пройдена";
		}
		if (error is SocketException)
		{
			return "ошибка сетевого соединения";
		}
		if (error is ObjectDisposedException)
		{
			return "соединение закрыто";
		}
		if (error is IOException)
		{
			return error.Message;
		}
		return error.GetType().Name;
	}

	internal static bool TryParse(string value, out string host, out int port)
	{
		host = "";
		port = 0;
		value = (value ?? "").Trim();
		int num = value.LastIndexOf(':');
		if (num <= 0 || num == value.Length - 1 || !int.TryParse(value.Substring(num + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
		{
			return false;
		}
		host = value.Substring(0, num);
		if (host.StartsWith("[", StringComparison.Ordinal))
		{
			if (!host.EndsWith("]", StringComparison.Ordinal))
			{
				return false;
			}
			host = host.Substring(1, host.Length - 2);
			if (IPAddress.TryParse(host, out var address))
			{
				return address.AddressFamily == AddressFamily.InterNetworkV6;
			}
			return false;
		}
		if (host.IndexOf(':') >= 0 || host.IndexOfAny(new char[9] { '/', '\\', '@', '?', '#', ' ', '\t', '\r', '\n' }) >= 0)
		{
			return false;
		}
		try
		{
			host = new IdnMapping().GetAscii(host);
		}
		catch
		{
			return false;
		}
		if (host.Length > 0 && host.Length <= 253)
		{
			return Uri.CheckHostName(host) != UriHostNameType.Unknown;
		}
		return false;
	}

	private static byte[] ReadExact(Stream stream, int count)
	{
		byte[] array = new byte[count];
		int num;
		for (int i = 0; i < count; i += num)
		{
			num = stream.Read(array, i, count - i);
			if (num <= 0)
			{
				throw new EndOfStreamException("SOCKS закрыл соединение");
			}
		}
		return array;
	}
}
