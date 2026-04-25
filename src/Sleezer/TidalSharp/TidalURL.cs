using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp;

public class TidalURL(string url, EntityType type, string id)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public string Url { get; init; } = url;
    public EntityType EntityType { get; init; } = type;
    public string Id { get; init; } = id;

    public static bool TryParse(string url, out TidalURL tidalUrl)
    {
        try
        {
            tidalUrl = Parse(url);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not parse {Url} as a Tidal URL", url);
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
            tidalUrl = null;
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
            return false;
        }
    }

    public static TidalURL Parse(string url)
    {
        int paramStart = url.IndexOf('?');
        if (paramStart != -1)
            url = url[..paramStart];

        EntityType type;
        string? id;
        if (url.Contains("/track/"))
        {
            type = EntityType.Track;
            id = Regex.Match(url, "/track/(\\d+)").Groups[1].Value;
        }
        else if (url.Contains("/playlist/"))
        {
            type = EntityType.Playlist;
            id = Regex.Match(url, "/playlist/(\\S+)").Groups[1].Value;
        }
        else if (url.Contains("/album/"))
        {
            type = EntityType.Album;
            id = Regex.Match(url, "/album/(\\d+)").Groups[1].Value;
        }
        else if (url.Contains("/artist"))
        {
            type = EntityType.Artist;
            id = Regex.Match(url, "/artist/(\\d+)").Groups[1].Value;
        }
        else if (url.Contains("/video"))
        {
            type = EntityType.Video;
            id = Regex.Match(url, "/video/(\\d+)").Groups[1].Value;
        }
        else if (url.Contains("/mix"))
        {
            type = EntityType.Mix;
            id = Regex.Match(url, "/mix/(\\S+)").Groups[1].Value;
        }
        else
            throw new InvalidURLException($"Unable to determine type of URL \"{url}\".");

        return new TidalURL(url, type, id);
    }

    public async Task<string[]> GetAssociatedTracks(TidalClient client, int topLimit = 100, CancellationToken token = default)
    {
        switch (EntityType)
        {
            case EntityType.Track:
                return [Id];
            case EntityType.Playlist:
                return (await client.API.GetPlaylistTracks(Id, token))["items"]!.Select(t => t["id"]!.ToString()).ToArray();
            case EntityType.Album:
                return (await client.API.GetAlbumTracks(Id, token))["items"]!.Select(t => t["id"]!.ToString()).ToArray();
            case EntityType.Mix:
                return (await client.API.GetMix(Id, token))["tracks"]!["items"]!.Select(t => t["id"]!.ToString()).ToArray();
            case EntityType.Artist:
                {
                    string[] albumIds = (await client.API.GetArtistAlbums(Id, FilterOptions.ALL, token))["items"]!.Select(a => a["id"]!.ToString()).ToArray();
                    List<string> trackIds = [];
                    for (int i = 0; i < albumIds.Length; i++)
                        trackIds.AddRange((await client.API.GetAlbumTracks(albumIds[i], token))["items"]!.Select(t => t["id"]!.ToString()));
                    return [.. trackIds];
                }
            case EntityType.Video:
                throw new InvalidOperationException("Attempted to get tracks for a video entity.");
        }

        return [];
    }

    public async Task<string?> GetCoverUrl(TidalClient client, MediaResolution resolution, CancellationToken token = default)
    {
        switch (EntityType)
        {
            case EntityType.Track:
                {
                    var data = await client.API.GetTrack(Id, token);
                    return Globals.GetImageUrl(data["album"]!["cover"]!.ToString(), resolution);
                }
            case EntityType.Album:
                {
                    var data = await client.API.GetAlbum(Id, token);
                    return Globals.GetImageUrl(data["cover"]!.ToString(), resolution);
                }
            case EntityType.Artist:
                {
                    var data = await client.API.GetArtist(Id, token);
                    var picture = data["picture"];
                    if (picture == null) return null;
                    return Globals.GetImageUrl(picture!.ToString(), resolution);
                }
            case EntityType.Playlist:
                {
                    var data = await client.API.GetPlaylist(Id, token);
                    var image = data["squareImage"];
                    if (image == null) return null;
                    return Globals.GetImageUrl(image!.ToString(), resolution);
                }
            case EntityType.Video:
                {
                    var data = await client.API.GetVideo(Id, token);
                    var image = data["imageId"];
                    if (image == null) return null;
                    return Globals.GetImageUrl(image!.ToString(), resolution);
                }
            case EntityType.Mix:
                {
                    var data = await client.API.GetMix(Id, token);
                    var images = data["mix"]!["images"]!.Children<JProperty>();

                    // jank to get the closest size as mixes don't use the regular hash thing
                    var closest = images
                        .Select(image => new
                        {
                            SizeName = image.Name,
                            Width = (int)image.Value["width"]!,
                            Height = (int)image.Value["height"]!,
                            Url = (string)image.Value["url"]!,
                            ClosestDimension = Math.Abs((int)resolution - Math.Max((int)image.Value["width"]!, (int)image.Value["height"]!))
                        })
                        .OrderBy(image => image.ClosestDimension)
                        .FirstOrDefault();

                    return closest?.Url ?? string.Empty;
                }
            default:
                return null;
        }
    }

    public async Task<string?> GetTitle(TidalClient client, CancellationToken token = default)
    {
        switch (EntityType)
        {
            case EntityType.Track:
                {
                    var data = await client.API.GetTrack(Id, token);
                    return data["title"]!.ToString();
                }
            case EntityType.Album:
                {
                    var data = await client.API.GetAlbum(Id, token);
                    return data["title"]!.ToString();
                }
            case EntityType.Artist:
                {
                    var data = await client.API.GetArtist(Id, token);
                    return data["name"]!.ToString();
                }
            case EntityType.Playlist:
                {
                    var data = await client.API.GetPlaylist(Id, token);
                    return data["title"]!.ToString();
                }
            case EntityType.Video:
                {
                    var data = await client.API.GetVideo(Id, token);
                    return data["title"]!.ToString();
                }
            case EntityType.Mix:
                {
                    var data = await client.API.GetMix(Id, token);
                    return data["mix"]!["title"]!.ToString();
                }
            default:
                return null;
        }
    }
}