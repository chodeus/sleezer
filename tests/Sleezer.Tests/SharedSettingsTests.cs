using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Download.Clients.Tidal;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Deezer;
using NzbDrone.Core.Indexers.Qobuz;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;
using NzbDrone.Plugin.Sleezer.Metadata;
using Xunit;

namespace Sleezer.Tests;

public class IndexerLoginTests
{
    private static IndexerDefinition Indexer(int id, string name, IProviderConfig settings) =>
        new() { Id = id, Name = name, Settings = settings, EnableAutomaticSearch = false, EnableInteractiveSearch = false };

    private static SlskdSettings Slskd(string url) => new() { BaseUrl = url, ApiKey = "key-" + url };

    [Fact]
    public void The_only_indexer_of_its_kind_is_used_even_when_disabled()
    {
        SlskdSettings only = Slskd("http://server-a:5030");

        SlskdSettings found = IndexerLogin.Find<SlskdSettings>(
            [Indexer(1, "Indexer A", only), Indexer(2, "Other", new SubSonicIndexerSettings())], 0, "Slskd");

        Assert.Same(only, found);
    }

    [Fact]
    public void No_indexer_of_its_kind_asks_for_one()
    {
        var ex = Assert.Throws<DownloadClientException>(() =>
            IndexerLogin.Find<SlskdSettings>([Indexer(2, "Other", new SubSonicIndexerSettings())], 0, "Slskd"));

        Assert.Contains("Add a Slskd indexer first", ex.Message);
    }

    [Fact]
    public void Several_without_a_choice_ask_which()
    {
        var ex = Assert.Throws<DownloadClientException>(() =>
            IndexerLogin.Find<SlskdSettings>([Indexer(1, "Indexer A", Slskd("http://a")), Indexer(2, "Indexer B", Slskd("http://b"))], 0, "Slskd"));

        Assert.Contains("There are 2 Slskd indexers", ex.Message);
    }

    [Fact]
    public void The_chosen_indexer_wins_among_several()
    {
        SlskdSettings chosen = Slskd("http://b");

        Assert.Same(chosen, IndexerLogin.Find<SlskdSettings>([Indexer(1, "Indexer A", Slskd("http://a")), Indexer(2, "Indexer B", chosen)], 2, "Slskd"));
    }

    [Fact]
    public void A_chosen_indexer_that_is_gone_or_another_kind_is_refused()
    {
        IndexerDefinition[] indexers = [Indexer(1, "Indexer A", Slskd("http://a")), Indexer(2, "Other", new SubSonicIndexerSettings())];

        Assert.Throws<DownloadClientException>(() => IndexerLogin.Find<SlskdSettings>(indexers, 9, "Slskd"));
        Assert.Throws<DownloadClientException>(() => IndexerLogin.Find<SlskdSettings>(indexers, 2, "Slskd"));
    }

    [Fact]
    public void The_dropdown_lists_automatic_then_this_kind_by_name()
    {
        object options = IndexerLogin.Options<SlskdSettings>(
            [Indexer(1, "Zeta", Slskd("http://z")), Indexer(2, "Other", new SubSonicIndexerSettings()), Indexer(3, "alpha", Slskd("http://a"))]);

        var list = ((System.Collections.IEnumerable)options.GetType().GetProperty("options")!.GetValue(options)!).Cast<object>()
            .Select(o => ((int)o.GetType().GetProperty("Value")!.GetValue(o)!, (string)o.GetType().GetProperty("Name")!.GetValue(o)!))
            .ToList();

        Assert.Equal([(0, "Automatic (the only one)"), (3, "alpha"), (1, "Zeta")], list);
    }
}

public class SharedLoginSettingsTests
{
    [Fact]
    public void Slskd_client_takes_the_indexer_login_and_its_host_follows()
    {
        SlskdProviderSettings client = new() { BaseUrl = "http://old-host:5030", ApiKey = "old-key" };
        Assert.Equal("old-host", client.Host);

        client.UseLogin(new SlskdSettings { BaseUrl = "http://new-host:5030", ApiKey = "new-key" });

        Assert.Equal("http://new-host:5030", client.BaseUrl);
        Assert.Equal("new-key", client.ApiKey);
        Assert.Equal("new-host", client.Host);
    }

    [Fact]
    public void SubSonic_client_takes_the_indexer_server_and_login()
    {
        SubSonicProviderSettings client = new();

        client.UseLogin(new SubSonicIndexerSettings { BaseUrl = "http://music.example", Username = "user-a", Password = "pass-a", UseTokenAuth = false, RequestTimeout = 90 });

        Assert.Equal(("http://music.example", "user-a", "pass-a", false, 90), (client.ServerUrl, client.Username, client.Password, client.UseTokenAuth, client.RequestTimeout));
    }

    [Theory]
    [InlineData(true, TrackCountFilterType.Lower, true)]
    [InlineData(false, TrackCountFilterType.Disabled, false)]
    public void Whole_albums_only_sets_the_slskd_track_and_source_checks(bool wholeAlbumsOnly, TrackCountFilterType filter, bool coherent)
    {
        SlskdSettings stored = new() { TrackCountFilter = (int)TrackCountFilterType.Unfitting, RequireCoherentSingleSource = !coherent, ApiKey = "key-a" };

        SlskdSettings effective = stored.WithWholeAlbumsOnly(wholeAlbumsOnly);

        Assert.Equal((filter, coherent, "key-a"), ((TrackCountFilterType)effective.TrackCountFilter, effective.RequireCoherentSingleSource, effective.ApiKey));
        Assert.Equal((TrackCountFilterType.Unfitting, !coherent), ((TrackCountFilterType)stored.TrackCountFilter, stored.RequireCoherentSingleSource));
    }
}

