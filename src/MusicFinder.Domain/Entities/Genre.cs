namespace MusicFinder.Domain.Entities;

public sealed class Genre
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsBuiltIn { get; set; }
}

public sealed class GenreAlias
{
    public string AliasNormalized { get; set; } = "";
    public long GenreId { get; set; }
}

public sealed class CustomTag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}
