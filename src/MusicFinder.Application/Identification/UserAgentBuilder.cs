using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Identification;

/// <summary>The application's name and version, sent to MusicBrainz so it can tell clients apart.</summary>
public sealed record AppInfo(string Name, string Version);

/// <summary>Why an online request ultimately failed (after the retries that were worth making).</summary>
public enum IdentificationFailure { Offline, RateLimited, ServerError }

public sealed class IdentificationHttpException : Exception
{
    public IdentificationFailure Failure { get; }
    public int? StatusCode { get; }

    public IdentificationHttpException(IdentificationFailure failure, int? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }
}

public static class UserAgentBuilder
{
    /// <summary>
    /// "MusicFinder/1.0.0 ( me@example.com )", the form MusicBrainz asks for. Null when there is no usable contact:
    /// MusicBrainz throttles anonymous clients harder, so the request is not sent at all.
    /// </summary>
    public static string? Build(AppInfo app, string? contact)
    {
        var c = Clean(contact);
        if (c is null) return null;
        return $"{Clean(app.Name) ?? "MusicFinder"}/{Clean(app.Version) ?? "0"} ( {c} )";
    }

    // The value ends up in an HTTP header: no control characters, no brackets that would end the comment early.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Where(ch => !char.IsControl(ch) && ch is not ('(' or ')' or ';' or ','))
                         .Select(ch => char.IsWhiteSpace(ch) ? ' ' : ch).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 || cleaned.Length > 200 ? null : cleaned;
    }
}
