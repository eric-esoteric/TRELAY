using System;

namespace TRELAY;

internal sealed class ProviderConnectionObservation
{
	public ProviderConnectionState State { get; set; }

	public string Detail { get; set; }

	public DateTime ObservedAt { get; set; }
}
