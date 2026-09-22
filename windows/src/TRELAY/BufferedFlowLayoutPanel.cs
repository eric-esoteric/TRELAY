using System.Windows.Forms;

namespace TRELAY;

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
	public BufferedFlowLayoutPanel()
	{
		DoubleBuffered = true;
		base.ResizeRedraw = true;
	}
}
