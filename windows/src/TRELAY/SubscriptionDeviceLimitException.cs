namespace TRELAY;

internal sealed class SubscriptionDeviceLimitException : ShareLinkResolutionException
{
	public SubscriptionDeviceLimitException(string message)
		: base(message)
	{
	}
}
