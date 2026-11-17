using MusicFinder.Application.Search;

namespace MusicFinder.UnitTests;

public class SavedSearchStateTests
{
    [Fact]
    public void RoundTrips_EveryFieldIncludingAnOpenEndedBpmRange()
    {
        var state = new SavedSearchState
        {
            Text = "genre:disco Χάρις",
            Genres = [new SavedGenreRef(0, 7), new SavedGenreRef(1, 0)],
            TagIds = [3, 4],
            Decades = [1980, 1990],
            BpmMin = 160,
            BpmMax = double.MaxValue,
            PlayedId = 2,
            HideConfirmed = true,
            HidePossible = false,
            SortField = 4,
            SortDirection = 1
        };

        var back = SavedSearchState.TryParse(state.ToJson())!;

        Assert.Equal(state.Text, back.Text);
        Assert.Equal(state.Genres, back.Genres);
        Assert.Equal(state.TagIds, back.TagIds);
        Assert.Equal(state.Decades, back.Decades);
        Assert.Equal((160d, double.MaxValue), (back.BpmMin, back.BpmMax));
        Assert.Equal((2, true, false, 4, 1), (back.PlayedId, back.HideConfirmed, back.HidePossible, back.SortField, back.SortDirection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Version\":2,\"Text\":\"x\"}")]   // from a newer version: not guessed at
    [InlineData("[1,2,3]")]
    public void DamagedOrUnknownText_IsNotASavedSearch(string? json) => Assert.Null(SavedSearchState.TryParse(json));

    [Fact]
    public void IsEmpty_OnlyWhenNothingWouldBeSearchedOrFiltered()
    {
        Assert.True(new SavedSearchState().IsEmpty);
        Assert.True(new SavedSearchState { SortField = 3 }.IsEmpty);          // a sort alone is not a search
        Assert.False(new SavedSearchState { Text = "x" }.IsEmpty);
        Assert.False(new SavedSearchState { HidePossible = true }.IsEmpty);
        Assert.False(new SavedSearchState { Decades = [1980] }.IsEmpty);
    }
}
