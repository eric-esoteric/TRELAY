using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TRELAY;

internal sealed class VpsProvisioner
{
	private readonly JavaScriptSerializer json = new JavaScriptSerializer();

	public string DiscoverFingerprint(string host, int sshPort, string user)
	{
		ValidateInput(host, sshPort, user, "handshake-only");
		string fingerprint = null;
		Action<string> observed = delegate(string f)
		{
			fingerprint = f;
		};
		using (SshClient sshClient = CreateClient(host, sshPort, user, "", null, observed))
		{
			try
			{
				sshClient.Connect();
			}
			catch
			{
				if (string.IsNullOrWhiteSpace(fingerprint))
				{
					throw;
				}
			}
		}
		if (string.IsNullOrWhiteSpace(fingerprint))
		{
			throw new InvalidOperationException("SSH-сервер не прислал отпечаток.");
		}
		return fingerprint;
	}

	public VpsAudit Audit(string host, int sshPort, string user, string password, string expectedFingerprint)
	{
		ValidateInput(host, sshPort, user, password);
		RequireFingerprint(expectedFingerprint);
		string fingerprint = null;
		Action<string> observed = delegate(string f)
		{
			fingerprint = f;
		};
		using SshClient sshClient = CreateClient(host, sshPort, user, password, expectedFingerprint, observed);
		sshClient.Connect();
		string text = Run(sshClient, "printf '%s' \"$(. /etc/os-release 2>/dev/null; echo ${PRETTY_NAME:-Linux})\"");
		string text2 = Run(sshClient, "docker --version 2>/dev/null || true");
		string text3 = Run(sshClient, "docker inspect -f '{{.Config.Image}}' amnezia-xray 2>/dev/null || docker images --format '{{.Repository}}:{{.Tag}}' | grep -i xray | head -n1 || true");
		sshClient.Disconnect();
		if (string.IsNullOrWhiteSpace(text2))
		{
			throw new InvalidOperationException("На VPS не найден Docker. Автонастройка пока требует Docker.");
		}
		if (string.IsNullOrWhiteSpace(text3))
		{
			throw new InvalidOperationException("На VPS не найден Docker-образ Xray. Сначала установи Xray/Amnezia Xray.");
		}
		VpsAudit vpsAudit = new VpsAudit();
		vpsAudit.Fingerprint = fingerprint;
		vpsAudit.Summary = text.Trim() + Environment.NewLine + text2.Trim() + Environment.NewLine + "Xray image: " + text3.Trim();
		return vpsAudit;
	}

	public VpsProvisionResult Provision(string host, int sshPort, string user, string password, int tunnelPort, string expectedFingerprint)
	{
		return ProvisionWithRecovery(host, sshPort, user, password, tunnelPort, expectedFingerprint, null);
	}

