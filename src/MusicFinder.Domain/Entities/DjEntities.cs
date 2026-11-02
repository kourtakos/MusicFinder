namespace MusicFinder.Domain.Entities;

public sealed class DjSession
{
    private DateTime? _endedUtc;
    private DateTime _startedUtc;

    public long Id { get; set; }

    /// <summary>Always UTC: the setter restores the Kind that SQLite text loses (see DapperConfig).</summary>
    public DateTime StartedUtc
    {
        get => _startedUtc;
        set => _startedUtc = Models.UtcTime.Normalize(value);
    }

    /// <summary>Null while the session is active. Always UTC (nullable DateTime bypasses the Dapper handler, so Kind is fixed here).</summary>
    public DateTime? EndedUtc
    {
        get => _endedUtc;
        set => _endedUtc = Models.UtcTime.Normalize(value);
    }

    public string? DjSoftware { get; set; }
    public string? Name { get; set; }
    /// <summary>True when MusicFinder started it on the first drag instead of the user pressing Start.</summary>
    public bool AutoStarted { get; set; }
}

/// <summary>A row of the append-only TrackEvents log. <see cref="EvidenceJson"/> records why, never more than what was observed.</summary>
public sealed class TrackEvent
{
    private DateTime _occurredUtc;

    public long Id { get; set; }
    public long? TrackId { get; set; }
    public long? SessionId { get; set; }
    /// <summary>A <see cref="Enums.TrackEventType"/> name.</summary>
    public string EventType { get; set; } = "";
    public DateTime OccurredUtc
    {
        get => _occurredUtc;
        set => _occurredUtc = Models.UtcTime.Normalize(value);
    }
    /// <summary>Where the event came from, e.g. <see cref="Models.PlaySources.InactivityHeuristic"/> or <see cref="Models.PlaySources.User"/>.</summary>
    public string Source { get; set; } = "";
    public double? Confidence { get; set; }
    public string? EvidenceJson { get; set; }
}

/// <summary>A play of a track: Possible (inferred) or Confirmed (manual or, from M4, a DJ application's own history).</summary>
public sealed class PlayRecord
{
    private DateTime _playedUtc;

    public long Id { get; set; }
    public long TrackId { get; set; }
    public long? SessionId { get; set; }
    public DateTime PlayedUtc
    {
        get => _playedUtc;
        set => _playedUtc = Models.UtcTime.Normalize(value);
    }
    /// <summary>A <see cref="Enums.PlayStatus"/> name (the database enforces 'Possible' or 'Confirmed').</summary>
    public string Status { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Stable id from the source (an importer's event id, or "evt:{anchor}"); with <see cref="Source"/> it makes imports idempotent.</summary>
    public string? ExternalEventKey { get; set; }
    /// <summary>The Exported event this play grew out of, kept as evidence.</summary>
    public long? RelatedEventId { get; set; }
    /// <summary>Set when a Confirmed play replaced this Possible one; superseded rows are not counted twice.</summary>
    public long? SupersededByPlayId { get; set; }
}
