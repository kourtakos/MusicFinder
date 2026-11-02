namespace MusicFinder.Domain.Entities;

public sealed class Folder
{
    public long Id { get; set; }
    public long RootId { get; set; }
    public long? ParentId { get; set; }
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public int DirectTrackCount { get; set; }
    public int RecursiveTrackCount { get; set; }
}