	public VpsProvisionResult ProvisionWithRecovery(string host, int sshPort, string user, string password, int tunnelPort, string expectedFingerprint, Action<string> preserveKey)
	{
		ValidateInput(host, sshPort, user, password);
		RequireFingerprint(expectedFingerprint);
		if (tunnelPort < 1025 || tunnelPort > 65535)
		{
			throw new ArgumentOutOfRangeException("tunnelPort", "Порт туннеля должен быть от 1025 до 65535.");
		}
		byte[] array = new byte[16];
		using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
		{
			randomNumberGenerator.GetBytes(array);
		}
		string text = Convert.ToBase64String(array);
		Array.Clear(array, 0, array.Length);
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("log", new Dictionary<string, object> { { "loglevel", "warning" } });
		dictionary.Add("policy", new Dictionary<string, object> { 
		{
			"levels",
			new Dictionary<string, object> { 
			{
				"0",
				new Dictionary<string, object> { { "connIdle", 86400 } }
			} }
		} });
		dictionary.Add("inbounds", new object[1]
		{
			new Dictionary<string, object>
			{
				{ "listen", "0.0.0.0" },
				{ "port", tunnelPort },
				{ "protocol", "shadowsocks" },
				{
					"settings",
					new Dictionary<string, object>
					{
						{ "method", "2022-blake3-aes-128-gcm" },
						{ "password", text },
						{ "network", "tcp,udp" }
					}
				}
			}
		});
		dictionary.Add("outbounds", new object[1]
		{
			new Dictionary<string, object>
			{
				{ "protocol", "freedom" },
				{ "tag", "direct" },
				{
					"settings",
					new Dictionary<string, object> { { "domainStrategy", "UseIPv4" } }
				}
			}
		});
		Dictionary<string, object> obj = dictionary;
		string encodedConfig = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Serialize(obj)));
		string s = "2022-blake3-aes-128-gcm:" + text;
		string text2 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-')
			.Replace('/', '_');
		string text3 = (host.Contains(":") ? ("[" + host + "]") : host);
		string text4 = "ss://" + text2 + "@" + text3 + ":" + tunnelPort + "#TRELAY-Telephony";
		preserveKey?.Invoke(text4);
		string fingerprint = null;
		SshClient client = CreateClient(host, sshPort, user, password, expectedFingerprint, delegate(string f)
		{
			fingerprint = f;
		});
		try
		{
			client.Connect();
			string text5 = Run(client, "docker inspect -f '{{index .Config.Labels \"com.trelay.managed\"}}' trelay-telephony 2>/dev/null || true").Trim();
			if (!string.IsNullOrWhiteSpace(text5) && !text5.Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Контейнер с именем trelay-telephony уже существует и создан не TRELAY.");
			}
			bool flag = text5.Equals("true", StringComparison.OrdinalIgnoreCase);
			if (!flag)
			{
				RunChecked(client, "test ! -e /opt/trelay/server.json", "На VPS уже есть конфиг без управляемого контейнера. Он сохранён; сначала проверь предыдущую установку.");
				string text6 = Run(client, "ss -H -lntu 2>/dev/null | grep -Eq '[:.]" + tunnelPort + "[[:space:]]' && printf occupied || true");
				if (text6.Trim() == "occupied")
				{
					throw new InvalidOperationException("Порт " + tunnelPort + " уже занят на VPS. Выбери другой.");
				}
			}
			string text7 = Run(client, "docker inspect -f '{{.Config.Image}}' amnezia-xray 2>/dev/null || docker images --format '{{.Repository}}:{{.Tag}}' | grep -i xray | head -n1").Trim();
			if (text7.Length == 0)
			{
				throw new InvalidOperationException("Не найден локальный Docker-образ Xray.");
			}
			ExecuteProvisionTransaction(flag, encodedConfig, tunnelPort, text7, delegate(string command, string message)
			{
				if (!client.IsConnected)
				{
					client.Connect();
				}
				RunChecked(client, command, message);
			});
			client.Disconnect();
		}
		finally
		{
			if (client != null)
			{
				((IDisposable)client).Dispose();
			}
		}
		VpsProvisionResult vpsProvisionResult = new VpsProvisionResult();
		vpsProvisionResult.ShareKey = text4;
		vpsProvisionResult.Fingerprint = fingerprint;
		return vpsProvisionResult;
	}

	public string Diagnose(string host, int sshPort, string user, string password, string expectedFingerprint)
	{
		ValidateInput(host, sshPort, user, password);
		RequireFingerprint(expectedFingerprint);
		string fingerprint = null;
		Action<string> observed = delegate(string f)
		{
			fingerprint = f;
		};
		using SshClient sshClient = CreateClient(host, sshPort, user, password, expectedFingerprint, observed);
		sshClient.Connect();
		string[] array = new string[11]
		{
			"docker ps -a --filter name=trelay-telephony --format '{{.Names}}|{{.Status}}|{{.Ports}}'", "docker logs --tail 30 trelay-telephony 2>&1 || true", "docker inspect -f '{{.HostConfig.NetworkMode}}|{{range .NetworkSettings.Networks}}{{.IPAddress}}|{{.Gateway}}{{end}}' trelay-telephony", "docker exec trelay-telephony /bin/sh -c 'command -v wget >/dev/null && wget -qO- -T 4 https://api.ipify.org || true' 2>&1", "ss -lntup", "ufw status", "curl -4 --max-time 4 -sS https://api.ipify.org || true", "ip rule show; ip route show table all",
			"nft list ruleset 2>&1 | sed -n '1,260p'", "iptables -S; iptables -t nat -S", "for target in 1.1.1.1:443 8.8.8.8:443; do ip=${target%:*}; port=${target#*:}; timeout 5 bash -c \"</dev/tcp/$ip/$port\" >/dev/null 2>&1 && echo \"$target OPEN\" || echo \"$target TIMEOUT\"; done"
		};
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("Fingerprint=" + fingerprint);
		string[] array2 = array;
		foreach (string text in array2)
		{
			stringBuilder.AppendLine("--- " + text);
			stringBuilder.AppendLine(Run(sshClient, text));
		}
		sshClient.Disconnect();
		return stringBuilder.ToString();
	}

	public string ConfigureTelephonyUpstream(string host, int sshPort, string user, string password, string expectedFingerprint, string address, int port, string clientId, string publicKey, string serverName, string fingerprint)
	{
		ValidateInput(host, sshPort, user, password);
		RequireFingerprint(expectedFingerprint);
		if (string.IsNullOrWhiteSpace(address) || !Regex.IsMatch(address, "^[a-zA-Z0-9.-]+$"))
		{
			throw new ArgumentException("Некорректный адрес upstream-сервера.");
		}
		if (port < 1 || port > 65535)
		{
			throw new ArgumentOutOfRangeException("port");
		}
		if (!Guid.TryParse(clientId, out var _))
		{
			throw new ArgumentException("Некорректный VLESS ID.");
		}
		if (string.IsNullOrWhiteSpace(publicKey) || !Regex.IsMatch(publicKey, "^[A-Za-z0-9_-]{40,64}$"))
		{
			throw new ArgumentException("Некорректный Reality public key.");
		}
		if (string.IsNullOrWhiteSpace(serverName) || !Regex.IsMatch(serverName, "^[a-zA-Z0-9.-]+$"))
		{
			throw new ArgumentException("Некорректный Reality server name.");
		}
		if (string.IsNullOrWhiteSpace(fingerprint) || !Regex.IsMatch(fingerprint, "^[a-zA-Z0-9_-]+$"))
		{
			fingerprint = "chrome";
		}
		string observed = null;
		using (SshClient sshClient = CreateClient(host, sshPort, user, password, expectedFingerprint, delegate(string f)
		{
			observed = f;
		}))
		{
			sshClient.Connect();
			RunChecked(sshClient, "test \"$(docker inspect -f '{{index .Config.Labels \"com.trelay.managed\"}}' trelay-telephony 2>/dev/null)\" = true", "Контейнер TRELAY не найден или принадлежит другому приложению. Изменения не выполнялись.");
			string text = Run(sshClient, "base64 -w0 /opt/trelay/server.json").Trim();
			if (text.Length == 0)
			{
				throw new InvalidOperationException("На VPS не найден действующий конфиг TRELAY.");
			}
			if (!(json.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(text))) is Dictionary<string, object> dictionary) || !dictionary.ContainsKey("inbounds"))
			{
				throw new InvalidOperationException("Действующий конфиг TRELAY повреждён.");
			}
			Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
			dictionary2.Add("protocol", "vless");
			dictionary2.Add("tag", "telephony-upstream");
			dictionary2.Add("settings", new Dictionary<string, object>
			{
				{ "packetEncoding", "xudp" },
				{
					"vnext",
					new object[1]
					{
						new Dictionary<string, object>
						{
							{ "address", address },
							{ "port", port },
							{
								"users",
								new object[1]
								{
									new Dictionary<string, object>
									{
										{ "id", clientId },
										{ "encryption", "none" },
										{ "flow", "xtls-rprx-vision" }
									}
								}
							}
						}
					}
				}
			});
			dictionary2.Add("streamSettings", new Dictionary<string, object>
			{
				{ "network", "tcp" },
				{ "security", "reality" },
				{
					"realitySettings",
					new Dictionary<string, object>
					{
						{ "fingerprint", fingerprint },
						{ "publicKey", publicKey },
						{ "serverName", serverName }
					}
				}
			});
			Dictionary<string, object> dictionary3 = dictionary2;
			Dictionary<string, object> dictionary4 = new Dictionary<string, object>();
			dictionary4.Add("protocol", "freedom");
			dictionary4.Add("tag", "direct");
			dictionary4.Add("settings", new Dictionary<string, object> { { "domainStrategy", "UseIPv4" } });
			Dictionary<string, object> dictionary5 = dictionary4;
			dictionary["outbounds"] = new object[2] { dictionary5, dictionary3 };
			dictionary["routing"] = new Dictionary<string, object>
			{
				{ "domainStrategy", "IPIfNonMatch" },
				{
					"rules",
					new object[2]
					{
						new Dictionary<string, object>
						{
							{ "type", "field" },
							{
								"domain",
								new object[2] { "domain:megapbx.ru", "domain:megafon.ru" }
							},
							{ "outboundTag", "telephony-upstream" }
						},
						new Dictionary<string, object>
						{
							{ "type", "field" },
							{
								"ip",
								new object[1] { "193.201.230.128/26" }
							},
							{ "outboundTag", "telephony-upstream" }
						}
					}
				}
			};
			string text2 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Serialize(dictionary)));
			string value = Run(sshClient, "docker inspect -f '{{.Config.Image}}' trelay-telephony").Trim();
			RunChecked(sshClient, "umask 077; printf '%s' '" + text2 + "' | base64 -d > /opt/trelay/server.json.new && chmod 600 /opt/trelay/server.json.new", "Не удалось записать новый конфиг.");
			RunChecked(sshClient, "docker run --rm --entrypoint xray -v /opt/trelay/server.json.new:/etc/xray/config.json:ro '" + ShellQuote(value) + "' run -test -config /etc/xray/config.json", "Xray отклонил upstream-конфиг.");
			RunChecked(sshClient, "cp -p /opt/trelay/server.json /opt/trelay/server.json.rollback", "Не удалось создать копию upstream-конфига.");
			try
			{
				RunChecked(sshClient, "mv /opt/trelay/server.json.new /opt/trelay/server.json", "Не удалось активировать upstream-конфиг.");
				RunChecked(sshClient, "docker restart trelay-telephony >/dev/null && sleep 2 && test \"$(docker inspect -f '{{.State.Running}}' trelay-telephony)\" = true", "Контейнер TRELAY не поднялся после изменения.");
			}
			catch (Exception innerException)
			{
				try
				{
					if (!sshClient.IsConnected)
					{
						sshClient.Connect();
					}
					RunChecked(sshClient, "cp -p /opt/trelay/server.json.rollback /opt/trelay/server.json && docker restart --time 10 trelay-telephony >/dev/null && test \"$(docker inspect -f '{{.State.Running}}' trelay-telephony)\" = true", "Не удалось подтвердить откат upstream-конфига.");
				}
				catch
				{
					throw new InvalidOperationException("Изменение upstream прервано; автоматический откат VPS не подтверждён. Резервная копия сохранена в server.json.rollback.", innerException);
				}
				throw;
			}
			sshClient.Disconnect();
		}
		return "MegaFon направлен через отдельный VLESS Reality upstream; остальной трафик остаётся прямым. Fingerprint=" + observed;
	}

	internal static void ExecuteProvisionTransaction(bool managedExists, string encodedConfig, int tunnelPort, string image, Action<string, string> execute)
	{
		string text = "trelay-telephony-rollback-" + Guid.NewGuid().ToString("N");
		bool flag = false;
		bool flag2 = false;
		try
		{
			execute("umask 077; install -d -m 700 /opt/trelay && printf '%s' '" + encodedConfig + "' | base64 -d > /opt/trelay/server.json.new && chmod 600 /opt/trelay/server.json.new", "Не удалось записать временный конфиг.");
			execute("docker run --rm --entrypoint xray -v /opt/trelay/server.json.new:/etc/xray/config.json:ro '" + ShellQuote(image) + "' run -test -config /etc/xray/config.json", "Xray отклонил конфигурацию.");
			if (managedExists)
			{
				execute("cp -p /opt/trelay/server.json /opt/trelay/server.json.bak", "Не удалось создать резервную копию.");
				flag = true;
			}
			flag2 = true;
			if (managedExists)
			{
				execute("docker rename trelay-telephony " + text + " && docker stop --time 10 " + text + " >/dev/null", "Не удалось остановить предыдущий контейнер.");
			}
			execute("mv /opt/trelay/server.json.new /opt/trelay/server.json", "Не удалось активировать конфиг.");
			execute("docker run -d --name trelay-telephony --label com.trelay.managed=true --restart unless-stopped -p " + tunnelPort + ":" + tunnelPort + "/tcp -p " + tunnelPort + ":" + tunnelPort + "/udp -v /opt/trelay/server.json:/etc/xray/config.json:ro --entrypoint xray '" + ShellQuote(image) + "' run -config /etc/xray/config.json", "Не удалось запустить контейнер TRELAY.");
			execute("sleep 2; test \"$(docker inspect -f '{{.State.Running}}' trelay-telephony 2>/dev/null)\" = true", "Контейнер запустился и сразу остановился.");
		}
		catch (Exception innerException)
		{
			if (flag2)
			{
				try
				{
					execute(BuildRollbackCommand(managedExists && flag, text), "Не удалось подтвердить откат VPS.");
				}
				catch
				{
					throw new InvalidOperationException("Настройка VPS прервана; автоматический откат не подтверждён. Сохранившийся контейнер отката: " + text + ". Проверь VPS перед повторной настройкой.", innerException);
				}
			}
			throw;
		}
		finally
		{
			try
			{
				execute("rm -f /opt/trelay/server.json.new", "Не удалось удалить временный конфиг.");
			}
			catch
			{
			}
		}
		if (managedExists)
		{
			try
			{
				execute("docker rm " + text + " >/dev/null 2>&1", "Старый контейнер оставлен для отката.");
			}
			catch
			{
			}
		}
	}

	private static string BuildRollbackCommand(bool managedExists, string rollbackName)
	{
		string text = "\"$(docker inspect -f '{{index .Config.Labels \"com.trelay.managed\"}}' trelay-telephony 2>/dev/null)\" = true";
		if (!managedExists)
		{
			return "set -e; if test " + text + "; then docker rm -f trelay-telephony >/dev/null; fi; rm -f /opt/trelay/server.json";
		}
		string text2 = "\"$(docker inspect -f '{{index .Config.Labels \"com.trelay.managed\"}}' " + rollbackName + " 2>/dev/null)\" = true";
		return "set -e; if test " + text2 + "; then if test " + text + "; then docker rm -f trelay-telephony >/dev/null; fi; cp -p /opt/trelay/server.json.bak /opt/trelay/server.json; docker rename " + rollbackName + " trelay-telephony; else test " + text + "; cp -p /opt/trelay/server.json.bak /opt/trelay/server.json; fi; docker start trelay-telephony >/dev/null; test \"$(docker inspect -f '{{.State.Running}}' trelay-telephony)\" = true";
	}

	private static void RequireFingerprint(string expected)
	{
		if (string.IsNullOrWhiteSpace(expected))
		{
			throw new ArgumentException("Сначала подтверди отпечаток SSH-сервера.");
		}
	}

	private static SshClient CreateClient(string host, int port, string user, string password, string expected, Action<string> observed)
	{
		PasswordAuthenticationMethod passwordAuthenticationMethod = new PasswordAuthenticationMethod(user, password);
		ConnectionInfo connectionInfo = new ConnectionInfo(host, port, user, passwordAuthenticationMethod);
		connectionInfo.Timeout = TimeSpan.FromSeconds(15.0);
		ConnectionInfo connectionInfo2 = connectionInfo;
		SshClient sshClient = new SshClient(connectionInfo2);
		sshClient.KeepAliveInterval = TimeSpan.FromSeconds(10.0);
		SshClient sshClient2 = sshClient;
		sshClient2.HostKeyReceived += delegate(object sender, HostKeyEventArgs e)
		{
			string text;
			using (SHA256 sHA = SHA256.Create())
			{
				text = "SHA256:" + Convert.ToBase64String(sHA.ComputeHash(e.HostKey)).TrimEnd('=');
			}
			observed(text);
			e.CanTrust = !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, text, StringComparison.Ordinal);
		};
		return sshClient2;
	}

	private static void ValidateInput(string host, int port, string user, string password)
	{
		if (string.IsNullOrWhiteSpace(host) || !Regex.IsMatch(host, "^[a-zA-Z0-9.:-]+$"))
		{
			throw new ArgumentException("Некорректный IP или адрес VPS.");
		}
		if (port < 1 || port > 65535)
		{
			throw new ArgumentException("Некорректный SSH-порт.");
		}
		if (string.IsNullOrWhiteSpace(user) || !Regex.IsMatch(user, "^[a-zA-Z0-9._-]+$"))
		{
			throw new ArgumentException("Некорректный SSH-логин.");
		}
		if (string.IsNullOrEmpty(password))
		{
			throw new ArgumentException("Введи пароль VPS.");
		}
	}

	private static string Run(SshClient client, string command)
	{
		using SshCommand sshCommand = client.CreateCommand(command);
		sshCommand.CommandTimeout = TimeSpan.FromSeconds(45.0);
		return sshCommand.Execute() ?? "";
	}

	private static void RunChecked(SshClient client, string command, string message)
	{
		using SshCommand sshCommand = client.CreateCommand(command);
		sshCommand.CommandTimeout = TimeSpan.FromSeconds(45.0);
		sshCommand.Execute();
		int? exitStatus = sshCommand.ExitStatus;
		if (exitStatus.GetValueOrDefault() != 0 || !exitStatus.HasValue)
		{
			throw new InvalidOperationException(message + " Код команды: " + sshCommand.ExitStatus + ".");
		}
	}

	private static string ShellQuote(string value)
	{
		return (value ?? "").Replace("'", "'\"'\"'");
	}
}
