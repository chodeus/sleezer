using Newtonsoft.Json.Linq;
using System.Text;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp.Downloading;

internal class StreamManifest
{
    public StreamManifest(TrackStreamData stream)
    {
        _encodedManifest = stream.Manifest!;
        _manifestMimeType = ManifestMimeTypeMethods.FromMime(stream.ManifestMimeType!);

        var rawManifest = Convert.FromBase64String(_encodedManifest);
        _decodedManifest = Encoding.UTF8.GetString(rawManifest);

        if (_manifestMimeType == ManifestMimeType.MPD)
        {
            var mpd = MPD.Parse(_decodedManifest);
            var dashInfo = new DashInfo(mpd);

            Urls = dashInfo.ChunkUrls;
            if (dashInfo.Codecs.Contains("flac"))
                Codecs = "FLAC";
            else if (dashInfo.Codecs.Contains("mp4a.40.5"))
                Codecs = "MP4A";
            else if (dashInfo.Codecs.Contains("mp4a.40.2"))
                Codecs = "MP4A";
            else
                Codecs = dashInfo.Codecs;

            MimeType = dashInfo.MimeType;
            SampleRate = dashInfo.AudioSamplingRate;
        }
        else if (_manifestMimeType == ManifestMimeType.MPD)
        {
            // TODO: i haven't seen one of these myself so im just guessing based on tidalapi

            var json = JObject.Parse(_decodedManifest);
            Urls = json["urls"]!.Select(t => t.ToString()).ToArray();
            Codecs = json["codecs"]!.ToString().ToUpper().Split('.')[0];
            MimeType = json["mimeType"]!.ToString();
            EncryptionType = json["encryptionType"]!.ToString();
            EncryptionKey = json["encryptionKey"]?.ToString();
        }
        else
            throw new UnsupportedManifestException("Unknown manifest, this track can't be downloaded.");

        FileExtension = ParseExtension(Urls[0], Codecs);
    }

    private string ParseExtension(string url, string codec)
    {
        if (url.Contains(".flac"))
            return ".flac";

        if (url.Contains(".mp4"))
        {
            if (codec.Contains("ac4") || codec.Contains("mha1"))
                return ".mp4";
            if (codec.Contains("flac"))
                return ".flac";

            return ".m4a";
        }

        if (url.Contains(".ts"))
            return ".ts";

        return ".m4a";
    }

    public string[] Urls { get; init; }
    public string Codecs { get; init; }
    public string MimeType { get; init; }
    public string FileExtension { get; init; }
    public int SampleRate { get; init; }

    // TODO: handle encryption key (currently also unhandled in tidalapi)
    public string EncryptionType { get; init; } = "NONE";
    public string? EncryptionKey { get; init; } = null;

    private string _encodedManifest;
    private string _decodedManifest;
    private ManifestMimeType _manifestMimeType;
}
