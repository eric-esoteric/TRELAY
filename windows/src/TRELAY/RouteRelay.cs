using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TRELAY;

internal sealed class RouteRelay : IDisposable
{
	private sealed class DeadlineGuard : IDisposable
	{
		private readonly TcpClient client;

		private readonly Timer timer;

		private int state;

		internal DeadlineGuard(TcpClient client, int milliseconds)
		{
			this.client = client;
			TimerCallback callback = delegate
			{
				if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
				{
					try
					{
						this.client.Close();
					}
					catch
					{
					}
				}
			};
			timer = new Timer(callback, null, milliseconds, -1);
		}

		public void Dispose()
		{
			Interlocked.CompareExchange(ref state, 1, 0);
			timer.Dispose();
		}
	}

	private sealed class RelayConnection
	{
		private readonly TcpClient client;

		private readonly TcpClient upstream;

		private readonly DateTime openedUtc = DateTime.UtcNow;

		private long fromBackend;

		private long fromClient;

		private long lastActivityTicks = DateTime.UtcNow.Ticks;

		private long lastInboundTicks = DateTime.UtcNow.Ticks;

		private int closed;

		private int backendEndedFirst = -1;

		private string backendEnd = "pending";

		private string clientEnd = "pending";

		private readonly string destination;

		internal readonly RelayTarget Target;

		internal bool ClosedLocally { get; private set; }

		internal TimeSpan Lifetime => DateTime.UtcNow - openedUtc;

		internal DateTime LastInboundUtc => new DateTime(Interlocked.Read(ref lastInboundTicks), DateTimeKind.Utc);

		internal bool ClosedByBackendWithoutData
		{
			get
			{
				if (backendEndedFirst == 1 && Interlocked.Read(ref fromBackend) == 0)
				{
					return DateTime.UtcNow - openedUtc < TimeSpan.FromSeconds(4.0);
				}
				return false;
			}
		}

		internal RelayConnection(TcpClient client, TcpClient upstream, RelayTarget target, string destination)
		{
			this.client = client;
			this.upstream = upstream;
			Target = target;
			this.destination = destination;
		}

		internal string Describe(string group)
		{
			string text = (ClosedLocally ? "trelay" : ((backendEndedFirst == 1) ? "upstream" : ((backendEndedFirst == 0) ? "app" : "?")));
			TimeSpan timeSpan = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);
			return "RELAY_CLOSED group=" + group + " via=" + Target.Name + " dst=" + destination + " closedBy=" + text + " upstreamEnd=" + backendEnd + " appEnd=" + clientEnd + " lifetime=" + (int)Lifetime.TotalSeconds + "s idleBeforeClose=" + (int)timeSpan.TotalSeconds + "s up=" + Interlocked.Read(ref fromClient) + " down=" + Interlocked.Read(ref fromBackend);
		}

		internal Task RunAsync()
		{
			Task task = PumpAsync(client, upstream, fromBackendSide: false);
			Task task2 = PumpAsync(upstream, client, fromBackendSide: true);
			return Task.WhenAll(task, task2);
		}

		private async Task PumpAsync(TcpClient from, TcpClient to, bool fromBackendSide)
		{
			byte[] buffer = new byte[32768];
			string operation = "read";
			try
			{
				NetworkStream source = from.GetStream();
				NetworkStream destination = to.GetStream();
				while (true)
				{
					int read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(continueOnCapturedContext: false);
					if (read <= 0)
					{
						SetEnd(fromBackendSide, "eof");
						break;
					}
					if (fromBackendSide)
					{
						Interlocked.Add(ref fromBackend, read);
					}
					else
					{
						Interlocked.Add(ref fromClient, read);
					}
					Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
					if (fromBackendSide)
					{
						Interlocked.Exchange(ref lastInboundTicks, DateTime.UtcNow.Ticks);
					}
					operation = "write";
					await destination.WriteAsync(buffer, 0, read).ConfigureAwait(continueOnCapturedContext: false);
					operation = "read";
				}
				Interlocked.CompareExchange(ref backendEndedFirst, fromBackendSide ? 1 : 0, -1);
				try
				{
					to.Client.Shutdown(SocketShutdown.Send);
				}
				catch
				{
					Close(abort: true);
				}
			}
			catch (Exception error)
			{
				SetEnd(fromBackendSide, operation + "-" + EndReason(error));
				Interlocked.CompareExchange(ref backendEndedFirst, fromBackendSide ? 1 : 0, -1);
				Close(abort: true);
			}
		}

