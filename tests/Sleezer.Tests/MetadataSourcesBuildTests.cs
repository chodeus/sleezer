using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Metadata.Proxy;
using Xunit;

namespace Sleezer.Tests;

public class MetadataSourcesBuildTests
{
    [Fact]
    public void Standard_lidarr_has_no_metadata_sources()
    {
        Assert.False(MetadataSourcesBuild.Present);
    }

    [Fact]
    public void Finds_a_type_lidarr_core_has()
    {
        Assert.True(MetadataSourcesBuild.HasType(typeof(Artist).Assembly, typeof(Artist).FullName!));
    }

    [Fact]
    public void Looks_for_the_custom_build_source_interface()
    {
        Assert.Equal("NzbDrone.Core.MetadataSource.Sources.IMetadataSource", MetadataSourcesBuild.SourceInterfaceName);
    }
}
