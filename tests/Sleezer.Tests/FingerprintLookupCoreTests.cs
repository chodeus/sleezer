using System.IO.Compression;
using System.Net;
using System.Text;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// FingerprintLookup against Lidarr core's real FingerprintingService: request building, error handling and
// response parsing are core's; only HTTP is faked, and fpcalc is swapped for a fixed fingerprint per file.
public class FingerprintLookupCoreTests
{
    private static readonly Logger Log = LogManager.CreateNullLogger();

    // AcoustID answering each fingerprint with its own recording; a request it is told to drop throws as a timeout does.
    private sealed class AcoustIdHttp(Func<int, bool> timesOut, int delayMs) : IHttpClient
    {
        public List<HttpRequest> Requests { get; } = [];

        public HttpResponse<T> Post<T>(HttpRequest request)
            where T : new()
        {
            int n;
            lock (Requests)
            {
                Requests.Add(request);
                n = Requests.Count - 1;
            }

            if (timesOut(n))
            {
                Thread.Sleep(delayMs);
                throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout);
            }

            using GZipStream gz = new(new MemoryStream(request.ContentData), CompressionMode.Decompress);
            using StreamReader reader = new(gz, Encoding.UTF8);
            List<string> prints = [.. reader.ReadToEnd().Split('&').Where(p => p.StartsWith("fingerprint.")).Select(p => p[(p.IndexOf('=') + 1)..])];
            string body = "{\"status\":\"ok\",\"fingerprints\":[" + string.Join(",", prints.Select((p, i) =>
                $"{{\"index\":{i},\"results\":[{{\"id\":\"t{i}\",\"score\":0.9,\"recordings\":[{{\"id\":\"rec-{p}\"}}]}}]}}")) + "]}";
            return new HttpResponse<T>(new HttpResponse(request, new HttpHeader(), body, HttpStatusCode.OK));
        }

        HttpResponse IHttpClient.Execute(HttpRequest request) => throw new NotSupportedException();
        void IHttpClient.DownloadFile(string url, string fileName) => throw new NotSupportedException();
        HttpResponse IHttpClient.Get(HttpRequest request) => throw new NotSupportedException();
        HttpResponse<T> IHttpClient.Get<T>(HttpRequest request) => throw new NotSupportedException();
        HttpResponse IHttpClient.Head(HttpRequest request) => throw new NotSupportedException();
        HttpResponse IHttpClient.Post(HttpRequest request) => throw new NotSupportedException();
        Task<HttpResponse> IHttpClient.ExecuteAsync(HttpRequest request) => throw new NotSupportedException();
        Task IHttpClient.DownloadFileAsync(string url, string fileName) => throw new NotSupportedException();
        Task<HttpResponse> IHttpClient.GetAsync(HttpRequest request) => throw new NotSupportedException();
        Task<HttpResponse<T>> IHttpClient.GetAsync<T>(HttpRequest request) => throw new NotSupportedException();
        Task<HttpResponse> IHttpClient.HeadAsync(HttpRequest request) => throw new NotSupportedException();
        Task<HttpResponse> IHttpClient.PostAsync(HttpRequest request) => throw new NotSupportedException();
        Task<HttpResponse<T>> IHttpClient.PostAsync<T>(HttpRequest request) => throw new NotSupportedException();
    }

    private sealed class CoreWithoutFpcalc(FingerprintingService core) : IFingerprintingService
    {
        public bool IsSetup() => true;
        public Version FpcalcVersion() => new(1, 5, 1);
        public void Lookup(List<LocalTrack> tracks, double threshold) =>
            core.Lookup([.. tracks.Select(t => Tuple.Create(t, new AcoustId { Duration = 200, Fingerprint = "AQ" + Path.GetFileName(t.Path)[..2] }))], threshold);
    }

    private static List<LocalTrack> Files(int count) => [.. Enumerable.Range(1, count).Select(i => new LocalTrack { Path = $"/downloads/Moonward/{i:00} - Lantern Song.flac" })];

    [Fact]
    public void The_retry_waits_out_the_timeout_Lidarr_sets_on_its_AcoustID_request()
    {
        AcoustIdHttp http = new(_ => false, 0);

        new CoreWithoutFpcalc(new FingerprintingService(Log, http, new CacheManager())).Lookup(Files(1), 0.5);

        Assert.Equal(FingerprintLookup.RequestTimeout, Assert.Single(http.Requests).RequestTimeout);
    }

    [Fact]
    public async Task A_request_Lidarr_drops_as_timed_out_is_sent_once_more()
    {
        List<LocalTrack> tracks = Files(10);
        AcoustIdHttp http = new(n => n == 0, 150);
        CoreWithoutFpcalc acoustId = new(new FingerprintingService(Log, http, new CacheManager()));

        await FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        Assert.All(tracks, t => Assert.Equal([$"rec-AQ{Path.GetFileName(t.Path)[..2]}"], t.AcoustIdResults));
    }

    [Fact]
    public async Task A_request_Lidarr_drops_twice_leaves_the_files_unverifiable()
    {
        List<LocalTrack> tracks = Files(10);
        AcoustIdHttp http = new(_ => true, 150);
        CoreWithoutFpcalc acoustId = new(new FingerprintingService(Log, http, new CacheManager()));

        await FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        Assert.All(tracks, t => Assert.Null(t.AcoustIdResults));
    }
}
