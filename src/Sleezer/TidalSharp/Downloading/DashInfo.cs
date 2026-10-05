namespace TidalSharp.Downloading;

internal class DashInfo
{
    public DashInfo(MPD mpd)
    {
        var firstAdaptationSet = mpd.Periods?[0].AdaptationSets?[0];
        var firstRepresentation = firstAdaptationSet?.Representations?[0];
        var firstSegmentTemplate = firstRepresentation?.SegmentTemplates?[0];
        var firstSegmentTimeline = firstSegmentTemplate?.SegmentTimelines?[0];

        Duration = mpd.MediaPresentationDuration!.Value;
        ContentType = firstAdaptationSet?.ContentType!;
        MimeType = firstAdaptationSet?.MimeType!;
        Codecs = firstRepresentation?.Codecs!;
        FirstUrl = firstSegmentTemplate?.Initialization!;
        MediaUrl = firstSegmentTemplate?.Media!;
        TimeScale = (uint)firstSegmentTemplate?.TimeScale!.Value!;
        AudioSamplingRate = int.Parse(firstRepresentation?.AudioSamplingRate!);
        ChunkSize = (int)firstSegmentTimeline?.Ss?[0].D!;
        LastChunkSize = (int)firstSegmentTimeline?.Ss?.Last().D!;

        ChunkUrls = GetUrls(mpd);
    }

    private string[] GetUrls(MPD mpd)
    {
        var firstSegmentTemplate = mpd.Periods?[0].AdaptationSets?[0].Representations?[0].SegmentTemplates?[0];

        // Each <S> covers 1 + r segments; counting r alone drops the tail of a multi-run timeline.
        var mediaCount = firstSegmentTemplate?.SegmentTimelines?[0].Ss!.Sum(s => 1 + Math.Max(s.R ?? 0, 0)) ?? 0;
        var startNumber = firstSegmentTemplate?.StartNumber ?? 1;

        var urls = new string[mediaCount + 1];
        urls[0] = FirstUrl ?? MediaUrl.Replace("$Number$", "0");
        for (var i = 0; i < mediaCount; i++)
            urls[i + 1] = MediaUrl.Replace("$Number$", (startNumber + i).ToString());

        return urls;
    }

    public TimeSpan Duration { get; init; }
    public string ContentType { get; init; }
    public string MimeType { get; init; }
    public string Codecs { get; init; }
    public string FirstUrl { get; init; }
    public string MediaUrl { get; init; }
    public uint TimeScale { get; init; }
    public int AudioSamplingRate { get; init; }
    public int ChunkSize { get; init; }
    public int LastChunkSize { get; init; }

    public string[] ChunkUrls { get; init; }
}