public class SharedSettingsCopyTests
{
    private static IndexerDefinition Indexer(int id, string name, IProviderConfig settings) => new() { Id = id, Name = name, Settings = settings };

    private static DownloadClientDefinition Client(int id, string name, IProviderConfig settings) => new() { Id = id, Name = name, Settings = settings };

    private static SharedSettingsPlan Plan(IndexerDefinition[] indexers, DownloadClientDefinition[] clients) =>
        SharedSettingsCopy.Plan(indexers, clients, [1, 3], true);

    [Fact]
    public void Agreeing_settings_copy_across_unchanged()
    {
        SharedSettingsPlan plan = Plan(
            [
                Indexer(1, "Store A", new DeezerIndexerSettings { HideAlbumsWithMissing = true, AllowMp3FallbackForMissingFlac = false, StrictMatching = true }),
                Indexer(2, "Store B", new QobuzIndexerSettings { HideNonStreamable = true, StrictMatching = true }),
                Indexer(3, "Peer A", new SlskdSettings { TrackCountFilter = (int)TrackCountFilterType.Lower, RequireCoherentSingleSource = true })
            ],
            [Client(1, "Store B", new QobuzSettings { RequireCompleteAlbum = true })]);

        Assert.True(plan.Rules.WholeAlbumsOnly);
        Assert.True(plan.Rules.HideUnstreamable);
        Assert.True(plan.Rules.StrictMatching);
        Assert.Equal([1, 3], plan.Rules.PreImportTaggingClients);
        Assert.True(plan.Rules.StripFeaturedArtists);
        Assert.True(plan.Rules.ValuesImported);
        Assert.False(plan.LyricsEnabled);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void A_rule_on_anywhere_stays_on_and_names_where_it_was_off()
    {
        SharedSettingsPlan plan = Plan(
            [
                Indexer(1, "Store A", new DeezerIndexerSettings { StrictMatching = true }),
                Indexer(2, "Store B", new QobuzIndexerSettings { StrictMatching = false })
            ],
            []);

        Assert.True(plan.Rules.StrictMatching);
        Assert.Contains(plan.Notes, n => n.StartsWith("Strict Matching: set on") && n.Contains("Store B"));
    }

    [Fact]
    public void Whole_albums_only_is_off_only_when_every_source_allowed_partial_albums()
    {
        SharedSettingsPlan plan = Plan(
            [
                Indexer(1, "Store A", new DeezerIndexerSettings { HideAlbumsWithMissing = false, AllowMp3FallbackForMissingFlac = true }),
                Indexer(3, "Peer A", new SlskdSettings { TrackCountFilter = (int)TrackCountFilterType.Unfitting, RequireCoherentSingleSource = false })
            ],
            [Client(1, "Store B", new QobuzSettings { RequireCompleteAlbum = false })]);

        Assert.False(plan.Rules.WholeAlbumsOnly);
    }

    [Fact]
    public void With_no_old_settings_the_strict_defaults_stand()
    {
        SharedSettingsPlan plan = SharedSettingsCopy.Plan([], [], [], false);

        Assert.True(plan.Rules.WholeAlbumsOnly && plan.Rules.HideUnstreamable && plan.Rules.StrictMatching);
        Assert.False(plan.LyricsEnabled || plan.TidalExtractFlac || plan.TidalReEncodeAAC);
    }

    [Fact]
    public void Lyrics_and_tidal_conversion_on_for_one_client_turn_on()
    {
        SharedSettingsPlan plan = Plan(
            [],
            [
                Client(1, "Store A", new DeezerSettings { SaveSyncedLyrics = true }),
                Client(2, "Store C", new TidalSettings { ExtractFlac = true })
            ]);

        Assert.True(plan.LyricsEnabled);
        Assert.True(plan.Lyrics.SaveSyncedLyrics);
        Assert.False(plan.Lyrics.UseLRCLIB);
        Assert.True(plan.TidalExtractFlac);
        Assert.False(plan.TidalReEncodeAAC);
    }

    [Fact]
    public void A_client_is_pointed_at_the_indexer_sharing_its_login_only_when_there_are_several()
    {
        SlskdProviderSettings client = new() { BaseUrl = "http://peer-b:5030", ApiKey = "key-b" };
        IndexerDefinition a = Indexer(5, "Peer A", new SlskdSettings { BaseUrl = "http://peer-a:5030", ApiKey = "key-a" });
        IndexerDefinition b = Indexer(6, "Peer B", new SlskdSettings { BaseUrl = "http://peer-b:5030", ApiKey = "key-b" });

        Assert.Equal(6, Plan([a, b], [Client(9, "Peer client", client)]).ClientIndexerIds[9]);
        Assert.Empty(Plan([b], [Client(9, "Peer client", client)]).ClientIndexerIds);
    }

    [Fact]
    public void A_client_whose_login_matches_no_indexer_is_reported()
    {
        DownloadClientDefinition client = Client(9, "Peer client", new SlskdProviderSettings { BaseUrl = "http://peer-c:5030", ApiKey = "key-c" });

        Assert.Contains(Plan([Indexer(5, "Peer A", new SlskdSettings { BaseUrl = "http://peer-a:5030", ApiKey = "key-a" })], [client]).Notes,
            n => n.StartsWith("Peer client: its own login matched no indexer"));
        Assert.Contains(Plan([], [client]).Notes, n => n.StartsWith("Peer client: there is no matching indexer"));
    }
}
