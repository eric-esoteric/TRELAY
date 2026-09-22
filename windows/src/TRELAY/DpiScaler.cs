using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TRELAY;

internal static class DpiScaler
{
	private const float BaseDpi = 96f;

	private static readonly Dictionary<Form, float> Factors = new Dictionary<Form, float>();

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr hwnd);

	public static void Apply(Form form, bool centerAfterScale = false)
	{
		if (form == null || form.IsDisposed)
		{
			return;
		}
		IntPtr handle = form.Handle;
		string environmentVariable = Environment.GetEnvironmentVariable("CALLHUNTER_TEST_SCALE");
		if (!float.TryParse(environmentVariable, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			uint dpiForWindow = GetDpiForWindow(handle);
			result = ((dpiForWindow == 0) ? 1f : ((float)dpiForWindow / 96f));
		}
		result = Math.Max(1f, Math.Min(2.5f, result));
		float value;
		bool flag = !Factors.TryGetValue(form, out value);
		if (flag)
		{
			value = 1f;
		}
		Factors[form] = result;
		form.AutoScaleMode = AutoScaleMode.None;
		if (flag)
		{
			form.DpiChanged += delegate(object sender, DpiChangedEventArgs e)
			{
				ApplyMonitorChange(form, e);
			};
			form.Disposed += delegate
			{
				Factors.Remove(form);
			};
		}
		float num = result / value;
		if (Math.Abs(num - 1f) >= 0.01f)
		{
			form.SuspendLayout();
			form.Scale(new SizeF(num, num));
			form.ResumeLayout(performLayout: true);
		}
		if (centerAfterScale)
		{
			CenterInWorkingArea(form);
		}
	}

	public static float Factor(Form form)
	{
		if (form == null || !Factors.TryGetValue(form, out var value))
		{
			return 1f;
		}
		return value;
	}

	public static int Px(Form form, int value)
	{
		return (int)Math.Round((float)value * Factor(form));
	}

	public static void ScaleNewControl(Form form, Control control)
	{
		float num = Factor(form);
		if (control != null && !(Math.Abs(num - 1f) < 0.01f))
		{
			control.Scale(new SizeF(num, num));
		}
	}

	public static void Center(Form form)
	{
		if (form != null && !form.IsDisposed)
		{
			CenterInWorkingArea(form);
		}
	}

	private static void CenterInWorkingArea(Form form)
	{
		Rectangle workingArea = Screen.FromHandle(form.Handle).WorkingArea;
		form.StartPosition = FormStartPosition.Manual;
		form.Location = new Point(workingArea.Left + Math.Max(0, (workingArea.Width - form.Width) / 2), workingArea.Top + Math.Max(0, (workingArea.Height - form.Height) / 2));
	}

	private static void ApplyMonitorChange(Form form, DpiChangedEventArgs e)
	{
		if (form == null || form.IsDisposed)
		{
			return;
		}
		float num = Factor(form);
		float num2 = Math.Max(1f, Math.Min(2.5f, (float)e.DeviceDpiNew / 96f));
		if (Math.Abs(num2 - num) < 0.01f)
		{
			return;
		}
		float num3 = num2 / num;
		Factors[form] = num2;
		form.SuspendLayout();
		foreach (Control control in form.Controls)
		{
			control.Scale(new SizeF(num3, num3));
		}
		form.Bounds = e.SuggestedRectangle;
		form.ResumeLayout(performLayout: true);
	}
}
