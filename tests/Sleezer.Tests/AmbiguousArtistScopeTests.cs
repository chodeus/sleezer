using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// What Lidarr's tier loop sees through IsValidRelease: only results the guard would drop are
// rejected, and only inside the search that opened the scope.
public class AmbiguousArtistScopeTests
{
    private static Artist A(string name) => new() { Name = name, CleanName = name.ToLowerInvariant().Replace(" ", string.Empty) };

    private static readonly Artist Searched = A("Main Act");
    private static readonly Artist[] Library = [A("Main Act"), A("Guest Act"), A("Guest Act")];

    private static StoreReleaseInfo R(string artist, params string[] mainArtists) => new()
    {
        Title = $"{artist} - Some Album (2024) [Lossless] [WEB]",
        Artist = artist,
        Album = "Some Album",
        MainArtists = mainArtists
    };

    [Fact]
    public void Rejects_only_what_the_guard_would_drop()
    {
        using (AmbiguousArtistScope.Begin(Searched, () => Library))
        {
            Assert.True(AmbiguousArtistScope.Rejects(R("Guest Act", "Guest Act")));
            Assert.False(AmbiguousArtistScope.Rejects(R("Guest Act", "Main Act", "Guest Act")));
            Assert.False(AmbiguousArtistScope.Rejects(R("Main Act", "Main Act")));
        }
    }

    [Fact]
    public void Rejects_nothing_outside_a_scope_or_after_it_closes()
    {
        StoreReleaseInfo dropped = R("Guest Act", "Guest Act");
        Assert.False(AmbiguousArtistScope.Rejects(dropped));

        using (AmbiguousArtistScope.Begin(Searched, () => Library))
            Assert.True(AmbiguousArtistScope.Rejects(dropped));

        Assert.False(AmbiguousArtistScope.Rejects(dropped));
    }

    // Indexer instances serve concurrent searches; each must see only the scope it opened.
    [Fact]
    public async Task Concurrent_searches_do_not_see_each_others_scope()
    {
        StoreReleaseInfo dropped = R("Guest Act", "Guest Act");
        using var bothInScope = new Barrier(2);

        async Task<bool> Search(IEnumerable<Artist> library)
        {
            await Task.Yield();
            using (AmbiguousArtistScope.Begin(Searched, () => library))
            {
                Assert.True(bothInScope.SignalAndWait(TimeSpan.FromSeconds(5)), "the two searches never overlapped");
                await Task.Delay(20);
                return AmbiguousArtistScope.Rejects(dropped);
            }
        }

        bool[] results = await Task.WhenAll(Search(Library), Search([A("Main Act"), A("Guest Act")]));

        Assert.Equal([true, false], results);
    }
}
