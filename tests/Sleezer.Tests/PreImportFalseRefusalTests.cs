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
using Fake = Sleezer.Tests.PreImportTaggerWholeDownloadTests.Fake;

namespace Sleezer.Tests;

// Real VariantQualifiers / RecordingVerdict / PreImportTagger; only Lidarr services are faked.
public class PreImportFalseRefusalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "false-refusals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    // The file is the innermost component, so its own cut decides over its folder's.
    [Fact]
    public void A_file_naming_the_wanted_cut_overrules_a_folder_naming_another()
    {
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Lantern Song (Mono)", ["Lantern Song (Stereo)"], null), "precondition: the folder alone conflicts");
        Assert.False(VariantQualifiers.DownloadConflicts("Lantern Song (Mono)", "Lantern Song (Stereo)", ["01 Lantern Song (Mono)"], null));
    }

    [Theory]
    [InlineData("Lantern Song (Extended Mix) (Mono)", "Lantern Song (Mono)", "01 Lantern Song (Extended Mix) (Stereo)")]
    public void A_file_naming_another_cut_than_its_folder_conflicts(string wanted, string folder, string stem)
    {
        Assert.True(VariantQualifiers.RemixSignaturesConflict(wanted, [stem], null), "precondition: the file alone conflicts");
        Assert.True(VariantQualifiers.DownloadConflicts(wanted, folder, [stem], null));
    }

    [Fact]
    public async Task A_stereo_file_in_a_mono_folder_stays_untagged()
    {
        string dir = Path.Combine(_root, "Lantern Song (Mono)");
        Directory.CreateDirectory(dir);
        string path = NewFile(dir, "01 Lantern Song (Extended Mix) (Stereo).flac", "Lantern Song (Extended Mix) (Stereo)");
        (Album album, Artist artist, AlbumRelease release) = Target("Lantern Song (Extended Mix) (Mono)");

        PreImportTagger tagger = Tagger(_ => Mapped(release, 0.0));
        PreImportTagger.TaggingResult result = await tagger.TagCompletedDownloadAsync(album, artist, null, "download", dir, 0.15, false, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.Equal("Store Album", AlbumTag(path));
    }

    // Identify returns the fingerprint-verified release at 0.158 unforced and the monitored one at 0.30 when forced.
    [Fact]
    public async Task A_release_the_fingerprint_hears_whole_survives_the_monitored_fallback()
    {
        string dir = Path.Combine(_root, "Artist - Wanted Album");
        Directory.CreateDirectory(dir);
        string[] paths = [NewFile(dir, "01.flac", "Lantern Song"), NewFile(dir, "02.flac", "Salt Road")];

        AlbumRelease heard = Release("heard-release", false, "Lantern Song", "Salt Road");
        AlbumRelease monitored = Release("monitored-release", true, "Lantern Song", "Salt Road", "Ember Gate");
        Artist artist = new() { Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Artist" }) };
        Album album = new() { Title = "Wanted Album", AlbumType = "Album", AnyReleaseOk = true, AlbumReleases = new LazyLoaded<List<AlbumRelease>>([monitored, heard]), Artist = new LazyLoaded<Artist>(artist), SecondaryTypes = [] };

        IFingerprintingService acoustId = Fake.Of<IFingerprintingService>((m, a) =>
        {
            if (m.Name == nameof(IFingerprintingService.Lookup))
            {
                foreach (LocalTrack t in (List<LocalTrack>)a![0]!)
                    t.AcoustIdResults = [Path.GetFileName(t.Path) == "01.flac" ? "rec-1" : "rec-2"];
            }

            return m.Name == nameof(IFingerprintingService.IsSetup) ? true : null;
        });
        IReleaseService releases = Fake.Of<IReleaseService>((_, _) => new List<AlbumRelease> { new() { Id = 99 } });
        ITrackService tracks = Fake.Of<ITrackService>((_, _) => heard.Tracks.Value.ToList());

        PreImportTagger tagger = Tagger(o => o.AlbumRelease == null ? Mapped(heard, 0.158) : Mapped(monitored, 0.30),
            acoustId, releases, tracks);
        PreImportTagger.TaggingResult result = await tagger.TagCompletedDownloadAsync(album, artist, null, "download", dir, 0.15, false, CancellationToken.None,
            verifyAllWithFingerprint: true, fingerprintTitleFallback: false);

        Assert.Equal(2, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
    }

    // A wanted instrumental that also says "(Clean)" must not defer to the vocal cut 6 s off.
    [Fact]
    public void A_clean_instrumental_keeps_the_instrumental_rule()
    {
        FingerprintVerdict verdict = RecordingVerdict.Resolve(["rec-other"], TimeSpan.FromSeconds(216),
            new WantedRecording("rec-wanted", null, "Lantern Song (Clean) (Instrumental)", 222_000, ["Artist"]),
            new Dictionary<string, IReadOnlyList<KnownTrack>> { ["rec-other"] = [new KnownTrack("Lantern Song", 220_000)] });

        Assert.Equal(FingerprintVerdict.Mismatch, verdict);
    }

    // AcoustID names the instrumental recording; the file is 6 s off a wanted clean edit.
    [Fact]
    public void A_clean_edit_does_not_defer_to_an_instrumental_recording()
    {
        FingerprintVerdict verdict = RecordingVerdict.Resolve(["rec-other"], TimeSpan.FromSeconds(216),
            new WantedRecording("rec-wanted", null, "Lantern Song (Clean)", 222_000, ["Artist"]),
            new Dictionary<string, IReadOnlyList<KnownTrack>> { ["rec-other"] = [new KnownTrack("Lantern Song (Instrumental)", 220_000)] });

        Assert.Equal(FingerprintVerdict.Mismatch, verdict);
    }

    private static AlbumRelease Release(string id, bool monitored, params string[] titles)
    {
        List<Track> list = [.. titles.Select((t, i) => new Track { Title = t, AbsoluteTrackNumber = i + 1, ForeignRecordingId = $"rec-{i + 1}", Duration = 200_000 })];
        return new AlbumRelease { Title = "Wanted Album", ForeignReleaseId = id, TrackCount = list.Count, Monitored = monitored, Media = [new Medium { Number = 1, Format = "CD" }], Tracks = new LazyLoaded<List<Track>>(list) };
    }

    private static (Album, Artist, AlbumRelease) Target(params string[] titles)
    {
        AlbumRelease release = Release("wanted-release", true, titles);
        Artist artist = new() { Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Artist" }) };
        Album album = new() { Title = titles[0], AlbumType = "Single", AnyReleaseOk = true, AlbumReleases = new LazyLoaded<List<AlbumRelease>>([release]), Artist = new LazyLoaded<Artist>(artist), SecondaryTypes = [] };
        return (album, artist, release);
    }

    private static Distance Dist(double v)
    {
        Distance d = new();
        d.Add("track_title", v);
        return d;
    }

    private static Func<List<LocalTrack>, List<LocalAlbumRelease>> Mapped(AlbumRelease release, double distance) => files =>
    {
        List<LocalTrack> ordered = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        TrackMapping mapping = new() { LocalExtra = [], MBExtra = [.. release.Tracks.Value.Skip(ordered.Count)] };
        for (int i = 0; i < ordered.Count && i < release.Tracks.Value.Count; i++)
        {
            ordered[i].Distance = Dist(0.0);
            mapping.Mapping.Add(ordered[i], Tuple.Create(release.Tracks.Value[i], ordered[i].Distance));
        }

        return [new LocalAlbumRelease(ordered) { Distance = Dist(distance), TrackMapping = mapping, AlbumRelease = release }];
    };

    private static PreImportTagger Tagger(Func<IdentificationOverrides, Func<List<LocalTrack>, List<LocalAlbumRelease>>> identifyBy,
        IFingerprintingService? acoustId = null, IReleaseService? releases = null, ITrackService? tracks = null, IMusicBrainzLookups? musicBrainz = null)
    {
        IIdentificationService identify = Fake.Of<IIdentificationService>((_, a) => identifyBy((IdentificationOverrides)a![1]!)((List<LocalTrack>)a[0]!));
        IAudioTagService tags = Fake.Of<IAudioTagService>((m, a) =>
        {
            if (m.Name == nameof(IAudioTagService.ReadTags))
            {
                using TagLib.File file = TagLib.File.Create((string)a![0]!);
                return new ParsedTrackInfo { Title = file.Tag.Title, AlbumTitle = file.Tag.Album, ArtistTitle = "Artist", TrackNumbers = [0], Duration = TimeSpan.FromSeconds(200) };
            }

            if (m.Name == nameof(IAudioTagService.WriteTags))
            {
                TrackFile tf = (TrackFile)a![0]!;
                using TagLib.File file = TagLib.File.Create(tf.Path);
                file.Tag.Album = "Wanted Album";
                file.Tag.MusicBrainzReleaseId = tf.Tracks.Value[0].AlbumRelease.Value.ForeignReleaseId;
                file.Tag.MusicBrainzTrackId = tf.Tracks.Value[0].ForeignRecordingId;
                file.Save();
            }

            return null;
        });
        IDiskProvider disk = Fake.Of<IDiskProvider>((m, a) => m.Name switch
        {
            nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a![0]!),
            nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)a![0]!, "*", SearchOption.AllDirectories),
            _ => throw new NotSupportedException(m.Name)
        });

        return new PreImportTagger(identify, tags, acoustId ?? Fake.Of<IFingerprintingService>((m, _) => throw new NotSupportedException(m.Name)),
            tracks ?? Fake.Of<ITrackService>((m, _) => throw new NotSupportedException(m.Name)),
            releases ?? Fake.Of<IReleaseService>((m, _) => throw new NotSupportedException(m.Name)),
            disk, musicBrainz ?? Fake.Of<IMusicBrainzLookups>((_, _) => null), LogManager.GetLogger(nameof(PreImportFalseRefusalTests)));
    }

    private static string NewFile(string dir, string name, string title)
    {
        string path = Path.Combine(dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        using TagLib.File file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Album = "Store Album";
        file.Tag.Performers = ["Artist"];
        file.Save();
        return path;
    }

    private static string AlbumTag(string path)
    {
        using TagLib.File file = TagLib.File.Create(path);
        return file.Tag.Album;
    }
}