		private void SetEnd(bool fromBackendSide, string reason)
		{
			if (fromBackendSide)
				backendEnd = reason;
			else
				clientEnd = reason;
		}

		private static string EndReason(Exception error)
		{
			SocketException socket = error as SocketException ?? (error as IOException)?.InnerException as SocketException;
			return socket != null ? "socket-" + (int)socket.SocketErrorCode : error.GetType().Name;
		}

		internal void Close(bool abort)
		{
			if (Interlocked.Exchange(ref closed, 1) != 0)
			{
				return;
			}
			if (backendEndedFirst == -1)
			{
				ClosedLocally = true;
			}
			if (abort)
			{
				try
				{
					client.Client.LingerState = new LingerOption(enable: true, 0);
				}
				catch
				{
				}
			}
			try
			{
				client.Close();
			}
			catch
			{
			}
			try
			{
				upstream.Close();
			}
			catch
			{
			}
		}
	}

	private sealed class UdpSession : IDisposable
	{
		private readonly RouteRelay owner;

		private readonly object gate = new object();

		private readonly Queue<byte[]> pending = new Queue<byte[]>();

		private TcpClient control;

		private UdpClient upstream;

		private IPEndPoint relayEndpoint;

		private bool binding;

		private bool disposed;

		private long lastActivityTicks = DateTime.UtcNow.Ticks;

		private long lastInboundTicks = DateTime.UtcNow.Ticks;

		internal readonly IPEndPoint Client;

		internal RelayTarget Target { get; private set; }

		internal DateTime LastActivityUtc => new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

		internal DateTime LastInboundUtc => new DateTime(Interlocked.Read(ref lastInboundTicks), DateTimeKind.Utc);

		internal UdpSession(RouteRelay owner, IPEndPoint client)
		{
			this.owner = owner;
			Client = client;
		}

		internal void Send(byte[] datagram)
		{
			Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
			UdpClient udpClient;
			IPEndPoint endPoint;
			lock (gate)
			{
				if (disposed)
				{
					return;
				}
				udpClient = upstream;
				endPoint = relayEndpoint;
				if (udpClient == null)
				{
					if (pending.Count < 256)
					{
						pending.Enqueue(datagram);
					}
					if (!binding)
					{
						binding = true;
						BindAsync();
					}
					return;
				}
			}
			try
			{
				udpClient.SendAsync(datagram, datagram.Length, endPoint).ContinueWith(Observe);
			}
			catch
			{
				Unbind(evacuated: false);
			}
		}

		private async void BindAsync()
		{
			try
			{
				foreach (RelayTarget target in (await owner.WaitForTargetsAsync().ConfigureAwait(continueOnCapturedContext: false)).Take(4))
				{
					IPEndPoint bound = null;
					byte[] request = new byte[10] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 };
					TcpClient tcp = await owner.OpenBackendAsync(target, request, (byte[] reply) => reply[1] == 0, delegate(byte[] reply)
					{
						bound = ParseBound(reply, target);
					}).ConfigureAwait(continueOnCapturedContext: false);
					if (tcp == null || bound == null)
					{
						tcp?.Close();
						continue;
					}
					UdpClient socket = new UdpClient(AddressFamily.InterNetwork);
					DisableUdpReset(socket);
					socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
					List<byte[]> flush;
					lock (gate)
					{
						if (disposed)
						{
							tcp.Close();
							socket.Close();
							return;
						}
						control = tcp;
						upstream = socket;
						relayEndpoint = bound;
						Target = target;
						binding = false;
						flush = pending.ToList();
						pending.Clear();
					}
					ReceiveLoop(socket, bound);
					WatchControl(tcp, socket);
					{
						foreach (byte[] item in flush)
						{
							try
							{
								_ = socket.SendAsync(item, item.Length, bound).ContinueWith(Observe);
							}
							catch
							{
							}
						}
						return;
					}
				}
			}
			catch
			{
			}
			lock (gate)
			{
				binding = false;
				pending.Clear();
			}
		}

		private static IPEndPoint ParseBound(byte[] reply, RelayTarget target)
		{
			IPAddress iPAddress;
			int num;
			if (reply[3] == 1)
			{
				iPAddress = new IPAddress(new byte[4]
				{
					reply[4],
					reply[5],
					reply[6],
					reply[7]
				});
				num = 8;
			}
			else
			{
				if (reply[3] != 4)
				{
					return null;
				}
				byte[] array = new byte[16];
				Buffer.BlockCopy(reply, 4, array, 0, 16);
				iPAddress = new IPAddress(array);
				num = 20;
			}
			int num2 = reply[num] * 256 + reply[num + 1];
			if (iPAddress.Equals(IPAddress.Any) || iPAddress.Equals(IPAddress.IPv6Any) || iPAddress.AddressFamily != AddressFamily.InterNetwork)
			{
				iPAddress = IPAddress.Loopback;
			}
			if (num2 == 0)
			{
				num2 = target.Port;
			}
			return new IPEndPoint(iPAddress, num2);
		}

		private async void ReceiveLoop(UdpClient socket, IPEndPoint expected)
		{
			int num = default(int);
			_ = num;
			_ = 0;
			while (true)
			{
				UdpReceiveResult result;
				try
				{
					result = await socket.ReceiveAsync().ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (ObjectDisposedException)
				{
					break;
				}
				catch (SocketException)
				{
					lock (gate)
					{
						if (disposed || upstream != socket)
						{
							break;
						}
					}
					continue;
				}
				catch (Exception)
				{
					break;
				}
				if (result.RemoteEndPoint.Equals(expected))
				{
					Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
					Interlocked.Exchange(ref lastInboundTicks, DateTime.UtcNow.Ticks);
					owner.SendToClient(Client, result.Buffer, result.Buffer.Length);
				}
			}
		}

		private async void WatchControl(TcpClient tcp, UdpClient socket)
		{
			int num = default(int);
			_ = num;
			_ = 0;
			try
			{
				byte[] discard = new byte[64];
				NetworkStream stream = tcp.GetStream();
				while (await stream.ReadAsync(discard, 0, discard.Length).ConfigureAwait(continueOnCapturedContext: false) > 0)
				{
				}
			}
			catch
			{
			}
			lock (gate)
			{
				if (disposed || upstream != socket)
				{
					return;
				}
			}
			Unbind(evacuated: false);
		}

		internal void Unbind(bool evacuated)
		{
			TcpClient tcpClient;
			UdpClient udpClient;
			lock (gate)
			{
				tcpClient = control;
				udpClient = upstream;
				control = null;
				upstream = null;
				relayEndpoint = null;
				if (evacuated)
				{
					Target = null;
				}
			}
			if (tcpClient != null)
			{
				try
				{
					tcpClient.Close();
				}
				catch
				{
				}
			}
			if (udpClient != null)
			{
				try
				{
					udpClient.Close();
				}
				catch
				{
				}
			}
		}

		public void Dispose()
		{
			lock (gate)
			{
				disposed = true;
				pending.Clear();
			}
			Unbind(evacuated: true);
		}
	}

	private const int SioUdpConnReset = -1744830452;

	private readonly Func<RelayTarget[]> selectTargets;

	private readonly Action<RelayTarget, RelayFailureKind> reportFailure;

	private readonly Action<string> log;

	private readonly object sync = new object();

	private readonly HashSet<RelayConnection> connections = new HashSet<RelayConnection>();

	private readonly HashSet<TcpClient> controls = new HashSet<TcpClient>();

	private readonly Dictionary<IPEndPoint, UdpSession> sessions = new Dictionary<IPEndPoint, UdpSession>();

	private readonly List<string> recentDestinations = new List<string>();

	private readonly TcpListener listener;

	private readonly UdpClient udp;

	private readonly Timer sweeper;

	private int associations;

	private volatile bool disposed;

	internal readonly string Name;

	internal int Port { get; private set; }

	internal int TotalConnections
	{
		get
		{
			lock (sync)
			{
				return connections.Count + sessions.Count;
			}
		}
	}

	internal RouteRelay(string name, int port, Func<RelayTarget[]> selectTargets, Action<RelayTarget, RelayFailureKind> reportFailure, Action<string> log)
	{
		if (selectTargets == null)
		{
			throw new ArgumentNullException("selectTargets");
		}
		Name = name ?? "";
		this.selectTargets = selectTargets;
		this.reportFailure = reportFailure ?? ((Action<RelayTarget, RelayFailureKind>)delegate
		{
		});
		this.log = log ?? ((Action<string>)delegate
		{
		});
		listener = new TcpListener(IPAddress.Loopback, port);
		try
		{
			listener.Server.ExclusiveAddressUse = true;
		}
		catch (SocketException)
		{
		}
		catch (PlatformNotSupportedException)
		{
		}
		listener.Start(512);
		try
		{
			Port = ((IPEndPoint)listener.LocalEndpoint).Port;
			udp = new UdpClient(AddressFamily.InterNetwork);
			try
			{
				udp.Client.ExclusiveAddressUse = true;
			}
			catch (SocketException)
			{
			}
			catch (PlatformNotSupportedException)
			{
			}
			DisableUdpReset(udp);
			udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, Port));
		}
		catch
		{
			listener.Stop();
			if (udp != null)
			{
				udp.Close();
			}
			throw;
		}
		sweeper = new Timer(delegate
		{
			Sweep();
		}, null, 30000, 30000);
		Task.Run((Func<Task>)AcceptLoopAsync);
		Task.Run((Func<Task>)UdpLoopAsync);
	}

	internal int ConnectionCount(RelayTarget target)
	{
		lock (sync)
		{
			return connections.Count((RelayConnection c) => c.Target == target) + sessions.Values.Count((UdpSession s) => s.Target == target);
		}
	}

	internal List<string> RecentDestinations()
	{
		lock (sync)
		{
			return new List<string>(recentDestinations);
		}
	}

	private void RememberDestination(string destination)
	{
		if (string.IsNullOrEmpty(destination) || destination == "?" || destination.Length > 253)
		{
			return;
		}
		lock (sync)
		{
			recentDestinations.Remove(destination);
			recentDestinations.Insert(0, destination);
			while (recentDestinations.Count > 8)
			{
				recentDestinations.RemoveAt(recentDestinations.Count - 1);
			}
		}
	}

	internal TimeSpan IdleFor(RelayTarget target)
	{
		DateTime dateTime = DateTime.MinValue;
		lock (sync)
		{
			foreach (RelayConnection connection in connections)
			{
				if (connection.Target == target && connection.LastInboundUtc > dateTime)
				{
					dateTime = connection.LastInboundUtc;
				}
			}
			foreach (UdpSession value in sessions.Values)
			{
				if (value.Target == target && value.LastInboundUtc > dateTime)
				{
					dateTime = value.LastInboundUtc;
				}
			}
		}
		if (dateTime == DateTime.MinValue)
		{
			return TimeSpan.MaxValue;
		}
		TimeSpan timeSpan = DateTime.UtcNow - dateTime;
		if (!(timeSpan < TimeSpan.Zero))
		{
			return timeSpan;
		}
		return TimeSpan.Zero;
	}

	internal int Evacuate(RelayTarget target)
	{
		List<RelayConnection> list;
		List<UdpSession> list2;
		lock (sync)
		{
			list = connections.Where((RelayConnection c) => c.Target == target).ToList();
			list2 = sessions.Values.Where((UdpSession s) => s.Target == target).ToList();
		}
		foreach (RelayConnection item in list)
		{
			item.Close(abort: true);
		}
		foreach (UdpSession item2 in list2)
		{
			item2.Unbind(evacuated: true);
		}
		return list.Count + list2.Count;
	}

	private static void DisableUdpReset(UdpClient client)
	{
		try
		{
			client.Client.IOControl(-1744830452, new byte[4], null);
		}
		catch
		{
		}
	}

	private async Task AcceptLoopAsync()
	{
		while (!disposed)
		{
			TcpClient client = null;
			bool pause = false;
			try
			{
				client = await listener.AcceptTcpClientAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (InvalidOperationException)
			{
				break;
			}
			catch (SocketException)
			{
				if (disposed)
				{
					break;
				}
				pause = true;
			}
			catch (Exception ex4)
			{
				log("RELAY_ACCEPT_ERROR port=" + Port + " " + ex4.GetType().Name);
				if (disposed)
				{
					break;
				}
				pause = true;
			}
			if (pause || client == null)
			{
				await Task.Delay(20).ConfigureAwait(continueOnCapturedContext: false);
				continue;
			}
			if (disposed)
			{
				client.Close();
				break;
			}
			HandleClientSafe(client);
		}
	}

	private async void HandleClientSafe(TcpClient client)
	{
		int num = default(int);
		_ = num;
		_ = 0;
		try
		{
			_ = num;
			_ = 0;
			try
			{
				await HandleClientAsync(client).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch
			{
			}
		}
		finally
		{
			try
			{
				client.Close();
			}
			catch
			{
			}
		}
	}

	private async Task HandleClientAsync(TcpClient client)
	{
		client.NoDelay = true;
		NetworkStream stream = client.GetStream();
		byte[] request;
		using (Deadline(client, 15000))
		{
			byte[] greeting = await ReadExactAsync(stream, 2).ConfigureAwait(continueOnCapturedContext: false);
			if (greeting[0] != 5 || greeting[1] == 0)
			{
				return;
			}
			if (Array.IndexOf(await ReadExactAsync(stream, greeting[1]).ConfigureAwait(continueOnCapturedContext: false), (byte)0) < 0)
			{
				await stream.WriteAsync(new byte[2] { 5, 255 }, 0, 2).ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			await stream.WriteAsync(new byte[2] { 5, 0 }, 0, 2).ConfigureAwait(continueOnCapturedContext: false);
			request = await ReadRequestAsync(stream).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (request != null)
		{
			if (request[1] == 1)
			{
				await ConnectAsync(client, stream, request).ConfigureAwait(continueOnCapturedContext: false);
			}
			else if (request[1] == 3)
			{
				await AssociateAsync(client, stream).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				await stream.WriteAsync(Reply(7), 0, 10).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private static byte[] Reply(byte code)
	{
		return new byte[10] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 };
	}

	private static string DescribeDestination(byte[] request)
	{
		try
		{
			int num = request[request.Length - 2] * 256 + request[request.Length - 1];
			string text;
			if (request[3] == 1)
			{
				text = new IPAddress(new byte[4]
				{
					request[4],
					request[5],
					request[6],
					request[7]
				}).ToString();
			}
			else if (request[3] == 4)
			{
				byte[] array = new byte[16];
				Buffer.BlockCopy(request, 4, array, 0, 16);
				text = string.Concat("[", new IPAddress(array), "]");
			}
			else
			{
				text = Encoding.ASCII.GetString(request, 5, request[4]);
			}
			return text + ":" + num;
		}
		catch
		{
			return "?";
		}
	}

	private static async Task<byte[]> ReadRequestAsync(Stream stream)
	{
		byte[] head = await ReadExactAsync(stream, 4).ConfigureAwait(continueOnCapturedContext: false);
		if (head[0] != 5 || head[2] != 0)
		{
			return null;
		}
		byte[] address;
		if (head[3] == 1)
		{
			address = await ReadExactAsync(stream, 4).ConfigureAwait(continueOnCapturedContext: false);
		}
		else if (head[3] == 4)
		{
			address = await ReadExactAsync(stream, 16).ConfigureAwait(continueOnCapturedContext: false);
		}
		else
		{
			if (head[3] != 3)
			{
				return null;
			}
			byte[] length = await ReadExactAsync(stream, 1).ConfigureAwait(continueOnCapturedContext: false);
			if (length[0] == 0)
			{
				return null;
			}
			byte[] name = await ReadExactAsync(stream, length[0]).ConfigureAwait(continueOnCapturedContext: false);
			address = new byte[1 + name.Length];
			address[0] = length[0];
			Buffer.BlockCopy(name, 0, address, 1, name.Length);
		}
		byte[] port = await ReadExactAsync(stream, 2).ConfigureAwait(continueOnCapturedContext: false);
		byte[] request = new byte[4 + address.Length + 2];
		Buffer.BlockCopy(head, 0, request, 0, 4);
		Buffer.BlockCopy(address, 0, request, 4, address.Length);
		Buffer.BlockCopy(port, 0, request, 4 + address.Length, 2);
		return request;
	}

	private static async Task<byte[]> ReadReplyAsync(Stream stream)
	{
		byte[] head = await ReadExactAsync(stream, 4).ConfigureAwait(continueOnCapturedContext: false);
		if (head[0] != 5)
		{
			throw new IOException("bad SOCKS reply");
		}
		byte[] nameLength = null;
		int addressLength;
		if (head[3] == 1)
		{
			addressLength = 4;
		}
		else if (head[3] == 4)
		{
			addressLength = 16;
		}
		else
		{
			if (head[3] != 3)
			{
				throw new IOException("bad SOCKS reply address");
			}
			nameLength = await ReadExactAsync(stream, 1).ConfigureAwait(continueOnCapturedContext: false);
			addressLength = nameLength[0];
		}
		byte[] rest = await ReadExactAsync(stream, addressLength + 2).ConfigureAwait(continueOnCapturedContext: false);
		byte[] reply = new byte[4 + ((nameLength != null) ? 1 : 0) + rest.Length];
		Buffer.BlockCopy(head, 0, reply, 0, 4);
		int offset = 4;
		if (nameLength != null)
		{
			reply[offset++] = nameLength[0];
		}
		Buffer.BlockCopy(rest, 0, reply, offset, rest.Length);
		return reply;
	}

	private async Task<RelayTarget[]> WaitForTargetsAsync()
	{
		Stopwatch clock = Stopwatch.StartNew();
		while (!disposed)
		{
			RelayTarget[] targets;
			try
			{
				targets = selectTargets() ?? new RelayTarget[0];
			}
			catch
			{
				targets = new RelayTarget[0];
			}
			if (targets.Length > 0 || clock.ElapsedMilliseconds > 12000)
			{
				return targets;
			}
			await Task.Delay(100).ConfigureAwait(continueOnCapturedContext: false);
		}
		return new RelayTarget[0];
	}

	private async Task<TcpClient> OpenBackendAsync(RelayTarget target, byte[] request, Func<byte[], bool> acceptReply, Action<byte[]> onReply)
	{
		TcpClient upstream = new TcpClient(AddressFamily.InterNetwork)
		{
			NoDelay = true
		};
		bool connected = false;
		try
		{
			Task connect = upstream.ConnectAsync(IPAddress.Loopback, target.Port);
			if (await Task.WhenAny(connect, Task.Delay(1500)).ConfigureAwait(continueOnCapturedContext: false) != connect || connect.IsFaulted || !upstream.Connected)
			{
				Observe(connect);
				reportFailure(target, RelayFailureKind.LocalConnect);
				return null;
			}
			connected = true;
			NetworkStream stream = upstream.GetStream();
			using (Deadline(upstream, 5000))
			{
				await stream.WriteAsync(new byte[3] { 5, 1, 0 }, 0, 3).ConfigureAwait(continueOnCapturedContext: false);
				byte[] hello = await ReadExactAsync(stream, 2).ConfigureAwait(continueOnCapturedContext: false);
				if (hello[0] != 5 || hello[1] != 0)
				{
					throw new IOException("backend refused SOCKS");
				}
				await stream.WriteAsync(request, 0, request.Length).ConfigureAwait(continueOnCapturedContext: false);
				byte[] reply = await ReadReplyAsync(stream).ConfigureAwait(continueOnCapturedContext: false);
				if (!acceptReply(reply))
				{
					onReply(reply);
					upstream.Close();
					return null;
				}
				onReply(reply);
			}
			TcpClient result = upstream;
			upstream = null;
			return result;
		}
		catch (Exception)
		{
			if (connected)
			{
				reportFailure(target, RelayFailureKind.Handshake);
			}
			return null;
		}
		finally
		{
			upstream?.Close();
		}
	}

	private async Task ConnectAsync(TcpClient client, NetworkStream stream, byte[] request)
	{
		RelayTarget[] targets = await WaitForTargetsAsync().ConfigureAwait(continueOnCapturedContext: false);
		byte[] backendReply = null;
		foreach (RelayTarget target in targets.Take(4))
		{
			if (disposed)
			{
				return;
			}
			TcpClient upstream = await OpenBackendAsync(target, request, (byte[] reply) => reply[1] == 0, delegate(byte[] reply)
			{
				backendReply = reply;
			}).ConfigureAwait(continueOnCapturedContext: false);
			if (upstream == null)
			{
				if (backendReply != null)
				{
					break;
				}
				continue;
			}
			string destination = DescribeDestination(request);
			RememberDestination(destination);
			RelayConnection connection = new RelayConnection(client, upstream, target, destination);
			lock (sync)
			{
				if (disposed)
				{
					upstream.Close();
					return;
				}
				connections.Add(connection);
			}
			try
			{
				await stream.WriteAsync(backendReply, 0, backendReply.Length).ConfigureAwait(continueOnCapturedContext: false);
				await connection.RunAsync().ConfigureAwait(continueOnCapturedContext: false);
				return;
			}
			finally
			{
				lock (sync)
				{
					connections.Remove(connection);
				}
				connection.Close(abort: false);
				if (connection.ClosedByBackendWithoutData && !connection.ClosedLocally)
				{
					reportFailure(target, RelayFailureKind.NoData);
				}
				if (connection.Lifetime >= TimeSpan.FromSeconds(60.0))
				{
					log(connection.Describe(Name));
				}
			}
		}
		byte[] failure = backendReply ?? Reply(1);
		try
		{
			await stream.WriteAsync(failure, 0, failure.Length).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
		}
	}

	private async Task AssociateAsync(TcpClient client, NetworkStream stream)
	{
		byte[] reply = Reply(0);
		reply[4] = 127;
		reply[7] = 1;
		reply[8] = (byte)(Port >> 8);
		reply[9] = (byte)Port;
		lock (sync)
		{
			if (disposed)
			{
				return;
			}
			controls.Add(client);
			associations++;
		}
		try
		{
			await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(continueOnCapturedContext: false);
			byte[] discard = new byte[512];
			while (await stream.ReadAsync(discard, 0, discard.Length).ConfigureAwait(continueOnCapturedContext: false) > 0)
			{
			}
		}
		catch
		{
		}
		finally
		{
			List<UdpSession> list = null;
			lock (sync)
			{
				controls.Remove(client);
				if (--associations <= 0)
				{
					associations = 0;
					list = sessions.Values.ToList();
					sessions.Clear();
				}
			}
			if (list != null)
			{
				foreach (UdpSession item in list)
				{
					item.Dispose();
				}
			}
		}
	}

	private async Task UdpLoopAsync()
	{
		while (!disposed)
		{
			UdpReceiveResult datagram;
			try
			{
				datagram = await udp.ReceiveAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (SocketException)
			{
				if (disposed)
				{
					break;
				}
				continue;
			}
			catch (Exception)
			{
				if (disposed)
				{
					break;
				}
				continue;
			}
			byte[] buffer = datagram.Buffer;
			if (!IPAddress.IsLoopback(datagram.RemoteEndPoint.Address) || buffer.Length < 10 || buffer[0] != 0 || buffer[1] != 0 || buffer[2] != 0)
			{
				continue;
			}
			UdpSession session;
			lock (sync)
			{
				if (!disposed && associations != 0)
				{
					if (sessions.TryGetValue(datagram.RemoteEndPoint, out session))
					{
						goto IL_0219;
					}
					if (sessions.Count < 4096)
					{
						session = new UdpSession(this, datagram.RemoteEndPoint);
						sessions.Add(datagram.RemoteEndPoint, session);
						goto IL_0219;
					}
				}
			}
			continue;
			IL_0219:
			session.Send(buffer);
		}
	}

	private void Sweep()
	{
		List<UdpSession> list;
		lock (sync)
		{
			if (disposed)
			{
				return;
			}
			DateTime limit = DateTime.UtcNow.AddMinutes(-3.0);
			list = sessions.Values.Where((UdpSession s) => s.LastActivityUtc < limit).ToList();
			foreach (UdpSession item in list)
			{
				sessions.Remove(item.Client);
			}
		}
		foreach (UdpSession item2 in list)
		{
			item2.Dispose();
		}
	}

	private void SendToClient(IPEndPoint client, byte[] datagram, int length)
	{
		try
		{
			udp.SendAsync(datagram, length, client).ContinueWith(Observe);
		}
		catch
		{
		}
	}

	private static void Observe(Task task)
	{
		if (task != null && task.IsFaulted)
		{
			_ = task.Exception;
		}
	}

	private static IDisposable Deadline(TcpClient client, int milliseconds)
	{
		return new DeadlineGuard(client, milliseconds);
	}

	internal static async Task<byte[]> ReadExactAsync(Stream stream, int count)
	{
		byte[] data = new byte[count];
		int read;
		for (int offset = 0; offset < count; offset += read)
		{
			read = await stream.ReadAsync(data, offset, count - offset).ConfigureAwait(continueOnCapturedContext: false);
			if (read <= 0)
			{
				throw new EndOfStreamException();
			}
		}
		return data;
	}

	public void Dispose()
	{
		List<RelayConnection> list;
		List<UdpSession> list2;
		List<TcpClient> list3;
		lock (sync)
		{
			if (disposed)
			{
				return;
			}
			disposed = true;
			list = connections.ToList();
			connections.Clear();
			list2 = sessions.Values.ToList();
			sessions.Clear();
			list3 = controls.ToList();
			controls.Clear();
		}
		try
		{
			listener.Stop();
		}
		catch
		{
		}
		try
		{
			udp.Close();
		}
		catch
		{
		}
		sweeper.Dispose();
		foreach (RelayConnection item in list)
		{
			item.Close(abort: true);
		}
		foreach (UdpSession item2 in list2)
		{
			item2.Dispose();
		}
		foreach (TcpClient item3 in list3)
		{
			try
			{
				item3.Close();
			}
			catch
			{
			}
		}
	}
}
