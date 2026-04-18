using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace NzbDrone.Plugin.Sleezer.ImportLists.ListenBrainz.ListenBrainzPlaylist
{
    public class ListenBrainzPlaylistRequestGenerator(ListenBrainzPlaylistSettings settings) : IImportListRequestGenerator
    {
        private readonly ListenBrainzPlaylistSettings _settings = settings;

        public virtual ImportListPageableRequestChain GetListItems()
        {
            ImportListPageableRequestChain pageableRequests = new();

            List<ImportListRequest> requests = [];

            foreach (string playlistId in _settings.PlaylistIds)
            {
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    requests.Add(CreatePlaylistRequest(playlistId));
                }
            }

            if (requests.Count != 0)
                pageableRequests.Add(requests);

            return pageableRequests;
        }

        public ImportListRequest CreatePlaylistRequest(string playlistId)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json);

            if (!string.IsNullOrEmpty(_settings.UserToken))
            {
                requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
            }

            HttpRequest request = requestBuilder.Build();
            request.Url = new HttpUri($"{_settings.BaseUrl}/1/playlist/{playlistId}");

            return new ImportListRequest(request);
        }

        public string GetEndpointUrl()
        {
            string username = _settings.AccessToken?.Trim() ?? "";
            return (ListenBrainzPlaylistEndpointType)_settings.PlaylistType switch
            {
                ListenBrainzPlaylistEndpointType.Normal => $"{_settings.BaseUrl}/1/user/{username}/playlists",
                ListenBrainzPlaylistEndpointType.CreatedFor => $"{_settings.BaseUrl}/1/user/{username}/playlists/createdfor",
                ListenBrainzPlaylistEndpointType.Recommendations => $"{_settings.BaseUrl}/1/user/{username}/playlists/recommendations",
                _ => $"{_settings.BaseUrl}/1/user/{username}/playlists"
            };
        }

        public ImportListRequest CreateDiscoveryRequest(int count = 100, int offset = 0)
        {
            HttpRequestBuilder requestBuilder = new HttpRequestBuilder(_settings.BaseUrl)
                .Accept(HttpAccept.Json);

            if (!string.IsNullOrEmpty(_settings.UserToken))
            {
                requestBuilder.SetHeader("Authorization", $"Token {_settings.UserToken}");
            }

            HttpRequest request = requestBuilder.Build();
            string endpointUrl = GetEndpointUrl();
            request.Url = new HttpUri($"{endpointUrl}?count={count}&offset={offset}");

            return new ImportListRequest(request);
        }
    }
}