using System.Collections.Generic;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// Provides methods for getting repositories for getting and managing instances of nomad-based folders. 
/// </summary>
public static class StorageRepoFactory
{
    /// <summary>
    /// Gets a repository for managing nomad folders.
    /// </summary>
    /// <param name="roamingKeyName">The key name used to publish the roaming data.</param>
    /// <param name="localKeyName">The key name used to publish the local event stream.</param>
    /// <param name="tempCacheFolder">A temp folder for caching during read and persisting writes during flush.</param>
    /// <param name="allEventStreamEntries">A collection of all event stream entries.</param>
    /// <param name="listeningEventStreamHandlers">The event stream handlers that are listening for updates.</param>
    /// <param name="client">The IPFS client used to interact with the network.</param>
    /// <param name="kuboOptions">The options used to read and write data to and from Kubo.</param>
    /// <param name="folderName">The name of the folder to ues, if not already set.</param>
    /// <returns>A repository for managing peer swarms.</returns>
    public static NomadKuboRepository<KuboNomadFolder, IFolder, NomadFolderData<Cid>, FolderUpdateEvent> GetFolderRepository(string roamingKeyName, string localKeyName, string folderName, IModifiableFolder tempCacheFolder, List<EventStreamEntry<Cid>> allEventStreamEntries, List<ISharedEventStreamHandler<Cid, EventStream<Cid>, EventStreamEntry<Cid>>> listeningEventStreamHandlers, ICoreApi client, IKuboOptions kuboOptions)
    {
        return new NomadKuboRepository<KuboNomadFolder, IFolder, NomadFolderData<Cid>, FolderUpdateEvent>
        {
            DefaultEventStreamLabel = folderName,
            Client = client,
            GetEventStreamHandlerConfigAsync = async (roamingId, cancellationToken) =>
            {
                var (localKey, roamingKey, foundRoamingId) = await NomadKeyHelpers.RoamingIdToNomadKeysAsync(roamingId, localKeyName, roamingKeyName, client, cancellationToken);
                return new NomadKuboEventStreamHandlerConfig<NomadFolderData<Cid>>
                {
                    RoamingId = roamingKey?.Id ?? foundRoamingId,
                    RoamingKey = roamingKey,
                    RoamingKeyName = roamingKeyName,
                    LocalKey = localKey,
                    LocalKeyName = localKeyName,
                    ListeningEventStreamHandlers = listeningEventStreamHandlers,
                    AllEventStreamEntries = allEventStreamEntries,
                };
            },
            GetDefaultRoamingValue = (localKey, roamingKey) => new NomadFolderData<Cid>
            {
                StorableItemId = roamingKey.Id,
                StorableItemName = folderName,
            },
            // how to pass a parent instance?
            // lots of small changes around the storage impl to fix, go nuts.
            // needs repo usage internally as well, might get rid of some of this. 
            ModifiableFromHandlerConfig = config => KuboNomadFolder.FromHandlerConfig(config, tempCacheFolder, kuboOptions, client),
            ReadOnlyFromHandlerConfig = config => ReadOnlyKuboNomadFolder.FromHandlerConfig(config, kuboOptions, client),
        };
    }
}
