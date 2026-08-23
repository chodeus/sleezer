using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Issue #85's remaining gap: the peer's claim can only be settled after the bytes
// land. These pin what the verdict is once both sides are known.
public class AudioQualityVerifierTests
{
    private static AudioQualityReading Actual(int? depth, int? rate = 44100, int files = 12, bool mixed = false)
        => new(depth, rate, files, mixed);

    // The case that opened the issue: advertised 24-bit, every file 16/44.1.
    [Fact]
    public void Compare_flags_a_depth_the_files_do_not_have()
    {
        Assert.Equal(QualityVerdict.Overstated,
            AudioQualityVerifier.Compare(advertisedDepth: 24, advertisedRate: 44100, Actual(16)));
    }

    [Fact]
    public void Compare_flags_a_sample_rate_the_files_do_not_have()
    {
        Assert.Equal(QualityVerdict.Overstated,
            AudioQualityVerifier.Compare(24, 96000, Actual(24, rate: 44100)));
    }

    [Fact]
    public void Compare_confirms_a_release_that_is_what_it_said()
    {
        Assert.Equal(QualityVerdict.Matches,
            AudioQualityVerifier.Compare(24, 96000, Actual(24, rate: 96000)));
    }

    [Fact]
    public void Compare_reports_mixed_before_anything_else()
    {
        // A folder assembled from two sources cannot be described by one claim, even
        // when the claim happens to match the first file read.
        Assert.Equal(QualityVerdict.Mixed,
            AudioQualityVerifier.Compare(24, 96000, Actual(24, rate: 96000, mixed: true)));
    }

    [Theory]
    [InlineData(null, 16)]   // peer advertised nothing
    [InlineData(24, null)]   // files are lossy, so there is no depth to read
    public void Compare_is_unknown_when_one_side_is_missing(int? advertised, int? read)
    {
        Assert.Equal(QualityVerdict.Unknown,
            AudioQualityVerifier.Compare(advertised, 44100, Actual(read)));
    }

    [Fact]
    public void Compare_is_unknown_when_no_file_could_be_read()
    {
        Assert.Equal(QualityVerdict.Unknown,
            AudioQualityVerifier.Compare(24, 44100, AudioQualityReading.None));
    }

    // Nothing was advertised, so there is nothing to verify — the depth alone decides.
    [Fact]
    public void Compare_does_not_penalise_a_rate_the_source_never_claimed()
    {
        Assert.Equal(QualityVerdict.Matches,
            AudioQualityVerifier.Compare(16, advertisedRate: null, Actual(16, rate: 44100)));
    }

    // The other direction is not symmetrical: a rate WAS advertised and the files did not
    // report one, so confirming it would vouch for something never checked.
    [Fact]
    public void Compare_will_not_confirm_a_rate_the_files_never_reported()
    {
        Assert.Equal(QualityVerdict.Unknown,
            AudioQualityVerifier.Compare(16, advertisedRate: 44100, Actual(16, rate: null)));
    }

    // Same hole via partial metadata: one file carries a rate, another does not, so the
    // agreed rate describes only the files that happened to have it.
    [Fact]
    public void Compare_will_not_confirm_a_rate_when_only_some_files_reported_one()
    {
        var partial = new AudioQualityReading(24, 96000, FilesRead: 12, Mixed: false, RateIncomplete: true);
        Assert.Equal(QualityVerdict.Unknown, AudioQualityVerifier.Compare(24, 96000, partial));
    }
}
