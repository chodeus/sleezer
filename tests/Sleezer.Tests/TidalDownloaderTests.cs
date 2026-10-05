using System.Net;
using System.Text;
using Newtonsoft.Json;
using NzbDrone.Common.Http;
using TidalSharp;
using TidalSharp.Data;
using TidalSharp.Downloading;
using TidalSharp.Exceptions;
using Xunit;

namespace Sleezer.Tests;

public class TidalDownloaderTests
{
    private static string Mpd(string timeline) => """
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT20S" type="static">
          <Period id="0">
            <AdaptationSet id="0" contentType="audio" mimeType="audio/mp4">
              <Representation id="FLAC,44100,16" codecs="flac" bandwidth="1000" audioSamplingRate="44100">
                <SegmentTemplate timescale="44100" initialization="https://cdn.invalid/0.mp4" media="https://cdn.invalid/$Number$.mp4" startNumber="1">
                  <SegmentTimeline>TIMELINE</SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """.Replace("TIMELINE", timeline);

    [Fact]
    public void A_single_run_timeline_lists_the_init_segment_then_every_media_segment()
    {
        string[] urls = new DashInfo(MPD.Parse(Mpd("""<S d="176128" r="2"/><S d="1000"/>"""))).ChunkUrls;

        Assert.Equal(["0", "1", "2", "3", "4"], urls.Select(SegmentNumber));
    }

    [Fact]
    public void Every_run_of_a_multi_run_timeline_counts_one_plus_its_repeats()
    {
        // 3 + 2 + 1 media segments; counting r alone dropped the last one and cut the track short.
        string[] urls = new DashInfo(MPD.Parse(Mpd("""<S d="176128" r="2"/><S d="88064" r="1"/><S d="1000"/>"""))).ChunkUrls;

        Assert.Equal(["0", "1", "2", "3", "4", "5", "6"], urls.Select(SegmentNumber));
    }

    [Fact]
    public async Task Segments_are_written_whole_and_in_order()
    {
        string path = TempTrack();
        try
        {
            await DownloaderFor(Tidal()).WriteRawTrackToFile("1", AudioQuality.LOSSLESS, path);

            Assert.Equal("IAB", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_failed_segment_fails_the_track_and_leaves_no_file()
    {
        string path = TempTrack();

        await Assert.ThrowsAsync<APIException>(() => DownloaderFor(Tidal(lastSegment: HttpStatusCode.Forbidden)).WriteRawTrackToFile("1", AudioQuality.LOSSLESS, path));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task An_atmos_stream_is_refused()
    {
        var ex = await Assert.ThrowsAsync<APIException>(() => DownloaderFor(Tidal(audioMode: "DOLBY_ATMOS")).WriteRawTrackToFile("1", AudioQuality.LOSSLESS, TempTrack()));

        Assert.Contains("DOLBY_ATMOS", ex.Message);
    }

    [Fact]
    public async Task A_track_is_tagged_with_its_isrc_copyright_bpm_and_composers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sleezer-{Guid.NewGuid():N}.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        try
        {
            await DownloaderFor(Catalogue()).ApplyMetadataToFile("1", path, composers: ["Ann Writer"]);

            using var tagged = TagLib.File.Create(path);
            Assert.Equal("USABC2600001", tagged.Tag.ISRC);
            Assert.Equal("℗ 2026 Example Records", tagged.Tag.Copyright);
            Assert.Equal(128u, tagged.Tag.BeatsPerMinute);
            Assert.Equal(["Ann Writer"], tagged.Tag.Composers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_non_json_answer_is_an_api_error()
    {
        var http = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.Forbidden, "<html>Access denied</html>"));
        var session = new Session(http);

        await Assert.ThrowsAsync<APIException>(() => new API(http, session).GetTrack("1"));
    }

    // Track 1 on album 9; artwork answers 401, which tags without a cover.
    private static FakeHttpClient Catalogue() => new(r =>
    {
        string path = new Uri(r.Url.ToString()).AbsolutePath;
        return path switch
        {
            "/v1/tracks/1" => FakeHttpClient.Respond(r, HttpStatusCode.OK, """
                {"id":1,"title":"Song","artists":[{"name":"Artist"}],"album":{"id":9,"cover":"ab-cd"},
                 "trackNumber":1,"volumeNumber":1,"isrc":"USABC2600001","copyright":"℗ 2026 Example Records","bpm":128}
                """),
            "/v1/albums/9" => FakeHttpClient.Respond(r, HttpStatusCode.OK, """
                {"id":9,"title":"Album","artists":[{"name":"Artist"}],"releaseDate":"2026-01-02","numberOfTracks":1,"numberOfVolumes":1}
                """),
            _ => FakeHttpClient.Respond(r, HttpStatusCode.Unauthorized)
        };
    });

    private static string SegmentNumber(string url) => Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);

    private static string TempTrack() => Path.Combine(Path.GetTempPath(), $"sleezer-{Guid.NewGuid():N}.m4a");

    private static Downloader DownloaderFor(IHttpClient http)
    {
        var session = new Session(http);
        return new Downloader(http, new API(http, session), session);
    }

    // Playback info for a two-segment DASH FLAC stream, then the init segment and both media segments.
    private static FakeHttpClient Tidal(string audioMode = "STEREO", HttpStatusCode lastSegment = HttpStatusCode.OK)
    {
        string playback = JsonConvert.SerializeObject(new
        {
            trackId = 1,
            assetPresentation = "FULL",
            audioMode,
            audioQuality = "LOSSLESS",
            manifestMimeType = "application/dash+xml",
            manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(Mpd("""<S d="176128" r="1"/>"""))),
            bitDepth = 16,
            sampleRate = 44100
        });

        return new FakeHttpClient(r =>
        {
            string url = r.Url.ToString();
            if (url.Contains("playbackinfopostpaywall", StringComparison.Ordinal))
                return FakeHttpClient.Respond(r, HttpStatusCode.OK, playback);
            if (url.EndsWith("/0.mp4", StringComparison.Ordinal))
                return FakeHttpClient.Respond(r, HttpStatusCode.OK, "I");
            if (url.EndsWith("/1.mp4", StringComparison.Ordinal))
                return FakeHttpClient.Respond(r, HttpStatusCode.OK, "A");
            if (url.EndsWith("/2.mp4", StringComparison.Ordinal))
                return FakeHttpClient.Respond(r, lastSegment, lastSegment == HttpStatusCode.OK ? "B" : "<Error>AccessDenied</Error>");

            return FakeHttpClient.Respond(r, HttpStatusCode.NotFound);
        });
    }
}
