using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Saves go to a temp file and are moved into place: an in-place write cut short by a full disk lost every setting.
public sealed class PluginSettingsTests : IDisposable
{
    private readonly string _appData = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}")).FullName;

    private sealed class AppFolder(string appData) : IAppFolderInfo
    {
        public string AppDataFolder => appData;
        public string TempFolder => Path.GetTempPath();
        public string StartUpFolder => appData;
    }

    [Fact]
    public void A_saved_value_reads_back_and_leaves_no_temp_file()
    {
        new PluginSettings(new AppFolder(_appData), LogManager.GetCurrentClassLogger()).SetValue("token", "abc");

        PluginSettings reopened = new(new AppFolder(_appData), LogManager.GetCurrentClassLogger());

        Assert.Equal("abc", reopened.GetValue<string>("token"));
        Assert.Empty(Directory.GetFiles(_appData, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() => Directory.Delete(_appData, recursive: true);
}
