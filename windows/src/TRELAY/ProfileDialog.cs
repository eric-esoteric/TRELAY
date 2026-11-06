using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Media;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TRELAY;

internal sealed class ProfileDialog : Form
{
	private readonly SecretStore secrets;

	private readonly TunnelProfile source;

	private readonly TextBox nameBox = new PaddedTextBox();

	private readonly TextBox countryBox = new PaddedTextBox();

	private readonly TextBox keyBox = new PaddedTextBox();

	private readonly TextBox fileBox = new PaddedTextBox();

	private readonly ListBox appsList = new ListBox();

	private readonly TextBox portBox = new PaddedTextBox();

	private readonly Label connectionStatus = new Label();

	private readonly Label appsStatus = new Label();

	private readonly TextBox healthCheckBox = new PaddedTextBox();

	private readonly RoundedButton saveButton;

	private readonly RoundedButton revealButton;

	private bool useFile;

	private bool keyChanged;

	private bool resolvingProfileName;

	private readonly string initialKey;

	private readonly string initialName;

	private readonly string initialCountry;

	private readonly bool initialTelephony;

	private readonly Dictionary<string, string> appPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public TunnelProfile Result { get; private set; }

	public List<TunnelProfile> Results { get; private set; }

	public ProfileDialog(TunnelProfile profile, int suggestedPort, SecretStore secrets, string initialKey = null, string initialName = null, string initialCountry = null, bool initialTelephony = false)
	{
		source = profile;
		this.secrets = secrets;
		this.initialKey = initialKey;
		this.initialName = initialName;
		this.initialCountry = initialCountry;
		this.initialTelephony = initialTelephony;
		Text = ((profile == null) ? "Добавить маршрут" : "Изменить маршрут");
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		base.AutoScaleMode = AutoScaleMode.None;
		base.ClientSize = new Size(760, 520);
		base.StartPosition = FormStartPosition.CenterParent;
		BackColor = Theme.Current.Background;
		ForeColor = Theme.Current.Text;
		Font = Theme.Font(10f, FontStyle.Regular);
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.MinimizeBox = false;
		base.Icon = AppIcon.Current;
		Label label = Theme.Label((profile == null) ? "Новый маршрут" : "Изменить маршрут", 17f, strong: true, Theme.Current.Text);
		label.SetBounds(26, 20, 450, 32);
		base.Controls.Add(label);
		Label label2 = Theme.Label("Укажите подключение и выберите приложения — остальное проверим сами.", 9.2f, strong: false, Theme.Current.Muted);
		label2.SetBounds(28, 54, 690, 24);
		base.Controls.Add(label2);
		Panel panel = Card(28, 90, 335, 360);
		Panel panel2 = Card(378, 90, 335, 360);
		base.Controls.Add(panel);
		base.Controls.Add(panel2);
		AddLabel(panel, "Подключение", 18, 16, 12f, Theme.Current.Text);
		AddLabel(panel, "Название (необязательно)", 18, 55, 9f, Theme.Current.Muted);
		StyleInput(nameBox, 18, 77, 188, 32);
		nameBox.TextChanged += delegate
		{
			ValidateForm();
		};
		panel.Controls.Add(nameBox);
		AddLabel(panel, "Страна", 218, 55, 9f, Theme.Current.Muted);
		countryBox.SetBounds(218, 77, 99, 32);
		countryBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
		countryBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
		countryBox.AutoCompleteCustomSource.AddRange(new string[6] { "Россия", "США", "Германия", "Нидерланды", "Армения", "Авто" });
		countryBox.TextChanged += delegate
		{
			ValidateForm();
		};
		Theme.StyleInput(countryBox);
		panel.Controls.Add(countryBox);
		RoundedButton keyMode = SmallButton("Ключ или ссылка", accent: true);
		keyMode.SetBounds(18, 125, 142, 34);
		panel.Controls.Add(keyMode);
		RoundedButton fileMode = SmallButton("Файл", accent: false);
		fileMode.SetBounds(170, 125, 82, 34);
		panel.Controls.Add(fileMode);
		keyBox.Multiline = false;
		keyBox.UseSystemPasswordChar = true;
		StyleInput(keyBox, 18, 171, 299, 34);
		keyBox.TextChanged += delegate
		{
			if (keyBox.Focused)
			{
				keyChanged = true;
				TryFillSuggestedName();
			}
			ValidateForm();
		};
		panel.Controls.Add(keyBox);
		fileBox.ReadOnly = true;
		StyleInput(fileBox, 18, 171, 220, 34);
		panel.Controls.Add(fileBox);
		RoundedButton browseFile = SmallButton("", accent: false);
		browseFile.IconKind = ButtonIconKind.More;
		browseFile.SetBounds(246, 171, 71, 34);
		browseFile.Click += delegate
		{
			BrowseConfig();
		};
		panel.Controls.Add(browseFile);
		revealButton = SmallButton("Показать", accent: false);
		RoundedButton reveal = revealButton;
		reveal.SetBounds(18, 215, 110, 32);
		reveal.Click += delegate
		{
			keyBox.UseSystemPasswordChar = !keyBox.UseSystemPasswordChar;
			reveal.Text = (keyBox.UseSystemPasswordChar ? "Показать" : "Скрыть");
		};
		panel.Controls.Add(reveal);
		connectionStatus.AutoSize = false;
		connectionStatus.SetBounds(18, 263, 299, 60);
		connectionStatus.Font = Theme.Font(9f, FontStyle.Regular);
		connectionStatus.ForeColor = Theme.Current.Muted;
		connectionStatus.BackColor = Color.Transparent;
		panel.Controls.Add(connectionStatus);
		keyMode.Click += delegate
		{
			SetConnectionMode(file: false, keyMode, fileMode, browseFile, reveal);
		};
		fileMode.Click += delegate
		{
			SetConnectionMode(file: true, keyMode, fileMode, browseFile, reveal);
		};
		AddLabel(panel2, "Приложения", 18, 16, 12f, Theme.Current.Text);
		Label label3 = Theme.Label("Повтор приложения = резервный VPN", 8.5f, strong: false, Theme.Current.Muted);
		label3.SetBounds(18, 48, 300, 35);
		panel2.Controls.Add(label3);
		appsList.SetBounds(18, 88, 299, 136);
		appsList.BorderStyle = BorderStyle.None;
		appsList.ItemHeight = 26;
		Theme.StyleInput(appsList);
		panel2.Controls.Add(appsList);
		RoundedButton roundedButton = SmallButton("Добавить .exe", accent: true);
		roundedButton.IconKind = ButtonIconKind.Plus;
		roundedButton.IconSize = 15;
		roundedButton.SetBounds(18, 236, 180, 38);
		roundedButton.Click += delegate
		{
			BrowseApps();
		};
		panel2.Controls.Add(roundedButton);
		RoundedButton roundedButton2 = SmallButton("Убрать", accent: false);
		roundedButton2.SetBounds(208, 236, 109, 38);
		roundedButton2.Click += delegate
		{
			RemoveApp();
		};
		panel2.Controls.Add(roundedButton2);
		appsStatus.AutoSize = false;
		appsStatus.SetBounds(18, 278, 299, 23);
		appsStatus.Font = Theme.Font(8.8f, FontStyle.Regular);
		appsStatus.ForeColor = Theme.Current.Muted;
		appsStatus.BackColor = Color.Transparent;
		panel2.Controls.Add(appsStatus);
		AddLabel(panel2, "Проверка сервиса (необязательно)", 18, 305, 8.3f, Theme.Current.Muted);
		StyleInput(healthCheckBox, 18, 326, 299, 30);
		healthCheckBox.TextChanged += delegate
		{
			ValidateForm();
		};
		panel2.Controls.Add(healthCheckBox);
		AddLabel(base.Controls, "SOCKS-порт", 28, 462, 8.8f, Theme.Current.Muted);
		portBox.SetBounds(125, 458, 100, 30);
		Theme.StyleInput(portBox);
		portBox.TextChanged += delegate
		{
			ValidateForm();
		};
		base.Controls.Add(portBox);
		saveButton = Theme.Button("Сохранить маршрут", accent: true);
		saveButton.SetBounds(460, 465, 253, 42);
		saveButton.Click += delegate
		{
			SaveProfile();
		};
		base.Controls.Add(saveButton);
		LoadProfile(profile, suggestedPort);
		SetConnectionMode(useFile, keyMode, fileMode, browseFile, reveal);
		ValidateForm();
		DpiScaler.Apply(this);
		base.Shown += delegate
		{
			BeginInvoke((Action)delegate
			{
				base.ActiveControl = null;
				nameBox.DeselectAll();
			});
		};
	}

