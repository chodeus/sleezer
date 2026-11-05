using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;
using Fake = Sleezer.Tests.PreImportTaggerWholeDownloadTests.Fake;

namespace Sleezer.Tests;

// The real tagger and Lidarr's real distance gate; only identification, AcoustID and the database are faked.
public class FingerprintHoldTaggingTests : IDisposable
{
    private static readonly Logger Log = LogManager.CreateNullLogger();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fingerprint-hold-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;

    public FingerprintHoldTaggingTests()
    {
        _dir = Path.Combine(_root, "Artist - Wanted Album");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string[] Files(params string[] titles) => [.. titles.Select((title, i) =>
    {
        string path = Path.Combine(_dir, $"{i + 1:00}.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        using TagLib.File file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Album = "Store Album";
        file.Tag.Performers = ["Artist"];
        file.Save();
        return path;
    })];

    private static Distance Dist(double value)
    {
        Distance distance = new();
        distance.Add("track_title", value);
        return distance;
    }

    private static (Album Album, Artist Artist, AlbumRelease Release) Target(params string[] titles)
    {
        List<Track> list = [.. titles.Select((t, i) => new Track { Title = t, AbsoluteTrackNumber = i + 1, ForeignRecordingId = $"rec-{i + 1}", Duration = 200_000 })];
        AlbumRelease release = new() { Title = "Wanted Album", ForeignReleaseId = "wanted-release", TrackCount = list.Count, Monitored = true, Media = [new Medium { Number = 1, Format = "Digital Media" }], Tracks = new LazyLoaded<List<Track>>(list) };
        Artist artist = new() { Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Artist" }) };
        Album album = new() { Title = "Wanted Album", AlbumType = "Album", AnyReleaseOk = true, AlbumReleases = new LazyLoaded<List<AlbumRelease>>([release]), Artist = new LazyLoaded<Artist>(artist), SecondaryTypes = [] };
        return (album, artist, release);
    }

    // Lidarr's identification of the download: album distance, and file 01's own track distance.
    private static List<LocalAlbumRelease> Identified(List<LocalTrack> files, AlbumRelease release, double albumDistance, double firstTrackDistance)
    {
        TrackMapping mapping = new() { LocalExtra = [], MBExtra = [] };
        List<LocalTrack> ordered = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        for (int i = 0; i < ordered.Count; i++)
        {
            Distance d = Dist(i == 0 ? firstTrackDistance : 0.0);
            ordered[i].Distance = d;
            mapping.Mapping.Add(ordered[i], Tuple.Create(release.Tracks.Value[i], d));
        }

        return [new LocalAlbumRelease(ordered) { Distance = Dist(albumDistance), TrackMapping = mapping, AlbumRelease = release }];
    }

    private static PreImportTagger Tagger(AlbumRelease release, double albumDistance, double firstTrackDistance, List<LocalAlbumRelease> seen)
    {
        IIdentificationService identify = Fake.Of<IIdentificationService>((_, a) =>
        {
            List<LocalAlbumRelease> r = Identified((List<LocalTrack>)a![0]!, release, albumDistance, firstTrackDistance);
            seen.Clear();
            seen.AddRange(r);
            return r;
        });
        IAudioTagService tags = Fake.Of<IAudioTagService>((m, a) =>
        {
            if (m.Name == nameof(IAudioTagService.ReadTags))
            {
                using TagLib.File file = TagLib.File.Create((string)a![0]!);
                return new ParsedTrackInfo { Title = file.Tag.Title, AlbumTitle = file.Tag.Album, ArtistTitle = "Artist", TrackNumbers = [0], Duration = TimeSpan.FromSeconds(200) };
            }

            return null;
        });
        IDiskProvider disk = Fake.Of<IDiskProvider>((m, a) => m.Name switch
        {
            nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a![0]!),
            nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)a![0]!, "*", SearchOption.AllDirectories),
            _ => throw new NotSupportedException(m.Name)
        });

        // AcoustID hears file 01 as "Ember Gate", a different song; file 02 as the wanted track 2.
        IFingerprintingService acoustId = Fake.Of<IFingerprintingService>((m, a) =>
        {
            if (m.Name == nameof(IFingerprintingService.Lookup))
            {
                foreach (LocalTrack track in (List<LocalTrack>)a![0]!)
                    track.AcoustIdResults = [Path.GetFileName(track.Path) == "01.flac" ? "rec-heard" : "rec-2"];
            }

            return m.Name == nameof(IFingerprintingService.IsSetup) ? true : null;
        });
        IReleaseService releases = Fake.Of<IReleaseService>((_, _) => new List<AlbumRelease> { new() { Id = 99 } });
        ITrackService trackService = Fake.Of<ITrackService>((_, _) => release.Tracks.Value
            .Select(t => new Track { Title = t.Title, ForeignRecordingId = t.ForeignRecordingId, Duration = t.Duration })
            .Append(new Track { Title = "Ember Gate", ForeignRecordingId = "rec-heard", Duration = 201_000 }).ToList());

        return new PreImportTagger(identify, tags, acoustId, trackService, releases, disk, Fake.Of<IMusicBrainzLookups>((_, _) => null), Log);
    }

