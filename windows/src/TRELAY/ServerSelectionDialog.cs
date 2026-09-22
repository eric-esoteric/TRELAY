using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TRELAY;

internal sealed class ServerSelectionDialog : Form
{
	private readonly ListView list = new ListView();

	private readonly RoundedButton saveButton;

	public List<ServerCandidate> SelectedServers { get; private set; }

	internal ServerSelectionDialog(IEnumerable<string> links)
	{
		Text = "Выбрать резервные серверы";
		base.Icon = AppIcon.Current;
		base.AutoScaleDimensions = new SizeF(96f, 96f);
		base.AutoScaleMode = AutoScaleMode.None;
		base.ClientSize = new Size(780, 520);
		base.StartPosition = FormStartPosition.CenterParent;
		BackColor = Theme.Current.Background;
		ForeColor = Theme.Current.Text;
		Font = Theme.Font(10f, FontStyle.Regular);
		base.FormBorderStyle = FormBorderStyle.FixedDialog;
		base.MaximizeBox = false;
		base.MinimizeBox = false;
		Label label = Theme.Label("Какие серверы использовать", 17f, strong: true, Theme.Current.Text);
		label.SetBounds(26, 20, 650, 32);
		base.Controls.Add(label);
		Label label2 = Theme.Label("Первый выбранный — основной. Остальные уже запущены и ждут сбоя.", 9.2f, strong: false, Theme.Current.Muted);
		label2.SetBounds(28, 55, 700, 24);
		base.Controls.Add(label2);
		this.list.SetBounds(28, 94, 724, 330);
		this.list.View = View.Details;
		this.list.CheckBoxes = true;
		this.list.FullRowSelect = true;
		this.list.HideSelection = false;
		this.list.MultiSelect = false;
		this.list.BorderStyle = BorderStyle.FixedSingle;
		this.list.BackColor = Theme.Current.Input;
		this.list.ForeColor = Theme.Current.Text;
		this.list.Columns.Add("Приоритет", 84);
		this.list.Columns.Add("Название от провайдера", 320);
		this.list.Columns.Add("Страна", 105);
		this.list.Columns.Add("Сервер", 190);
		ListView listView = this.list;
		ItemCheckedEventHandler value = delegate
		{
			BeginInvoke(new Action(RefreshState));
		};
		listView.ItemChecked += value;
		base.Controls.Add(this.list);
		RoundedButton roundedButton = Theme.Button("Выше", accent: false);
		roundedButton.SetBounds(28, 440, 102, 36);
		roundedButton.Click += delegate
		{
			MoveSelected(-1);
		};
		base.Controls.Add(roundedButton);
		RoundedButton roundedButton2 = Theme.Button("Ниже", accent: false);
		roundedButton2.SetBounds(140, 440, 102, 36);
		roundedButton2.Click += delegate
		{
			MoveSelected(1);
		};
		base.Controls.Add(roundedButton2);
		RoundedButton roundedButton3 = Theme.Button("Отмена", accent: false);
		roundedButton3.SetBounds(420, 440, 140, 42);
		roundedButton3.Click += delegate
		{
			base.DialogResult = DialogResult.Cancel;
			Close();
		};
		base.Controls.Add(roundedButton3);
		saveButton = Theme.Button("Добавить выбранные", accent: true);
		saveButton.SetBounds(572, 440, 180, 42);
		saveButton.Click += Save;
		base.Controls.Add(saveButton);
		List<ServerCandidate> list = links.Select(CreateCandidate).ToList();
		List<int> list2 = (from x in list.Select((ServerCandidate candidate, int index) => new { candidate, index })
			where x.candidate.Country == "Россия"
			select x.index).Take(3).ToList();
		HashSet<int> hashSet = ((list2.Count > 0) ? new HashSet<int>(list2) : new HashSet<int>(Enumerable.Range(0, Math.Min(3, list.Count))));
		for (int num = 0; num < list.Count; num++)
		{
			AddItem(list[num], hashSet.Contains(num));
		}
		RefreshState();
		DpiScaler.Apply(this);
		this.list.Columns[0].Width = DpiScaler.Px(this, 84);
		this.list.Columns[1].Width = DpiScaler.Px(this, 320);
		this.list.Columns[2].Width = DpiScaler.Px(this, 105);
		this.list.Columns[3].Width = DpiScaler.Px(this, 190);
	}

	private static ServerCandidate CreateCandidate(string link)
	{
		string text = ConnectionValidator.SuggestProfileName(link);
		Uri result;
		string text2 = (link.StartsWith("xrayjson://", StringComparison.OrdinalIgnoreCase) ? EmbeddedXrayConfig.GetEndpoint(link) : (link.StartsWith("trelay-mihomo://", StringComparison.OrdinalIgnoreCase) ? MihomoConfig.GetEndpoint(link) : (Uri.TryCreate(link, UriKind.Absolute, out result) ? (result.Host + (result.IsDefaultPort ? "" : (":" + result.Port))) : "сервер")));
		if (string.IsNullOrWhiteSpace(text))
		{
			text = text2;
		}
		string text3 = (text + " " + text2).ToLowerInvariant();
		bool flag = new string[11]
		{
			"russia", "russian", "росси", "москва", "moscow", "saint petersburg", "петербург", "kazan", "ru-", " ru ",
			"\ud83c\uddf7\ud83c\uddfa"
		}.Any(text3.Contains);
		ServerCandidate serverCandidate = new ServerCandidate();
		serverCandidate.Link = link;
		serverCandidate.Name = text;
		serverCandidate.Country = (flag ? "Россия" : "Авто");
		serverCandidate.Endpoint = text2;
		return serverCandidate;
	}

	private void AddItem(ServerCandidate candidate, bool selected)
	{
		ListViewItem listViewItem = new ListViewItem("");
		listViewItem.SubItems.Add(candidate.Name);
		listViewItem.SubItems.Add(candidate.Country);
		listViewItem.SubItems.Add(candidate.Endpoint);
		listViewItem.Tag = candidate;
		listViewItem.Checked = selected;
		list.Items.Add(listViewItem);
	}

	private void RefreshState()
	{
		int num = 0;
		foreach (ListViewItem item in list.Items)
		{
			item.SubItems[0].Text = (item.Checked ? ((++num == 1) ? "Основной" : ("Резерв " + (num - 1))) : "—");
		}
		saveButton.Enabled = num > 0;
		saveButton.BackColor = (saveButton.Enabled ? Theme.Current.Accent : Theme.Current.Hover);
	}

	private void MoveSelected(int delta)
	{
		if (list.SelectedIndices.Count != 0)
		{
			int num = list.SelectedIndices[0];
			int num2 = num + delta;
			if (num2 >= 0 && num2 < list.Items.Count)
			{
				ListViewItem listViewItem = list.Items[num];
				list.Items.RemoveAt(num);
				list.Items.Insert(num2, listViewItem);
				listViewItem.Selected = true;
				listViewItem.Focused = true;
				RefreshState();
			}
		}
	}

	private void Save(object sender, EventArgs e)
	{
		SelectedServers = (from ListViewItem x in list.Items
			where x.Checked
			select (ServerCandidate)x.Tag).ToList();
		if (SelectedServers.Count != 0)
		{
			base.DialogResult = DialogResult.OK;
			Close();
		}
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		WindowChrome.ApplyDark(base.Handle);
	}
}
