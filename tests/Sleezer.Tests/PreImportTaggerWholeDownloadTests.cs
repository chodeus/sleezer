using System.Reflection;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// The real tagger on real FLAC files; only Lidarr's services are faked.
public class PreImportTaggerWholeDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whole-download-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;

    public PreImportTaggerWholeDownloadTests()
    {
        _dir = Path.Combine(_root, "Artist - Wanted Album");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_root, true);

    public class Fake : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _handler = (_, _) => null;

        public static T Of<T>(Func<MethodInfo, object?[]?, object?> handler)
            where T : class
        {
            T proxy = Create<T, Fake>();
            ((Fake)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => _handler(method!, args);
    }

    private string[] Files(params string[] titles) =>
    [
        .. titles.Select((title, i) =>
        {
            string path = Path.Combine(_dir, $"{i + 1:00}.flac");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
            using TagLib.File file = TagLib.File.Create(path);
            file.Tag.Title = title;
            file.Tag.Album = "Store Album";
            file.Tag.Performers = ["Artist"];
            file.Save();
            return path;
        })
    ];

    private static string AlbumTag(string path)
    {
        using TagLib.File file = TagLib.File.Create(path);
        return file.Tag.Album;
    }

    private static void TagAsWanted(TrackFile trackFile)
    {
        using TagLib.File file = TagLib.File.Create(trackFile.Path);
        file.Tag.Album = "Wanted Album";
        file.Save();
    }

    private static Distance Dist(double value)
    {
        Distance distance = new();
        distance.Add("track_title", value);
        return distance;
    }

    private static (Album Album, Artist Artist, AlbumRelease Release) Target(string type, params string[] titles)
    {
        List<Track> tracks = [.. titles.Select((t, i) => new Track { Title = t, AbsoluteTrackNumber = i + 1 })];
        AlbumRelease release = new() { Title = "Wanted Album", TrackCount = tracks.Count, Monitored = true, Media = [new Medium { Number = 1, Format = "Digital Media" }], Tracks = new LazyLoaded<List<Track>>(tracks) };
        Artist artist = new() { Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Artist" }) };
        Album album = new() { Title = "Wanted Album", AlbumType = type, AnyReleaseOk = true, AlbumReleases = new LazyLoaded<List<AlbumRelease>>([release]), Artist = new LazyLoaded<Artist>(artist), SecondaryTypes = [] };
        return (album, artist, release);
    }

    private static List<LocalAlbumRelease> AllMapped(List<LocalTrack> files, AlbumRelease release, double distance)
    {
        TrackMapping mapping = new() { LocalExtra = [], MBExtra = [] };
        List<LocalTrack> ordered = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        for (int i = 0; i < ordered.Count; i++)
            mapping.Mapping.Add(ordered[i], Tuple.Create(release.Tracks.Value[i], Dist(0.0)));

        return [new LocalAlbumRelease(ordered) { Distance = Dist(distance), TrackMapping = mapping, AlbumRelease = release }];
    }

    private static PreImportTagger Tagger(AlbumRelease release, double distance, Action<TrackFile> writeTags)
    {
        IIdentificationService identify = Fake.Of<IIdentificationService>((_, a) => AllMapped((List<LocalTrack>)a![0]!, release, distance));
        IAudioTagService tags = Fake.Of<IAudioTagService>((m, a) =>
        {
            if (m.Name == nameof(IAudioTagService.ReadTags))
            {
                using TagLib.File file = TagLib.File.Create((string)a![0]!);
                return new ParsedTrackInfo { Title = file.Tag.Title, AlbumTitle = file.Tag.Album, ArtistTitle = "Artist", TrackNumbers = [0] };
            }

            if (m.Name == nameof(IAudioTagService.WriteTags))
                writeTags((TrackFile)a![0]!);

            return null;
        });
        IDiskProvider disk = Fake.Of<IDiskProvider>((m, a) => m.Name switch
        {
            nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a![0]!),
            nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)a![0]!, "*", SearchOption.AllDirectories),
            _ => throw new NotSupportedException(m.Name)
        });

        return new PreImportTagger(identify, tags, Fake.Of<IFingerprintingService>((m, _) => throw new NotSupportedException(m.Name)),
            Fake.Of<ITrackService>((m, _) => throw new NotSupportedException(m.Name)),
            Fake.Of<IReleaseService>((m, _) => throw new NotSupportedException(m.Name)),
            disk, Fake.Of<IMusicBrainzLookups>((_, _) => null), LogManager.GetLogger(nameof(PreImportTaggerWholeDownloadTests)));
    }

    private Task<PreImportTagger.TaggingResult> Tag(PreImportTagger tagger, Album album, Artist artist, CancellationToken ct) =>
        tagger.TagCompletedDownloadAsync(album, artist, null, "download", _dir, 0.15, false, ct);

    private string[] FolderNames() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order()!];

    [Fact]
    public async Task Tags_every_file_and_leaves_no_backup()
    {
        string[] paths = Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, TagAsWanted), album, artist, CancellationToken.None);

        Assert.Equal((3, 0, 0), (result.Tagged, result.SkippedWeakMatch, result.TagWriteFailed));
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
        Assert.Equal(["01.flac", "02.flac", "03.flac"], FolderNames());
    }

    [Fact]
    public async Task Puts_every_file_back_when_a_write_throws()
    {
        string[] paths = Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            if (tf.Path.EndsWith("02.flac", StringComparison.Ordinal))
                throw new IOException("file is locked");
            TagAsWanted(tf);
        });

        PreImportTagger.TaggingResult result = await Tag(tagger, album, artist, CancellationToken.None);

        Assert.Equal((0, 2, 1), (result.Tagged, result.SkippedWeakMatch, result.TagWriteFailed));
        Assert.Empty(result.TaggedFiles!);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
        Assert.Equal(["01.flac", "02.flac", "03.flac"], FolderNames());
    }

    // Lidarr's writer logs a failed save and returns, so the file just keeps its old tags.
    [Fact]
    public async Task Puts_every_file_back_when_a_write_silently_does_not_take()
    {
        string[] paths = Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            if (!tf.Path.EndsWith("02.flac", StringComparison.Ordinal))
                TagAsWanted(tf);
        });

        PreImportTagger.TaggingResult result = await Tag(tagger, album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
        Assert.Equal(["01.flac", "02.flac", "03.flac"], FolderNames());
    }

    [Fact]
    public async Task Finishes_the_pass_when_cancelled_while_writing()
    {
        string[] paths = Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        using CancellationTokenSource cts = new();
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            TagAsWanted(tf);
            cts.Cancel();
        });

        await Record.ExceptionAsync(() => Tag(tagger, album, artist, cts.Token));

        Assert.Single(paths.Select(AlbumTag).Distinct());
    }

    [Fact]
    public async Task Counts_each_file_once_when_no_pass_tags()
    {
        Files("Alpha", "Unrelated");
        (Album album, Artist artist, AlbumRelease release) = Target("Single", "Alpha", "Beta");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.40, _ => { }), album, artist, CancellationToken.None);

        Assert.Equal((0, 2, 0), (result.Tagged, result.SkippedWeakMatch, result.TagWriteFailed));
    }
}
