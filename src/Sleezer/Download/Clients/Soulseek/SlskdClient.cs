using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public class SlskdClient : DownloadClientBase<SlskdProviderSettings>
{
    private readonly ISlskdDownloadManager _manager;
    private readonly ISlskdApiClient _apiClient;
    private readonly Lazy<IIndexerFactory> _indexerFactory;

    public override string Name => "Slskd";
    public override string Protocol => nameof(SoulseekDownloadProtocol);

    public SlskdClient(
        ISlskdDownloadManager manager,
        ISlskdApiClient apiClient,
        IConfigService configService,
        IDiskProvider diskProvider,
        IRemotePathMappingService remotePathMappingService,
        ILocalizationService localizationService,
        Lazy<IIndexerFactory> indexerFactory,
        Logger logger)
        : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
    {
        _manager = manager;
        _apiClient = apiClient;
        _indexerFactory = indexerFactory;
    }

    public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) =>
        await _manager.DownloadAsync(remoteAlbum, Definition.Id, Connected());

    public override IEnumerable<DownloadClientItem> GetItems()
    {
        DownloadClientItemClientInfo clientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
        foreach (DownloadClientItem item in _manager.GetItems(Definition.Id, Connected(), GetRemoteToLocal()))
        {
            item.DownloadClientInfo = clientInfo;
            yield return item;
        }
    }

    public override void RemoveItem(DownloadClientItem clientItem, bool deleteData) =>
        _manager.RemoveItem(clientItem, deleteData, Definition.Id, Connected());

    public override DownloadClientInfo GetStatus()
    {
        SlskdProviderSettings settings = Connected();
        return new()
        {
            IsLocalhost = settings.IsLocalhost,
            OutputRootFolders = [_remotePathMappingService.RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath))]
        };
    }

    protected override void Test(List<ValidationFailure> failures)
    {
        try
        {
            Connected();
        }
        catch (DownloadClientException ex)
        {
            failures.Add(new ValidationFailure(nameof(SlskdProviderSettings.IndexerId), ex.Message));
            return;
        }

        // Explicit type argument prevents the compiler from inferring TSource = ValidationFailure?
        // (which would mismatch the List<ValidationFailure> parameter).
        var failure = _apiClient.TestConnectionAsync(Settings).GetAwaiter().GetResult();
        if (failure != null)
        {
            failures.Add(failure);
            return;
        }

        // Probe the slskd-reported download path on the local filesystem (translated via Remote Path Mapping).
        // Catches misconfigured RPM, wrong PUID/PGID, and unmounted volumes before the first download instead of mid-import.
        OsPath localPath = GetRemoteToLocal();
        if (localPath.IsEmpty)
        {
            failures.Add(new ValidationFailure("DownloadPath",
                $"Slskd reports download path '{Settings.DownloadPath}' but it could not be resolved locally. " +
                $"If slskd runs on a different host, configure a Remote Path Mapping for host '{Settings.Host}'."));
            return;
        }

        ValidationFailure folderFailure = TestFolder(localPath.FullPath, "DownloadPath");
        if (folderFailure != null)
            failures.Add(folderFailure);
    }

    public override object RequestAction(string action, IDictionary<string, string> query) =>
        action == IndexerLogin.OptionsAction
            ? IndexerLogin.Options<SlskdSettings>(_indexerFactory.Value.All())
            : base.RequestAction(action, query);

    // Lazy: the indexer factory is resolved alongside the download clients.
    private SlskdProviderSettings Connected()
    {
        Settings.UseLogin(IndexerLogin.Find<SlskdSettings>(_indexerFactory.Value.All(), Settings.IndexerId, "Slskd"));
        return Settings;
    }

    private OsPath GetRemoteToLocal() =>
        _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(Settings.DownloadPath));
}
