using System;
using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Kubo.Models;
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
    /// <param name="client">The IPFS client used to interact with the network.</param>
    /// <param name="kuboOptions">The options used to read and write data to and from Kubo.</param>
    /// <param name="folderName">The name of the folder to ues, if not already set.</param>
    /// <returns>A repository for managing peer swarms.</returns>
    public static NomadKuboRepository<NomadKuboFolder, IFolder, NomadFolderData<DagCid, Cid>, FolderUpdateEvent> GetFolderRepository(string roamingKeyName, string localKeyName, string folderName, IModifiableFolder tempCacheFolder, ICoreApi client, IKuboOptions kuboOptions)
    {
        return new NomadKuboRepository<NomadKuboFolder, IFolder, NomadFolderData<DagCid, Cid>, FolderUpdateEvent>
        {
            DefaultEventStreamLabel = $"Folder {folderName}",
            Client = client,
            KuboOptions = kuboOptions,
            GetEventStreamHandlerConfigAsync = async (roamingId, cancellationToken) =>
            {
                var (localKey, roamingKey, foundRoamingId) = await NomadKeyHelpers.RoamingIdToNomadKeysAsync(roamingId, roamingKeyName, localKeyName, client, cancellationToken);
                return new NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>>
                {
                    RoamingId = roamingKey?.Id ?? (foundRoamingId is not null ? Cid.Decode(foundRoamingId) : null),
                    RoamingKey = roamingKey,
                    RoamingKeyName = roamingKeyName,
                    LocalKey = localKey,
                    LocalKeyName = localKeyName,
                };
            },
            GetDefaultRoamingValue = (localKey, roamingKey) => new NomadFolderData<DagCid, Cid>
            {
                StorableItemId = roamingKey.Id,
                StorableItemName = folderName,
                Sources = [localKey.Id],
            },
            ModifiableFromHandlerConfig = config => NomadKuboFolder.FromHandlerConfig(config, tempCacheFolder, kuboOptions, client),
            ReadOnlyFromHandlerConfig = config => ReadOnlyNomadKuboFolder.FromHandlerConfig(config, client),
        };
    }
}
