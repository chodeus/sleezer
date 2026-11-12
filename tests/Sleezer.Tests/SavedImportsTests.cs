using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using Xunit;

namespace Sleezer.Tests;

public class SavedImportsTests
{
    [Fact]
    public void A_file_not_saved_yet_is_an_import()
    {
        Assert.True(SavedImports.IsImport(new object(), 0));
    }

    [Fact]
    public void A_file_Lidarr_saved_before_the_metadata_pass_is_still_an_import()
    {
        object trackFile = new();
        SavedImports.Saved(trackFile);

        Assert.True(SavedImports.IsImport(trackFile, 42));
    }

    [Fact]
    public void A_saved_file_a_scan_read_back_is_not_an_import()
    {
        object added = new();
        SavedImports.Saved(added);

        Assert.False(SavedImports.IsImport(new object(), 42));
    }

    [Fact]
    public void A_saved_import_is_converted_once()
    {
        object trackFile = new();
        SavedImports.Saved(trackFile);
        SavedImports.IsImport(trackFile, 42);

        Assert.False(SavedImports.IsImport(trackFile, 42));
    }
}