	private Panel Card(int x, int y, int width, int height)
	{
		RoundedPanel roundedPanel = new RoundedPanel();
		roundedPanel.BackColor = Theme.Current.Card;
		roundedPanel.Radius = 16;
		RoundedPanel roundedPanel2 = roundedPanel;
		roundedPanel2.SetBounds(x, y, width, height);
		return roundedPanel2;
	}

	private void AddLabel(Control.ControlCollection parent, string text, int x, int y, float size, Color color)
	{
		Label label = Theme.Label(text, size, size >= 11f, color);
		label.SetBounds(x, y, 300, 26);
		parent.Add(label);
	}

	private void AddLabel(Control parent, string text, int x, int y, float size, Color color)
	{
		AddLabel(parent.Controls, text, x, y, size, color);
	}

	private RoundedButton SmallButton(string text, bool accent)
	{
		RoundedButton roundedButton = Theme.Button(text, accent);
		roundedButton.Font = Theme.Font(8.7f, FontStyle.Bold);
		return roundedButton;
	}

	private static void StyleInput(TextBox box, int x, int y, int width, int height)
	{
		box.BorderStyle = BorderStyle.FixedSingle;
		box.SetBounds(x, y, width, height);
		Theme.StyleInput(box);
	}

	private void LoadProfile(TunnelProfile profile, int suggestedPort)
	{
		if (profile == null)
		{
			nameBox.Text = initialName ?? "";
			countryBox.Text = initialCountry ?? "Авто";
			portBox.Text = suggestedPort.ToString();
			useFile = false;
			keyChanged = true;
			if (!string.IsNullOrWhiteSpace(initialKey))
			{
				keyBox.Text = initialKey;
			}
			TryFillSuggestedName();
			return;
		}
		nameBox.Text = profile.Name;
		countryBox.Text = (string.IsNullOrWhiteSpace(profile.Country) ? "Авто" : profile.Country);
		healthCheckBox.Text = profile.HealthCheckEndpoint ?? "";
		portBox.Text = Math.Max(1025, Math.Min(65535, profile.SocksPort)).ToString();
		useFile = !string.Equals(profile.ConnectionType, "key", StringComparison.OrdinalIgnoreCase);
		fileBox.Text = profile.XrayConfigPath ?? "";
		if (!useFile && secrets.Exists(profile.Id))
		{
			keyBox.Text = secrets.Load(profile.Id);
		}
		keyChanged = false;
		foreach (string item in profile.AppNames ?? new List<string>())
		{
			appsList.Items.Add(item);
		}
		foreach (string item2 in profile.AppPaths ?? new List<string>())
		{
			if (File.Exists(item2))
			{
				appPaths[Path.GetFileNameWithoutExtension(item2)] = item2;
			}
		}
	}

