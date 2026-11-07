using System.Text.Json;
using System.Text.Json.Serialization;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.ImportLists.LastFmRecommendation;
using Xunit;

namespace Sleezer.Tests;

// One malformed value must not fail a whole store response (Tubifarry 666b256, 5cd42bd).
public class StoreJsonParsingTests
{
    private sealed record Dated(
        [property: JsonPropertyName("release_date"), JsonConverter(typeof(LenientDateTimeConverter))] DateTime? ReleaseDate);

    private static readonly JsonSerializerOptions LastFmOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("\"0000-00-00\"")]
    [InlineData("\"\"")]
    [InlineData("\"soon\"")]
    [InlineData("0")]
    [InlineData("null")]
    public void An_unknown_release_date_reads_as_no_date(string value)
    {
        Assert.Null(JsonSerializer.Deserialize<Dated>($"{{\"release_date\":{value}}}")!.ReleaseDate);
    }

    [Fact]
    public void A_release_date_reads_as_its_day()
    {
        Assert.Equal(new DateTime(2020, 5, 1), JsonSerializer.Deserialize<Dated>("{\"release_date\":\"2020-05-01\"}")!.ReleaseDate);
    }

    [Theory]
    [InlineData("215")]
    [InlineData("\"215\"")]
    public void A_lastfm_track_duration_reads_as_a_number_or_a_string(string value)
    {
        string json = $"{{\"name\":\"n\",\"duration\":{value},\"url\":\"u\",\"artist\":{{\"name\":\"a\"}}}}";

        Assert.Equal(215, JsonSerializer.Deserialize<LastFmTrack>(json, LastFmOptions)!.Duration);
    }
}
