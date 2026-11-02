namespace MusicFinder.Application.Identification;

/// <summary>fpcalc is missing, or could not fingerprint this file. The message is meant for the user.</summary>
public sealed class FingerprintException : Exception
{
    public bool Missing { get; }
    public FingerprintException(string message, bool missing = false, Exception? inner = null) : base(message, inner) => Missing = missing;
}
