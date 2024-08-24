using System.Diagnostics;
using CommunityToolkit.Diagnostics;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Kubo;
using OwlCore.Kubo.Cache;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage.System.IO;
using OwlCore.Diagnostics;
using OwlCore.Extensions;
using OwlCore.Nomad.Kubo.Events;
using OwlCore.Nomad.Storage.Kubo.Tests.Extensions;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public partial class KuboNomadFolderTests
{
    [TestMethod]
    public async Task InitTestAsync()
    {
        Logger.MessageReceived += LoggerOnMessageReceived;
        var cancellationToken = CancellationToken.None;

        var temp = new SystemFolder(Path.GetTempPath());
        var testTempFolder = await SafeCreateFolderAsync(temp, $"{nameof(KuboNomadFolderTests)}.{nameof(InitTestAsync)}", cancellationToken);
        var kubo = await BootstrapKuboAsync(testTempFolder, 5011, 8011, cancellationToken);
        
        var folderId = nameof(InitTestAsync);
        var localKeyName = $"Nomad.Storage.Local.{folderId}";
        var roamingKeyName = $"Nomad.Storage.Roaming.{folderId}";

        var client = await CreateCachedClientAsync(kubo, cancellationToken);
        var (local, roaming) = await NomadStorageKeys.CreateStorageKeysAsync(localKeyName, roamingKeyName, folderId, folderId, client, cancellationToken);
        
        // Roaming key should contain an empty list of files and folders, as well as the sources used to construct the object. 
        Guard.IsNotNull(roaming.Key);
        Guard.IsNotNullOrWhiteSpace(roaming.Key.Id);
        {
            Guard.IsNotNull(roaming.Value);
            Guard.IsNotNullOrWhiteSpace(roaming.Value.StorableItemId);
            Guard.IsNotNullOrWhiteSpace(roaming.Value.StorableItemName);
            Guard.IsEmpty(roaming.Value.Files);
            Guard.IsEmpty(roaming.Value.Folders);
            Guard.IsNotEmpty(roaming.Value.Sources);
        }
        
        // Local key should contain an empty event stream with the roaming key as the targetid
        Guard.IsNotNull(local.Key);
        Guard.IsNotNullOrWhiteSpace(local.Key.Id);
        {
            Guard.IsNotNull(local.Value);
            Guard.IsNotNullOrWhiteSpace(local.Value.Label);
            Guard.IsNotNullOrWhiteSpace(local.Value.TargetId);
            Guard.IsEqualTo(local.Value.TargetId, $"{roaming.Key.Id}");
            Guard.IsEmpty(local.Value.Entries.ToArray());
        }

        await kubo.Client.ShutdownAsync();
        kubo.Dispose();
        await SetAllFileAttributesRecursive(testTempFolder, attributes => attributes & ~FileAttributes.ReadOnly);
        await temp.DeleteAsync(testTempFolder, cancellationToken);
        Logger.MessageReceived -= LoggerOnMessageReceived;
    }
}