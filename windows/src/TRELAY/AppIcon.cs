using System.Drawing;
using System.Windows.Forms;

namespace TRELAY;

internal static class AppIcon
{
	private static Icon current;

	public static Icon Current
	{
		get
		{
			if (current == null)
			{
				current = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
			}
			return current;
		}
	}
}
