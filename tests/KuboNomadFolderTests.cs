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

[TestClass]
public partial class KuboNomadFolderTests
{
    private void LoggerOnMessageReceived(object? sender, LoggerMessageEventArgs args)
    {
        Debug.WriteLine(args.Message);
    }

    private async Task VerifyFolderContents(SystemFolder sourceFolder, SystemFolder destinationFolder, CancellationToken cancellationToken)
    {
        var pushedFiles = await sourceFolder.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken: cancellationToken);
        var pulledFiles = await destinationFolder.GetFilesAsync(cancellationToken: cancellationToken).OrderBy(x => x.Name).ToListAsync(cancellationToken: cancellationToken);

        Guard.IsGreaterThan(pushedFiles.Count, 0);
        Guard.IsGreaterThan(pulledFiles.Count, 0);
        Guard.IsEqualTo(pushedFiles.Count, pulledFiles.Count);

        foreach (var pushedFile in pushedFiles)
        {
            var pulledFile = pulledFiles.First(x => x.Name == pushedFile.Name);

            var pushedFileBytes = await pushedFile.ReadBytesAsync(cancellationToken);
            var pulledFileBytes = await pulledFile.ReadBytesAsync(cancellationToken);
            CollectionAssert.AreEqual(pushedFileBytes, pulledFileBytes);
        }
    }

    private static async Task AddPeerToSwarmAsync(IGenericApi clientA, ICoreApi clientB, CancellationToken cancellationToken)
    {
        var peerB = await clientA.IdAsync(cancel: cancellationToken);
        foreach (var address in peerB.Addresses)
        {
            try
            {
                await clientB.Swarm.ConnectAsync(address, cancellationToken);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task<NomadFolderData<Cid>> ResolveAndValidatePublishedRoamingSeedAsync(ICoreApi client, IKey roamingKey, KuboOptions kuboOptions, CancellationToken cancellationToken)
    {
        var (publishedRoaming, _) = await client.ResolveDagCidAsync<NomadFolderData<Cid>>(roamingKey.Id, nocache: !kuboOptions.UseCache, cancellationToken);
        Guard.IsNotNull(publishedRoaming);
        {
            Guard.IsNotEmpty(publishedRoaming.Sources);
            Guard.IsNotNullOrWhiteSpace(publishedRoaming.StorableItemId);
            Guard.IsNotNullOrWhiteSpace(publishedRoaming.StorableItemName);
            Guard.IsEmpty(publishedRoaming.Files);
            Guard.IsEmpty(publishedRoaming.Folders);
        }
        return publishedRoaming;
    }

    public async Task EncryptedPubSubNomadPairAsync(KuboBootstrapper kubo, IKuboOptions kuboOptions, ICoreApi client, IGenericApi genericApi, IKey localKey, bool isRoamingReceiver, string roamingKeyName, string roomName, string password, CancellationToken cancellationToken = default)
    {
        // Setup encrypted pubsub
        var thisPeer = await genericApi.IdAsync(cancel: cancellationToken);
        var encryptedPubSub = new AesPasswordEncryptedPubSub(client.PubSub, password, salt: roomName);
        using var peerRoom = new PeerRoom(thisPeer, encryptedPubSub, $"{roomName}")
        {
            HeartbeatEnabled = false,
        };
        
        // Local key must be initialized prior to pairing
        // Roaming key must exist on the 'roaming sender' node, must not exist on 'roaming receiver' node.
        // The node that receives a roaming key should be a sender for local key, and vice versa.
        await KeyExchange.ExchangeRoamingKeyAsync(peerRoom, roamingKeyName, isReceiver: isRoamingReceiver, kubo, kuboOptions, client, cancellationToken);

        // The node that sends a roaming key should be receiver for local key, and vice versa.
        var isLocalReceiver = !isRoamingReceiver;
        await KeyExchange.ExchangeLocalSourceAsync(peerRoom, localKey, roamingKeyName, isReceiver: isLocalReceiver, kuboOptions, client, cancellationToken);
    }

    public static DateTime GetLastWriteTimeFor(IStorable storable, IEnumerable<EventStreamEntry<Cid>> eventStreamEntries)
    {
        return storable switch
        {
            KuboNomadFile nFile => eventStreamEntries.Where(x => x.TargetId == nFile.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            KuboNomadFolder nFolder => eventStreamEntries.Where(x => x.TargetId == nFolder.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            ReadOnlyKuboNomadFile nFile => eventStreamEntries.Where(x => x.TargetId == nFile.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            ReadOnlyKuboNomadFolder nFolder => eventStreamEntries.Where(x => x.TargetId == nFolder.Id).Max(x => x.TimestampUtc) ?? ThrowHelper.ThrowNotSupportedException<DateTime>("Unhandled code path"),
            SystemFolder systemFolder => systemFolder.Info.LastWriteTimeUtc,
            SystemFile systemFile => systemFile.Info.LastWriteTimeUtc,
            _ => throw new ArgumentOutOfRangeException(nameof(storable), storable, null)
        };
    }
    
    /// <summary>
    /// Creates a temp folder for the test fixture to work in, safely unlocking and removing existing files if needed.
    /// </summary>
    /// <returns>The folder that was created.</returns>
    public static async Task<SystemFolder> SafeCreateFolderAsync(SystemFolder rootFolder, string name, CancellationToken cancellationToken)
    {
        // When Kubo is stopped unexpectedly, it may leave some files with a ReadOnly attribute.
        // Since this folder is created every time tests are run, we need to clean up any files leftover from prior runs.
        // To do that, we need to remove the ReadOnly file attribute.
        var testTempRoot = (SystemFolder)await rootFolder.CreateFolderAsync(name, overwrite: false, cancellationToken: cancellationToken);
        await SetAllFileAttributesRecursive(testTempRoot, attributes => attributes & ~FileAttributes.ReadOnly);

        // Delete and recreate the folder.
        return (SystemFolder)await rootFolder.CreateFolderAsync(name, overwrite: true, cancellationToken: cancellationToken);
    }
    
    /// <summary>
    /// Changes the file attributes of all files in all subfolders of the provided <see cref="SystemFolder"/>.
    /// </summary>
    /// <param name="rootFolder">The folder to set file permissions in.</param>
    /// <param name="transform">This function is provided the current file attributes, and should return the new file attributes.</param>
    public static async Task SetAllFileAttributesRecursive(SystemFolder rootFolder, Func<FileAttributes, FileAttributes> transform)
    {
        await foreach (var childFile in rootFolder.GetFilesAsync())
        {
            var file = (SystemFile)childFile;
            file.Info.Attributes = transform(file.Info.Attributes);
        }

        await foreach (var childFolder in rootFolder.GetFoldersAsync())
        {
            var folder = (SystemFolder)childFolder;
            await SetAllFileAttributesRecursive(folder, transform);
        }
    }
    
    private static async Task<KuboBootstrapper> BootstrapKuboAsync(SystemFolder kuboRepo, int apiPort, int gatewayPort, CancellationToken cancellationToken)
    {
        var kubo = new KuboBootstrapper(kuboRepo.Path)
        {
            ApiUri = new Uri($"http://127.0.0.1:{apiPort}"),
            GatewayUri = new Uri($"http://127.0.0.1:{gatewayPort}"),
            RoutingMode = DhtRoutingMode.None,
            LaunchConflictMode = BootstrapLaunchConflictMode.Attach,
            ApiUriMode = ConfigMode.OverwriteExisting,
            GatewayUriMode = ConfigMode.OverwriteExisting,
        };
                
        await kubo.StartAsync(cancellationToken);
        return kubo;
    }
    
    private async Task<CachedCoreApi> CreateCachedClientAsync(KuboBootstrapper kubo, CancellationToken cancellationToken)
    {
        var cacheFolder = (SystemFolder)await kubo.RepoFolder.CreateFolderAsync(".cache", overwrite: false, cancellationToken: cancellationToken);
        var cacheLayer = new CachedCoreApi(cacheFolder, kubo.Client);

        await cacheLayer.InitAsync(cancellationToken);
        return cacheLayer;
    }

    /// <summary>
    /// Creates roaming and local storage keys and yields a default recommended value, but does not publish the value to the newly created keys.
    /// </summary>
    /// <param name="localKeyName">The name of the local key to create.</param>
    /// <param name="roamingKeyName">The name of the roaming key to create.</param>
    /// <param name="nomadFolderName">The root folder name to use in the roaming data.</param>
    /// <param name="eventStreamLabel">The label to use for the created local event stream.</param>
    /// <param name="client">A client to use for communicating with ipfs.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    private async Task<((IKey Key, EventStream<Cid> Value) LocalKey, (IKey Key, NomadFolderData<Cid> Value) RoamingKey)> CreateStorageKeysAsync(string localKeyName, string roamingKeyName, string nomadFolderName, string eventStreamLabel, ICoreApi client, CancellationToken cancellationToken)
    {
        // Get or create ipns key
        var enumerableKeys = await client.Key.ListAsync(cancellationToken);
        var keys = enumerableKeys as IKey[] ?? enumerableKeys.ToArray();

        var localKey = keys.FirstOrDefault(x => x.Name == localKeyName);
        var roamingKey = keys.FirstOrDefault(x => x.Name == roamingKeyName);
        
        // Key should not be created yet
        Guard.IsNull(localKey);
        Guard.IsNull(roamingKey);
        
        localKey = await client.Key.CreateAsync(localKeyName, "ed25519", size: 4096, cancellationToken);
        roamingKey = await client.Key.CreateAsync(roamingKeyName, "ed25519", size: 4096, cancellationToken);

        // Get default value and cid
        // ---
        // Roaming should not be exported/imported without including at least one local source,
        // otherwise we'd have an extra step exporting local separately from roaming from A to B.
        // ---
        // This is also retroactively handled when publishing an event stream to roaming, but we're only defining the seed values here.
        var defaultLocalValue = new EventStream<Cid>
        {
            Label = eventStreamLabel,
            TargetId = roamingKey.Id,
            Entries = [],
        };

        var defaultRoamingValue = new NomadFolderData<Cid>
        {
            StorableItemId = roamingKey.Id,
            StorableItemName = nomadFolderName,
            Files = [],
            Folders = [],
            Sources = [localKey.Id],
        };

        return ((localKey, defaultLocalValue), (roamingKey, defaultRoamingValue));
    }

    private static Task<(IKey Key, EventStream<Cid> Value)> GetOrCreateLocalStorageKeyAsyc(string localKeyName, string eventStreamLabel, IKey roamingKey, KuboOptions kuboOptions, ICoreApi client, CancellationToken cancellationToken)
    {
        // Local key should contain an empty event stream with the roaming key as the targetid
        return client.GetOrCreateKeyAsync(localKeyName, _ => new EventStream<Cid>
        {
            Label = eventStreamLabel,
            TargetId = roamingKey.Id, 
            Entries = [],
        }, kuboOptions.IpnsLifetime, nocache: !kuboOptions.UseCache, size: 4096, cancellationToken);
    }
}