    private Task<PreImportTagger.TaggingResult> TagAsSoulseek(PreImportTagger tagger, Album album, Artist artist) =>
        tagger.TagCompletedDownloadAsync(album, artist, null, "download", _dir, 0.15 /* PostProcessRunner.TagConfidenceThreshold */, false, CancellationToken.None,
            verifyAllWithFingerprint: true, fingerprintTitleFallback: true, preferDigitalMedia: true);

    [Fact]
    public async Task A_close_match_with_a_wrong_song_is_held()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");
        List<LocalAlbumRelease> seen = [];

        PreImportTagger.TaggingResult result = await TagAsSoulseek(Tagger(release, 0.05, 0.05, seen), album, artist);

        Assert.NotNull(ImportHolds.ReasonToHold(result, verifying: true, new HashSet<string>(paths)));
    }

    // Looser than sleezer's 0.15 but within Lidarr's import limits (album 0.20, track 0.40): still held.
    [Theory]
    [InlineData(0.18, 0.05)] // album distance between sleezer's 0.15 and Lidarr's 0.20
    [InlineData(0.05, 0.30)] // file 01's own match between sleezer's 0.15 and Lidarr's 0.40
    public async Task A_wrong_song_in_a_match_only_Lidarr_accepts_is_held(double albumDistance, double firstTrackDistance)
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");
        List<LocalAlbumRelease> seen = [];

        PreImportTagger.TaggingResult result = await TagAsSoulseek(Tagger(release, albumDistance, firstTrackDistance, seen), album, artist);

        // Lidarr's own gate on the very release the tagger saw: accepted, so it imports.
        Assert.True(new CloseAlbumMatchSpecification(Log).IsSatisfiedBy(seen.Single(), null!).Accepted);
        Assert.Equal(0, result.Tagged);
        Assert.NotNull(ImportHolds.ReasonToHold(result, verifying: true, new HashSet<string>(paths)));
    }

    // Lidarr won't import these (album over 0.20, or a track over 0.40), so a hold would only mislead.
    [Theory]
    [InlineData(0.30, 0.05)]
    [InlineData(0.05, 0.50)]
    public async Task A_wrong_song_in_a_match_Lidarr_rejects_is_not_held(double albumDistance, double firstTrackDistance)
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");
        List<LocalAlbumRelease> seen = [];

        PreImportTagger.TaggingResult result = await TagAsSoulseek(Tagger(release, albumDistance, firstTrackDistance, seen), album, artist);

        Assert.Null(ImportHolds.ReasonToHold(result, verifying: true, new HashSet<string>(paths)));
        Assert.False(new CloseAlbumMatchSpecification(Log).IsSatisfiedBy(seen.Single(), null!).Accepted);
    }
}
