using System.Text.Json;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.MediaCover;
using NzbDrone.Plugin.Sleezer.Core.Replacements;
using Xunit;

namespace Sleezer.Tests;

// Lidarr derives a cover's file extension from its whole URL, and again each time it loads the cover from the database.
public class UserAgentCoverUrlTests
{
    private const string BrowserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    // Same serialize/parse pair Lidarr uses for the Images column.
    private sealed class ImagesColumn : EmbeddedDocumentConverter<List<MediaCover>>
    {
        public List<MediaCover> RoundTrip(List<MediaCover> covers) => Parse(JsonSerializer.Serialize((object)covers, SerializerSettings));
    }

    [Theory]
    [InlineData("https://cdn.example/images/cover/abc/500x500.jpg", BrowserAgent)]
    [InlineData("https://cdn.example/i/u/def.png", "MyApp/1.0.0")]
    [InlineData("https://img.example/rs:fit/w:150/ghi.jpeg", "MyApp/2.1 (+https://example.org/myapp)")]
    public void a_cover_keeps_its_file_extension(string url, string userAgent)
    {
        MediaCover cover = new(MediaCoverTypes.Cover, FlexibleHttpDispatcher.WithUserAgent(url, userAgent));

        Assert.Equal(Path.GetExtension(url), cover.Extension);
        Assert.Equal(Path.GetExtension(url), new ImagesColumn().RoundTrip([cover]).Single().Extension);
    }

    [Theory]
    [InlineData("https://cdn.example/images/cover/abc/500x500.jpg", BrowserAgent)]
    [InlineData("https://cdn.example/i/u/def.png", "MyApp/1.0.0")]
    [InlineData("https://cdn.example/artist/noext", "MyApp/1.0.0")]
    public void the_request_goes_out_clean_with_the_agent_as_a_header(string url, string userAgent)
    {
        HttpRequest request = new(FlexibleHttpDispatcher.WithUserAgent(url, userAgent));

        FlexibleHttpDispatcher.ExtractUserAgentFromUrl(request);

        Assert.Equal(userAgent, request.Headers.GetSingleValue("User-Agent"));
        Assert.Equal(url, request.Url.FullUri);
    }

    [Fact]
    public void a_url_stored_before_the_extension_marker_still_works()
    {
        HttpRequest request = new("https://cdn.example/i/u/def.jpg?x-user-agent=MyApp/1.0.0");

        FlexibleHttpDispatcher.ExtractUserAgentFromUrl(request);

        Assert.Equal("MyApp/1.0.0", request.Headers.GetSingleValue("User-Agent"));
        Assert.Equal("https://cdn.example/i/u/def.jpg", request.Url.FullUri);
    }
}
