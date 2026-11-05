using System.Reflection;
using System.Runtime.Versioning;
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
    private const string WantedReleaseId = "wanted-release";

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

    private string NewFile(string name, string title) => NewFileIn(_dir, name, title);

    private static string NewFileIn(string dir, string name, string title)
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

    private string[] Files(params string[] titles) => [.. titles.Select((title, i) => NewFile($"{i + 1:00}.flac", title))];

    private static string AlbumTag(string path)
    {
        using TagLib.File file = TagLib.File.Create(path);
        return file.Tag.Album;
    }

    // As Lidarr's writer: the album title and the release id are the keys it groups a download by.
    private static void TagAsWanted(TrackFile trackFile)
    {
        using TagLib.File file = TagLib.File.Create(trackFile.Path);
        file.Tag.Album = "Wanted Album";
        file.Tag.MusicBrainzReleaseId = WantedReleaseId;
        file.Tag.MusicBrainzTrackId = trackFile.Tracks.Value[0].ForeignRecordingId;
        file.Save();
    }

    private static Distance Dist(double value)
    {
        Distance distance = new();
        distance.Add("track_title", value);
        return distance;
    }

    private static (Album Album, Artist Artist, AlbumRelease Release) Target(string type, params (string Title, int Number)[] tracks)
    {
        List<Track> list = [.. tracks.Select(t => new Track { Title = t.Title, AbsoluteTrackNumber = t.Number, ForeignRecordingId = $"rec-{t.Number}", Duration = 200_000 })];
        AlbumRelease release = new() { Title = "Wanted Album", ForeignReleaseId = WantedReleaseId, TrackCount = list.Count, Monitored = true, Media = [new Medium { Number = 1, Format = "Digital Media" }], Tracks = new LazyLoaded<List<Track>>(list) };
        Artist artist = new() { Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = "Artist" }) };
        Album album = new() { Title = "Wanted Album", AlbumType = type, AnyReleaseOk = true, AlbumReleases = new LazyLoaded<List<AlbumRelease>>([release]), Artist = new LazyLoaded<Artist>(artist), SecondaryTypes = [] };
        return (album, artist, release);
    }

    private static (Album Album, Artist Artist, AlbumRelease Release) Target(string type, params string[] titles) =>
        Target(type, [.. titles.Select((t, i) => (t, i + 1))]);

    private static List<LocalAlbumRelease> AllMapped(List<LocalTrack> files, AlbumRelease release, double distance)
    {
        TrackMapping mapping = new() { LocalExtra = [], MBExtra = [] };
        List<LocalTrack> ordered = [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
        // Each file carries its own match distance, as Lidarr's identification sets it (LocalAlbumRelease.PopulateMatch).
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Distance = Dist(0.0);
            mapping.Mapping.Add(ordered[i], Tuple.Create(release.Tracks.Value[i], ordered[i].Distance));
        }

        return [new LocalAlbumRelease(ordered) { Distance = Dist(distance), TrackMapping = mapping, AlbumRelease = release }];
    }

    // AcoustID naming one recording for every file; Lidarr holds it under the title given.
    private static PreImportTagger Fingerprinted(AlbumRelease release, Func<LocalTrack, string> recordingOf, string recordingTitle,
        string? alsoHeard = null, IMusicBrainzLookups? musicBrainz = null, double distance = 0.0)
    {
        IFingerprintingService acoustId = Fake.Of<IFingerprintingService>((m, a) =>
        {
            if (m.Name == nameof(IFingerprintingService.Lookup))
            {
                foreach (LocalTrack track in (List<LocalTrack>)a![0]!)
                    track.AcoustIdResults = alsoHeard == null ? [recordingOf(track)] : [recordingOf(track), alsoHeard];
            }

            return m.Name == nameof(IFingerprintingService.IsSetup) ? true : null;
        });
        IReleaseService releases = Fake.Of<IReleaseService>((_, _) => new List<AlbumRelease> { new() { Id = 99 } });
        ITrackService tracks = Fake.Of<ITrackService>((_, _) => release.Tracks.Value
            .Select(t => new Track { Title = t.Title, ForeignRecordingId = t.ForeignRecordingId, Duration = t.Duration })
            .Append(new Track { Title = recordingTitle, ForeignRecordingId = "rec-heard", Duration = 201_000 }).ToList());
        return Tagger(release, distance, TagAsWanted, acoustId: acoustId, releases: releases, tracks: tracks, musicBrainz: musicBrainz);
    }

    private static PreImportTagger Tagger(AlbumRelease release, double distance, Action<TrackFile> writeTags, Action? onIdentify = null,
        IFingerprintingService? acoustId = null, IReleaseService? releases = null, ITrackService? tracks = null, IMusicBrainzLookups? musicBrainz = null)
    {
        IIdentificationService identify = Fake.Of<IIdentificationService>((_, a) =>
        {
            onIdentify?.Invoke();
            return AllMapped((List<LocalTrack>)a![0]!, release, distance);
        });
        IAudioTagService tags = Fake.Of<IAudioTagService>((m, a) =>
        {
            if (m.Name == nameof(IAudioTagService.ReadTags))
            {
                using TagLib.File file = TagLib.File.Create((string)a![0]!);
                return new ParsedTrackInfo { Title = file.Tag.Title, AlbumTitle = file.Tag.Album, ArtistTitle = "Artist", TrackNumbers = [0], Duration = TimeSpan.FromSeconds(200) };
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

        return new PreImportTagger(identify, tags, acoustId ?? Fake.Of<IFingerprintingService>((m, _) => throw new NotSupportedException(m.Name)),
            tracks ?? Fake.Of<ITrackService>((m, _) => throw new NotSupportedException(m.Name)),
            releases ?? Fake.Of<IReleaseService>((m, _) => throw new NotSupportedException(m.Name)),
            disk, musicBrainz ?? Fake.Of<IMusicBrainzLookups>((_, _) => null), LogManager.GetLogger(nameof(PreImportTaggerWholeDownloadTests)));
    }

    // As the Soulseek client runs it: every file fingerprinted, and the title fallback checked too.
    private Task<PreImportTagger.TaggingResult> TagVerified(PreImportTagger tagger, Album album, Artist artist) =>
        tagger.TagCompletedDownloadAsync(album, artist, null, "download", _dir, 0.15, false, CancellationToken.None,
            verifyAllWithFingerprint: true, fingerprintTitleFallback: true);

    [Fact]
    public async Task A_download_the_fingerprint_hears_as_another_song_is_tagged_nowhere_and_says_so()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await TagVerified(Fingerprinted(release, _ => "rec-heard", "Ember Gate"), album, artist);

        Assert.Equal(0, result.Tagged);
        Assert.Equal("fingerprint says 01.flac is a different recording than 'Lantern Song'", result.WrongRecording);
        Assert.Equal(paths[0], result.WrongRecordingFile);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
    }

    // An unexpected error in the title pass (here its MusicBrainz lookup) must not lose the first pass's refusal.
    [Fact]
    public async Task A_refusal_outlives_a_title_pass_that_throws()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("EP", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");
        int lookups = 0;
        IMusicBrainzLookups musicBrainz = Fake.Of<IMusicBrainzLookups>((_, _) => ++lookups > 1 ? throw new InvalidOperationException("unexpected") : null);

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, _ => "rec-heard", "Ember Gate", alsoHeard: "rec-unknown", musicBrainz: musicBrainz), album, artist);

        Assert.Equal(2, lookups);
        Assert.Equal(1, result.TagWriteFailed);
        Assert.Equal("fingerprint says 01.flac is a different recording than 'Lantern Song'", result.WrongRecording);
        Assert.Equal(paths[0], result.WrongRecordingFile);
    }

    // Lidarr mapped the files crosswise: held, so a manual import can map them right.
    [Fact]
    public async Task An_album_mapped_crosswise_is_held_for_a_manual_import()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Salt Road", "Lantern Song");

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, f => Path.GetFileName(f.Path) == "01.flac" ? "rec-2" : "rec-1", "Ember Gate"), album, artist);

        Assert.Equal(0, result.Tagged);
        Assert.Equal("fingerprint says 01.flac is a different recording than 'Lantern Song'", result.WrongRecording);
        Assert.Equal(paths[0], result.WrongRecordingFile);
    }

    [Fact]
    public async Task A_reissue_title_of_the_wanted_song_holds_nothing()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, f => Path.GetFileName(f.Path) == "01.flac" ? "rec-heard" : "rec-2", "Lantern Song - 2011 Remaster"), album, artist);

        Assert.Null(result.WrongRecording);
    }

    // Lidarr's mapping pairs each file with the other's track; the names, and the fingerprint, say otherwise.
    // Only a single or EP gets the title pass: a crosswise album still fails, and is searched again.
    [Fact]
    public async Task A_single_mapped_crosswise_but_named_right_is_tagged_by_its_names()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Single", "Lantern Song", "Salt Road");
        string[] paths = Files("Salt Road", "Lantern Song");

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, f => Path.GetFileName(f.Path) == "01.flac" ? "rec-2" : "rec-1", "Ember Gate"), album, artist);

        Assert.Equal(2, result.Tagged);
        Assert.Null(result.WrongRecording);
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
    }

    [Fact]
    public async Task A_download_the_fingerprint_hears_as_the_wanted_songs_is_tagged()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, f => $"rec-{Path.GetFileNameWithoutExtension(f.Path).TrimStart('0')}", "Ember Gate"), album, artist);

        Assert.Equal(2, result.Tagged);
        Assert.Null(result.WrongRecording);
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
    }

    private static string OwnRecording(LocalTrack file) => $"rec-{Path.GetFileNameWithoutExtension(file.Path).TrimStart('0')}";

    // Past the 0.150 limit, at 0.158, with AcoustID naming each file's own recording.
    [Fact]
    public async Task An_album_past_the_limit_is_tagged_when_the_fingerprint_names_every_track()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await TagVerified(Fingerprinted(release, OwnRecording, "Ember Gate", distance: 0.158), album, artist);

        Assert.Equal(2, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
    }

    [Fact]
    public async Task An_album_past_the_limit_stays_untagged_when_one_track_is_unverifiable()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await TagVerified(
            Fingerprinted(release, f => Path.GetFileName(f.Path) == "02.flac" ? "rec-heard" : "rec-1", "Salt Road", distance: 0.158), album, artist);

        Assert.Equal(0, result.Tagged);
        Assert.Null(result.WrongRecording);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
    }

    // Without the fingerprint pass there is no word on the recordings, so the limit holds.
    [Fact]
    public async Task An_album_past_the_limit_stays_untagged_without_the_fingerprint_pass()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "Lantern Song", "Salt Road");
        string[] paths = Files("Lantern Song", "Salt Road");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.158, TagAsWanted), album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
    }

    // The folder lacks "(Extended Mix)"; the file name carries it.
    [Fact]
    public async Task A_qualifier_every_file_names_stands_in_for_the_folder()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Single", "Lantern Song (Extended Mix)");
        album.Title = "Lantern Song (Extended Mix)";
        string path = NewFile("01 - Artist - Lantern Song (Extended Mix).flac", "Lantern Song (Extended Mix)");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, TagAsWanted), album, artist, CancellationToken.None);

        Assert.Equal(1, result.Tagged);
        Assert.Equal("Wanted Album", AlbumTag(path));
    }

    [Fact]
    public async Task A_qualifier_only_some_files_name_still_refuses_the_download()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("EP", "Lantern Song (Extended Mix)", "Salt Road (Extended Mix)");
        album.Title = "Lantern Song (Extended Mix)";
        string[] paths = [NewFile("01 - Lantern Song (Extended Mix).flac", "Lantern Song (Extended Mix)"), NewFile("02 - Salt Road.flac", "Salt Road")];

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, TagAsWanted), album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
    }

    [Fact]
    public async Task Plain_files_in_a_plain_folder_still_refuse_an_extended_target()
    {
        (Album album, Artist artist, AlbumRelease release) = Target("Single", "Lantern Song (Extended Mix)");
        album.Title = "Lantern Song (Extended Mix)";
        string path = NewFile("01 - Artist - Lantern Song.flac", "Lantern Song");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, TagAsWanted), album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.Equal("Store Album", AlbumTag(path));
    }

    private Task<PreImportTagger.TaggingResult> Tag(PreImportTagger tagger, Album album, Artist artist, CancellationToken ct, bool strip = false) =>
        tagger.TagCompletedDownloadAsync(album, artist, null, "download", _dir, 0.15, strip, ct);

    private string[] FolderNames() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

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

    // Untouched files still agree with each other, so only the planned release id shows nothing was written.
    [Fact]
    public async Task Counts_nothing_as_tagged_when_no_write_took()
    {
        string[] paths = Files("One", "Two");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, _ => { }), album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Store Album", AlbumTag(p)));
    }

    // A re-grab of files tagged before: album and release already match, so only the recording shows the write didn't take.
    [Fact]
    public async Task Rolls_back_a_silent_failure_on_a_file_that_already_carried_the_release()
    {
        string[] paths = Files("One", "Two", "Three");
        using (TagLib.File stale = TagLib.File.Create(paths[1]))
        {
            stale.Tag.Album = "Wanted Album";
            stale.Tag.MusicBrainzReleaseId = WantedReleaseId;
            stale.Tag.MusicBrainzTrackId = "rec-3";
            stale.Save();
        }

        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            if (!tf.Path.EndsWith("02.flac", StringComparison.Ordinal))
                TagAsWanted(tf);
        });

        PreImportTagger.TaggingResult result = await Tag(tagger, album, artist, CancellationToken.None);

        Assert.Equal(0, result.Tagged);
        Assert.Equal("Store Album", AlbumTag(paths[0]));
        Assert.Equal("Store Album", AlbumTag(paths[2]));
    }

    // Identification can wait on AcoustID or MusicBrainz; only the file work is one at a time.
    [Fact]
    public async Task A_pass_still_looking_up_does_not_hold_up_another_folder()
    {
        string other = Path.Combine(_root, "Artist - Other Album");
        Directory.CreateDirectory(other);
        Files("One");
        NewFileIn(other, "01.flac", "One");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One");
        TaskCompletionSource firstLookingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstMayFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PreImportTagger first = Tagger(release, 0.0, TagAsWanted, () =>
        {
            firstLookingUp.TrySetResult();
            firstMayFinish.Task.Wait(TimeSpan.FromSeconds(10));
        });

        Task<PreImportTagger.TaggingResult> one = Task.Run(() => Tag(first, album, artist, CancellationToken.None));
        await firstLookingUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
        PreImportTagger.TaggingResult second = await Tagger(release, 0.0, TagAsWanted)
            .TagCompletedDownloadAsync(album, artist, null, "other", other, 0.15, false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        firstMayFinish.SetResult();
        await one.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, second.Tagged);
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

        PreImportTagger.TaggingResult result = await Tag(tagger, album, artist, cts.Token);

        Assert.Equal(3, result.Tagged);
        Assert.All(paths, p => Assert.Equal("Wanted Album", AlbumTag(p)));
    }

    [Fact]
    public async Task Counts_each_file_once_when_no_pass_tags()
    {
        Files("Alpha", "Unrelated");
        (Album album, Artist artist, AlbumRelease release) = Target("Single", "Alpha", "Beta");

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.40, _ => { }), album, artist, CancellationToken.None);

        Assert.Equal((0, 2, 0), (result.Tagged, result.SkippedWeakMatch, result.TagWriteFailed));
        Assert.Null(result.WrongRecording);
    }

    // The feat-strip rename puts zz.flac at "01 - Beta.flac", the case-only twin of 01 - beta.flac's freed name.
    [Fact]
    public async Task Rolls_back_a_rename_onto_a_name_that_differs_from_an_original_only_by_case()
    {
        NewFile("01 - beta.flac", "q1");
        NewFile("zz.flac", "q2");
        NewFile("zzz.flac", "q3");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", ("Alpha", 2), ("Beta", 1), ("Gamma", 3));
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            if (tf.Path.EndsWith("zzz.flac", StringComparison.Ordinal))
                throw new IOException("file is locked");
            TagAsWanted(tf);
        });

        PreImportTagger.TaggingResult result = await Tag(tagger, album, artist, CancellationToken.None, strip: true);

        Assert.Equal(0, result.Tagged);
        Assert.Equal(["01 - beta.flac", "zz.flac", "zzz.flac"], FolderNames());
        Assert.All(FolderNames(), name => Assert.Equal("Store Album", AlbumTag(Path.Combine(_dir, name))));
    }

    [Fact]
    public async Task Renames_featured_files_only_once_the_pass_stands()
    {
        NewFile("01 - beta.flac", "q1");
        NewFile("zz.flac", "q2");
        NewFile("zzz.flac", "q3");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", ("Alpha", 2), ("Beta", 1), ("Gamma", 3));

        PreImportTagger.TaggingResult result = await Tag(Tagger(release, 0.0, TagAsWanted), album, artist, CancellationToken.None, strip: true);

        Assert.Equal(3, result.Tagged);
        Assert.Equal(["01 - Beta.flac", "02 - Alpha.flac", "03 - Gamma.flac"], FolderNames());
        Assert.Equal(["01 - Beta.flac", "02 - Alpha.flac", "03 - Gamma.flac"], result.TaggedFiles!.Select(t => Path.GetFileName(t.FinalPath)).Order(StringComparer.Ordinal));
    }

    // A rollback that leaves a file tagged must stop the import, not read as one failed write.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_rollback_that_cannot_put_the_files_back_fails_the_pass()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "A read-only folder blocks a move only for an unprivileged POSIX user.");
        string[] paths = Files("One", "Two");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two");
        PreImportTagger tagger = Tagger(release, 0.0, tf =>
        {
            if (tf.Path.EndsWith("02.flac", StringComparison.Ordinal))
            {
                File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                throw new IOException("file is locked");
            }

            TagAsWanted(tf);
        });

        try
        {
            await Assert.ThrowsAsync<TagRestoreException>(() => Tag(tagger, album, artist, CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Equal(2, Directory.GetFiles(_dir, "*.sleezer-untagged").Length);
    }

    [Fact]
    public async Task A_pass_on_a_parent_folder_waits_for_one_on_its_child()
    {
        Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        TaskCompletionSource childWrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource childMayFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource parentWrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PreImportTagger child = Tagger(release, 0.0, tf =>
        {
            TagAsWanted(tf);
            childWrote.TrySetResult();
            childMayFinish.Task.Wait(TimeSpan.FromSeconds(10));
        });
        PreImportTagger parent = Tagger(release, 0.0, tf =>
        {
            parentWrote.TrySetResult();
            TagAsWanted(tf);
        });

        Task<PreImportTagger.TaggingResult> one = Task.Run(() => Tag(child, album, artist, CancellationToken.None));
        await childWrote.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<PreImportTagger.TaggingResult> two = Task.Run(() => parent.TagCompletedDownloadAsync(album, artist, null, "parent", _root, 0.15, false, CancellationToken.None));
        bool parentWroteMeanwhile = await Task.WhenAny(parentWrote.Task, Task.Delay(500)) == parentWrote.Task;
        childMayFinish.SetResult();
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(parentWroteMeanwhile);
    }

    [Fact]
    public async Task Waits_for_another_pass_on_the_same_folder()
    {
        string[] paths = Files("One", "Two", "Three");
        (Album album, Artist artist, AlbumRelease release) = Target("Album", "One", "Two", "Three");
        TaskCompletionSource firstWrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstMayFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondWrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PreImportTagger first = Tagger(release, 0.0, tf =>
        {
            TagAsWanted(tf);
            firstWrote.TrySetResult();
            firstMayFinish.Task.Wait(TimeSpan.FromSeconds(10));
        });
        PreImportTagger second = Tagger(release, 0.0, tf =>
        {
            secondWrote.TrySetResult();
            if (tf.Path.EndsWith("02.flac", StringComparison.Ordinal))
                throw new IOException("file is locked");
            TagAsWanted(tf);
        });

        Task<PreImportTagger.TaggingResult> one = Task.Run(() => Tag(first, album, artist, CancellationToken.None));
        await firstWrote.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<PreImportTagger.TaggingResult> two = Task.Run(() => Tag(second, album, artist, CancellationToken.None));
        bool secondWroteMeanwhile = await Task.WhenAny(secondWrote.Task, Task.Delay(500)) == secondWrote.Task;
        firstMayFinish.SetResult();
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(secondWroteMeanwhile);
        Assert.Single(paths.Select(AlbumTag).Distinct());
    }
}
