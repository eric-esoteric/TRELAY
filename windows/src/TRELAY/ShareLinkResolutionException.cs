using System;

namespace TRELAY;

internal class ShareLinkResolutionException : Exception
{
	public ShareLinkResolutionException(string message)
		: base(message)
	{
	}

	public ShareLinkResolutionException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
