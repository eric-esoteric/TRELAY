using System.Threading;

namespace TRELAY;

internal sealed class RelayTarget
{
	private int port;

	internal readonly string Id;

	internal readonly string Name;

	internal int Port
	{
		get
		{
			return Volatile.Read(ref port);
		}
		set
		{
			Volatile.Write(ref port, value);
		}
	}

	internal RelayTarget(string id, string name, int port)
	{
		Id = id;
		Name = name;
		this.port = port;
	}
}
