using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper
{
    public sealed class SearchSniperRepositoryHelper(
        IMainDatabase database,
        IEventAggregator eventAggregator,
        IArtistService artistService) : BasicRepository<Album>(database, eventAggregator)
    {
        private readonly IArtistService _artistService = artistService;

        // Query failures propagate: SearchSniperTask.Execute logs them, where an empty result would read as "nothing to search".
        public List<Album> GetCutoffUnmetAlbumsBatch(Dictionary<int, List<int>> profileCutoffs, int lastId, int limit) =>
            profileCutoffs.Count == 0 ? [] : Batch(BuildCutoffUnmetQuery(profileCutoffs), lastId, limit);

        public List<Album> GetPartialAlbumsBatch(int lastId, int limit) => Batch(BuildMissingTracksQuery(), lastId, limit);

        public List<Album> GetAlbumsWithFilesBatch(IReadOnlyCollection<int> profileIds, int lastId, int limit) =>
            profileIds.Count == 0 ? [] : Batch(BuildAlbumsWithFilesQuery(profileIds), lastId, limit);

        public (int minId, int maxId) GetPartialAlbumsIdRange() => IdRange(BuildMissingTracksQuery);

        public (int minId, int maxId) GetCutoffUnmetAlbumsIdRange(Dictionary<int, List<int>> profileCutoffs) =>
            profileCutoffs.Count == 0 ? (0, 0) : IdRange(() => BuildCutoffUnmetQuery(profileCutoffs));

        public (int minId, int maxId) GetAlbumsWithFilesIdRange(IReadOnlyCollection<int> profileIds) =>
            profileIds.Count == 0 ? (0, 0) : IdRange(() => BuildAlbumsWithFilesQuery(profileIds));

        // One file per album from its monitored release, the set CutoffSpecification scores from.
        public Dictionary<int, TrackFile> GetFirstTrackFiles(IEnumerable<int> albumIds)
        {
            string ids = string.Join(",", albumIds);
            if (ids.Length == 0)
                return [];

            SqlBuilder builder = new SqlBuilder(_database.DatabaseType)
                .Select(typeof(TrackFile))
                .Join<TrackFile, Track>((f, t) => f.Id == t.TrackFileId)
                .Join<Track, AlbumRelease>((t, r) => t.AlbumReleaseId == r.Id)
                .Where<AlbumRelease>(r => r.Monitored == true)
                .Where($@"""TrackFiles"".""AlbumId"" IN ({ids})");

            return _database.Query<TrackFile>(builder)
                .GroupBy(f => f.AlbumId)
                .ToDictionary(g => g.Key, g => g.MinBy(f => f.Id)!);
        }

        private List<Album> Batch(SqlBuilder query, int lastId, int limit) =>
            PopulateArtists(Query(query
                .Where($@"""Albums"".""Id"" > {lastId}")
                .OrderBy($@"""Albums"".""Id"" ASC LIMIT {limit}")));

        private (int minId, int maxId) IdRange(Func<SqlBuilder> query)
        {
            List<Album> minResult = Query(query().OrderBy($@"""Albums"".""Id"" ASC LIMIT 1"));

            if (minResult.Count == 0)
                return (0, 0);

            List<Album> maxResult = Query(query().OrderBy($@"""Albums"".""Id"" DESC LIMIT 1"));

            return (minResult[0].Id, maxResult.Count > 0 ? maxResult[0].Id : minResult[0].Id);
        }

        private SqlBuilder BuildCutoffUnmetQuery(Dictionary<int, List<int>> profileCutoffs)
        {
            string whereClause = string.Join(" OR ", profileCutoffs.Select(kvp =>
                $"(\"Artists\".\"QualityProfileId\" = {kvp.Key} AND " +
                $"CAST(json_extract(\"TrackFiles\".\"Quality\", '$.quality') AS INTEGER) IN ({string.Join(",", kvp.Value)}))"
            ));

            return AlbumsWithFiles()
                .Where($"({whereClause})")
                .GroupBy<Album>(x => x.Id);
        }

        private SqlBuilder BuildAlbumsWithFilesQuery(IReadOnlyCollection<int> profileIds) =>
            AlbumsWithFiles()
                .Where($@"""Artists"".""QualityProfileId"" IN ({string.Join(",", profileIds)})")
                .GroupBy<Album>(x => x.Id);

        private SqlBuilder AlbumsWithFiles() =>
            Builder()
                .Join<Album, Artist>((a, ar) => a.ArtistMetadataId == ar.ArtistMetadataId)
                .Join<Album, AlbumRelease>((a, r) => a.Id == r.AlbumId)
                .Join<AlbumRelease, Track>((r, t) => r.Id == t.AlbumReleaseId)
                .Join<Track, TrackFile>((t, f) => t.TrackFileId == f.Id)
                .Where<Album>(a => a.Monitored == true)
                .Where<Artist>(ar => ar.Monitored == true)
                .Where<AlbumRelease>(r => r.Monitored == true);

        private SqlBuilder BuildMissingTracksQuery()
        {
            return Builder()
                .Join<Album, Artist>((a, ar) => a.ArtistMetadataId == ar.ArtistMetadataId)
                .Join<Album, AlbumRelease>((a, r) => a.Id == r.AlbumId)
                .Join<AlbumRelease, Track>((r, t) => r.Id == t.AlbumReleaseId)
                .LeftJoin<Track, TrackFile>((t, f) => t.TrackFileId == f.Id)
                .Where<Album>(a => a.Monitored == true)
                .Where<Artist>(ar => ar.Monitored == true)
                .Where<AlbumRelease>(r => r.Monitored == true)
                .GroupBy<Album>(a => a.Id)
                .GroupBy<Artist>(ar => ar.SortName)
                .Having("COUNT(DISTINCT \"Tracks\".\"Id\") > 0")
                .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) > 0")
                .Having("SUM(CASE WHEN \"Tracks\".\"TrackFileId\" > 0 THEN 1 ELSE 0 END) < COUNT(DISTINCT \"Tracks\".\"Id\")");
        }

        public static Dictionary<int, List<int>> BuildProfileCutoffs(IEnumerable<QualityProfile> qualityProfiles)
        {
            Dictionary<int, List<int>> profileCutoffs = [];

            foreach (QualityProfile profile in qualityProfiles)
            {
                if (!profile.UpgradeAllowed)
                    continue;

                int cutoffIndex = profile.Items.FindIndex(x =>
                    (x.Quality?.Id == profile.Cutoff) || (x.Id == profile.Cutoff));

                if (cutoffIndex <= 0)
                    continue;

                List<int> qualityIds = [];

                foreach (QualityProfileQualityItem item in profile.Items.Take(cutoffIndex))
                {
                    if (item.Quality != null)
                    {
                        qualityIds.Add(item.Quality.Id);
                    }
                    else if (item.Items?.Count > 0)
                    {
                        foreach (QualityProfileQualityItem groupItem in item.Items)
                        {
                            if (groupItem.Quality != null)
                                qualityIds.Add(groupItem.Quality.Id);
                        }
                    }
                }

                if (qualityIds.Count != 0)
                    profileCutoffs[profile.Id] = qualityIds;
            }

            return profileCutoffs;
        }

        private List<Album> PopulateArtists(List<Album> albums)
        {
            if (albums.Count == 0)
                return albums;

            HashSet<int> neededMetadataIds = albums
                .Where(a => a.ArtistMetadataId > 0)
                .Select(a => a.ArtistMetadataId)
                .ToHashSet();

            if (neededMetadataIds.Count == 0)
                return albums;

            Dictionary<int, Artist> artistsByMetadataId = [];
            foreach (Artist artist in _artistService.GetAllArtists())
            {
                if (neededMetadataIds.Contains(artist.ArtistMetadataId))
                {
                    artistsByMetadataId[artist.ArtistMetadataId] = artist;
                    if (artistsByMetadataId.Count == neededMetadataIds.Count)
                        break;
                }
            }

            foreach (Album album in albums)
            {
                if (artistsByMetadataId.TryGetValue(album.ArtistMetadataId, out Artist? artist))
                    album.Artist = new LazyLoaded<Artist>(artist);
            }

            return albums;
        }

        public new SqlBuilder Builder() => base.Builder();

        public new List<Album> Query(SqlBuilder builder) => base.Query(builder);
    }
}
