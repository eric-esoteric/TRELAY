using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TRELAY;

internal static class WindowChrome
{
	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hwnd, int command);

	public static void ApplyDark(IntPtr handle)
	{
		try
		{
			int value = 1;
			DwmSetWindowAttribute(handle, 20, ref value, 4);
			DwmSetWindowAttribute(handle, 19, ref value, 4);
			int value2 = 0;
			int value3 = 16777215;
			DwmSetWindowAttribute(handle, 35, ref value2, 4);
			DwmSetWindowAttribute(handle, 36, ref value3, 4);
		}
		catch
		{
		}
	}

	public static void RestoreToForeground(Form form)
	{
		if (form == null || form.IsDisposed)
		{
			return;
		}
		try
		{
			ShowWindow(form.Handle, (form.WindowState == FormWindowState.Minimized) ? 9 : 5);
			BringWindowToTop(form.Handle);
			SetForegroundWindow(form.Handle);
			form.BringToFront();
			form.Activate();
		}
		catch
		{
		}
	}
}
