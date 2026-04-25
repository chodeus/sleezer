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
        var firstAdaptationSet = mpd.Periods?[0].AdaptationSets?[0];
        var firstRepresentation = firstAdaptationSet?.Representations?[0];
        var firstSegmentTemplate = firstRepresentation?.SegmentTemplates?[0];

        // min segments count; i.e. .initialization + the very first of .media;
        // see https://developers.broadpeak.io/docs/foundations-dash
        var segmentsCount = 1 + 1;

        foreach (var s in firstSegmentTemplate?.SegmentTimelines?[0].Ss!)
            segmentsCount += s.R == null ? 1 : s.R.Value;

        var urls = new string[segmentsCount];
        for (var i = 0; i < segmentsCount; i++)
            urls[i] = MediaUrl.Replace("$Number$", i.ToString());

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