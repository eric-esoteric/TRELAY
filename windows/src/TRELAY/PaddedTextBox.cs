using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TRELAY;

internal class PaddedTextBox : TextBox
{
	private const int EmSetMargins = 211;

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		int num = Math.Max(6, (int)Math.Round(8f * (float)base.DeviceDpi / 96f));
		SendMessage(base.Handle, 211, new IntPtr(3), new IntPtr((num << 16) | num));
	}
}