	private void SetConnectionMode(bool file, RoundedButton keyButton, RoundedButton fileButton, RoundedButton browseFile, RoundedButton reveal)
	{
		useFile = file;
		keyBox.Visible = !file;
		reveal.Visible = !file;
		fileBox.Visible = file;
		browseFile.Visible = file;
		keyButton.BackColor = ((!file) ? Theme.Current.Accent : Theme.Current.Input);
		keyButton.ForeColor = ((!file) ? Color.Black : Theme.Current.Text);
		fileButton.BackColor = (file ? Theme.Current.Accent : Theme.Current.Input);
		fileButton.ForeColor = (file ? Color.Black : Theme.Current.Text);
		ValidateForm();
	}

	private void BrowseConfig()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Filter = "VPN configs|*.json;*.xray;*.yaml;*.yml|All files|*.*";
		openFileDialog.CheckFileExists = true;
		using OpenFileDialog openFileDialog2 = openFileDialog;
		if (openFileDialog2.ShowDialog(this) == DialogResult.OK)
		{
			fileBox.Text = openFileDialog2.FileName;
			if (string.IsNullOrWhiteSpace(nameBox.Text))
			{
				nameBox.Text = Path.GetFileNameWithoutExtension(openFileDialog2.FileName);
			}
			ValidateForm();
		}
	}

	private void TryFillSuggestedName()
	{
		if (!string.IsNullOrWhiteSpace(nameBox.Text))
		{
			return;
		}
		string text = keyBox.Text.Trim();
		if (!text.StartsWith("key://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			string value = ConnectionValidator.SuggestProfileName(text);
			if (!string.IsNullOrWhiteSpace(value))
			{
				nameBox.Text = value;
			}
		}
	}

	private void BrowseApps()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Filter = "Windows applications|*.exe";
		openFileDialog.CheckFileExists = true;
		openFileDialog.Multiselect = true;
		using OpenFileDialog openFileDialog2 = openFileDialog;
		if (openFileDialog2.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		string[] fileNames = openFileDialog2.FileNames;
		foreach (string text in fileNames)
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (!appsList.Items.Cast<string>().Contains(fileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
			{
				appsList.Items.Add(fileNameWithoutExtension);
			}
			appPaths[fileNameWithoutExtension] = text;
		}
		ValidateForm();
	}

	private void RemoveApp()
	{
		if (appsList.SelectedIndex >= 0)
		{
			string key = appsList.SelectedItem.ToString();
			appsList.Items.RemoveAt(appsList.SelectedIndex);
			appPaths.Remove(key);
		}
		ValidateForm();
	}

	private bool ValidateForm()
	{
		string kind = "";
		bool flag;
		string message;
		if (useFile)
		{
			flag = ConnectionValidator.ValidateFile(fileBox.Text, out kind, out message);
		}
		else if (!keyChanged && source != null && secrets.Exists(source.Id))
		{
			flag = true;
			kind = source.KeyKind;
			message = "Ключ сохранён и защищён Windows.";
		}
		else
		{
			flag = ConnectionValidator.ValidateKey(keyBox.Text, out kind, out message);
		}
		connectionStatus.Text = (flag ? "✓  " : "!  ") + message;
		connectionStatus.ForeColor = (flag ? Theme.Current.Success : Theme.Current.Danger);
		bool flag2 = appsList.Items.Count > 0;
		appsStatus.Text = (flag2 ? ("✓ Добавлено: " + appsList.Items.Count) : "! Добавь хотя бы одно приложение");
		appsStatus.ForeColor = (flag2 ? Theme.Current.Success : Theme.Current.Danger);
		bool flag3 = !string.IsNullOrWhiteSpace(countryBox.Text);
		int result;
		bool flag4 = int.TryParse(portBox.Text, out result) && result >= 1025 && result <= 65535;
		string message2;
		bool flag5 = SocksHealthProbe.ValidateEndpoint(healthCheckBox.Text, out message2);
		if (!flag5)
		{
			appsStatus.Text = "! " + message2;
			appsStatus.ForeColor = Theme.Current.Danger;
		}
		saveButton.Enabled = !resolvingProfileName && flag && flag2 && flag3 && flag5 && flag4;
		saveButton.BackColor = (saveButton.Enabled ? Theme.Current.Accent : Theme.Current.Hover);
		return saveButton.Enabled;
	}

	private async void SaveProfile()
	{
		if (resolvingProfileName)
		{
			return;
		}
		if (!ValidateForm())
		{
			SystemSounds.Exclamation.Play();
			return;
		}
		string kind = "";
		string ignored;
		if (useFile)
		{
			ConnectionValidator.ValidateFile(fileBox.Text, out kind, out ignored);
		}
		else if (keyChanged || source == null)
		{
			ConnectionValidator.ValidateKey(keyBox.Text, out kind, out ignored);
		}
		else if (!useFile && source != null)
		{
			kind = source.KeyKind;
		}
		string enteredKey = keyBox.Text.Trim();
		string subscription = !useFile && enteredKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? enteredKey : null;
		string resolvedSuggestedName = "";
		bool isSubscription = enteredKey.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase) || enteredKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
		if (!useFile && isSubscription)
		{
			resolvingProfileName = true;
			saveButton.Enabled = false;
			connectionStatus.Text = "↻  Получаю список серверов…";
			connectionStatus.ForeColor = Theme.Current.Muted;
			List<string> links = null;
			SubscriptionIdentityRequiredException identityError = null;
			string savedClientId = TryLoadSubscriptionClientId();
			try
			{
				links = await Task.Run(() => ShareLinkResolver.ResolveMany(enteredKey, savedClientId));
			}
			catch (SubscriptionIdentityRequiredException ex)
			{
				identityError = ex;
			}
			catch (ShareLinkResolutionException ex2)
			{
				resolvingProfileName = false;
				ValidateForm();
				MessageBox.Show(this, ex2.Message, "Не удалось открыть подписку", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return;
			}
			catch (Exception)
			{
				resolvingProfileName = false;
				ValidateForm();
				MessageBox.Show(this, "Не удалось получить список серверов. Проверьте подключение и повторите сохранение.", "Не удалось открыть подписку", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return;
			}
			if (identityError != null)
			{
				DialogResult answer = MessageBox.Show(this, "Сервис «" + identityError.Host + "» выдаёт серверы только клиентам с постоянным ID устройства.\n\nTRELAY создаст постоянный анонимизированный ID для этой Windows, защитит его и будет отправлять только этому сервису. Исходный системный ID и серийные номера наружу не передаются.", "Подписка требует ID устройства", MessageBoxButtons.OKCancel, MessageBoxIcon.Asterisk);
				if (answer != DialogResult.OK)
				{
					resolvingProfileName = false;
					ValidateForm();
					return;
				}
				try
				{
					string clientId = GetOrCreateSubscriptionClientId();
					links = await Task.Run(() => ShareLinkResolver.ResolveMany(enteredKey, clientId));
				}
				catch (ShareLinkResolutionException ex4)
				{
					resolvingProfileName = false;
					ValidateForm();
					MessageBox.Show(this, ex4.Message, "Не удалось открыть подписку", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				catch (Exception)
				{
					resolvingProfileName = false;
					ValidateForm();
					MessageBox.Show(this, "Не удалось защитить анонимный ID подписки через Windows.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					return;
				}
			}
			resolvingProfileName = false;
			if (!base.IsDisposed && !base.Disposing)
			{
				ValidateForm();
			}
			if (base.IsDisposed || base.Disposing || !string.Equals(enteredKey, keyBox.Text.Trim(), StringComparison.Ordinal))
			{
				return;
			}
			if (links == null || links.Count == 0)
			{
				MessageBox.Show(this, "Подписка не вернула ни одного сервера.", "Не удалось открыть подписку", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return;
			}
			if (links.Count > 1)
			{
				using (ServerSelectionDialog serverSelectionDialog = new ServerSelectionDialog(links))
				{
				if (serverSelectionDialog.ShowDialog(this) != DialogResult.OK || !SaveServerGroup(serverSelectionDialog.SelectedServers, subscription))
					{
						return;
					}
				}
				base.DialogResult = DialogResult.OK;
				Close();
				return;
			}
			enteredKey = links[0];
			ConnectionValidator.ValidateKey(enteredKey, out kind, out ignored);
			resolvedSuggestedName = ConnectionValidator.SuggestProfileName(enteredKey);
		}
		string profileName = nameBox.Text.Trim();
		if (profileName.Length == 0 && resolvedSuggestedName.Length > 0)
		{
			profileName = resolvedSuggestedName;
		}
		if (profileName.Length == 0)
		{
			if (useFile)
			{
				profileName = Path.GetFileNameWithoutExtension(fileBox.Text.Trim());
			}
			else
			{
				string keyForName = keyBox.Text;
				if (keyForName.StartsWith("key://", StringComparison.OrdinalIgnoreCase) || keyForName.StartsWith("ssconf://", StringComparison.OrdinalIgnoreCase))
				{
					resolvingProfileName = true;
					saveButton.Enabled = false;
					connectionStatus.Text = "↻  Получаю название от VPN…";
					connectionStatus.ForeColor = Theme.Current.Muted;
					try
					{
						profileName = await Task.Run(() => ConnectionValidator.SuggestProfileName(keyForName));
					}
					finally
					{
						resolvingProfileName = false;
						if (!base.IsDisposed && !base.Disposing)
						{
							ValidateForm();
						}
					}
					if (base.IsDisposed || base.Disposing)
					{
						return;
					}
					if (useFile || !string.Equals(keyForName, keyBox.Text, StringComparison.Ordinal))
					{
						SaveProfile();
						return;
					}
					if (!ValidateForm())
					{
						return;
					}
				}
				else
				{
					profileName = ConnectionValidator.SuggestProfileName(keyForName);
				}
			}
		}
		if (string.IsNullOrWhiteSpace(profileName))
		{
			profileName = (useFile ? "Xray VPN" : (string.IsNullOrWhiteSpace(kind) ? "VPN" : (kind.ToUpperInvariant() + " VPN")));
		}
		Result = new TunnelProfile
		{
			Id = ((source == null || (!useFile && keyChanged)) ? Guid.NewGuid().ToString("N") : source.Id),
			Name = profileName,
			ConnectionType = (useFile ? "file" : "key"),
			KeyKind = kind,
			Country = countryBox.Text.Trim(),
			ProviderId = ((source == null) ? "auto" : source.ProviderId),
			TelephonyOptimized = ((source == null) ? initialTelephony : source.TelephonyOptimized),
			HealthCheckEndpoint = healthCheckBox.Text.Trim(),
			XrayConfigPath = (useFile ? fileBox.Text.Trim() : ""),
			SocksPort = int.Parse(portBox.Text),
			Enabled = (source == null || source.Enabled),
			FailoverGroupId = source?.FailoverGroupId ?? "",
			FailoverPriority = source?.FailoverPriority ?? 0,
			SubscriptionId = !useFile && !keyChanged ? source?.SubscriptionId : null,
			SubscriptionReplacement = !useFile && !keyChanged && source?.SubscriptionReplacement == true,
			SingleServerSubscription = !useFile && !keyChanged && source?.SingleServerSubscription == true,
			AppNames = appsList.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
			AppPaths = (from string x in appsList.Items
				where appPaths.ContainsKey(x)
				select appPaths[x]).ToList()
		};
		try
		{
			if (!useFile && (keyChanged || source == null))
			{
				secrets.Save(Result.Id, enteredKey);
			}
			if (subscription != null)
			{
				Result.SubscriptionId = SubscriptionUpdates.SaveSource(secrets, subscription);
				Result.SubscriptionReplacement = false;
				Result.SingleServerSubscription = true;
			}
		}
		catch (Exception)
		{
			Result = null;
			MessageBox.Show(this, "Не удалось сохранить ключ. Проверьте свободное место и доступ к папке данных, затем повторите сохранение.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return;
		}
		Results = new List<TunnelProfile> { Result };
		base.DialogResult = DialogResult.OK;
		Close();
	}

	private string GetOrCreateSubscriptionClientId()
	{
		string text = TryLoadSubscriptionClientId();
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		byte[] array = CreateStableWindowsSubscriptionId();
		try
		{
			string text2 = "win-" + BitConverter.ToString(array, 0, 16).Replace("-", "").ToLowerInvariant();
			secrets.Save("subscription-client-v1", text2);
			return text2;
		}
		finally
		{
			Array.Clear(array, 0, array.Length);
		}
	}

	private string TryLoadSubscriptionClientId()
	{
		try
		{
			if (!secrets.Exists("subscription-client-v1"))
			{
				return null;
			}
			string text = secrets.Load("subscription-client-v1");
			if (!IsValidSubscriptionClientId(text))
			{
				return null;
			}
			return text;
		}
		catch
		{
			return null;
		}
	}

	private static byte[] CreateStableWindowsSubscriptionId()
	{
		string text = null;
		try
		{
			using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography", writable: false);
			text = Convert.ToString(registryKey?.GetValue("MachineGuid"));
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Guid.NewGuid().ToString("N");
		}
		using SHA256 sHA = SHA256.Create();
		return sHA.ComputeHash(Encoding.UTF8.GetBytes(text.Trim().ToLowerInvariant() + ":TRELAY.subscription.win.v1"));
	}

	private static bool IsValidSubscriptionClientId(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length < 10 || value.Length > 64)
		{
			return false;
		}
		return value.All((char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || char.IsDigit(c) || c == '=' || c == '-');
	}

	private bool SaveServerGroup(List<ServerCandidate> selected, string subscription)
	{
		List<TunnelProfile> list = new List<TunnelProfile>();
		string failoverGroupId = Guid.NewGuid().ToString("N");
		string subscriptionId = null;
		int num = int.Parse(portBox.Text);
		try
		{
			if (subscription != null) subscriptionId = SubscriptionUpdates.SaveSource(secrets, subscription);
			for (int i = 0; i < selected.Count; i++)
			{
				ServerCandidate serverCandidate = selected[i];
				if (!ConnectionValidator.ValidateKey(serverCandidate.Link, out var kind, out var _))
				{
					throw new InvalidDataException("Сервер «" + serverCandidate.Name + "» содержит повреждённый ключ.");
				}
				string text = nameBox.Text.Trim();
				string name = ((text.Length == 0) ? serverCandidate.Name : (text + ((selected.Count > 1) ? (" · " + serverCandidate.Name) : "")));
				string text2 = countryBox.Text.Trim();
				if (text2.Equals("Авто", StringComparison.OrdinalIgnoreCase) && serverCandidate.Country != "Авто")
				{
					text2 = serverCandidate.Country;
				}
				TunnelProfile tunnelProfile = new TunnelProfile();
				tunnelProfile.Id = Guid.NewGuid().ToString("N");
				tunnelProfile.Name = name;
				tunnelProfile.ConnectionType = "key";
				tunnelProfile.KeyKind = kind;
				tunnelProfile.Country = text2;
				tunnelProfile.ProviderId = ((source == null) ? "auto" : source.ProviderId);
				tunnelProfile.TelephonyOptimized = ((source == null) ? initialTelephony : source.TelephonyOptimized);
				tunnelProfile.HealthCheckEndpoint = healthCheckBox.Text.Trim();
				tunnelProfile.SocksPort = Math.Min(65535, num + i);
				tunnelProfile.Enabled = source == null || source.Enabled;
				tunnelProfile.FailoverGroupId = failoverGroupId;
				tunnelProfile.FailoverPriority = i;
				tunnelProfile.SubscriptionId = subscriptionId;
				tunnelProfile.AppNames = appsList.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
				tunnelProfile.AppPaths = (from string x in appsList.Items
					where appPaths.ContainsKey(x)
					select appPaths[x]).ToList();
				TunnelProfile tunnelProfile2 = tunnelProfile;
				secrets.Save(tunnelProfile2.Id, serverCandidate.Link);
				list.Add(tunnelProfile2);
			}
		}
		catch (Exception ex)
		{
			if (subscriptionId != null) secrets.Delete(subscriptionId);
			foreach (TunnelProfile item in list)
			{
				secrets.Delete(item.Id);
			}
			MessageBox.Show(this, (ex is InvalidDataException) ? ex.Message : "Не удалось защитить выбранные ключи Windows. Проверь доступ к папке данных.", "TRELAY", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		Results = list;
		Result = list[0];
		return true;
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		WindowChrome.ApplyDark(base.Handle);
	}

	public void CaptureSnapshot(string path)
	{
		Show();
		Refresh();
		Application.DoEvents();
		using (Bitmap bitmap = new Bitmap(base.Width, base.Height))
		{
			DrawToBitmap(bitmap, new Rectangle(0, 0, base.Width, base.Height));
			bitmap.Save(path, ImageFormat.Png);
		}
		Hide();
	}
}
