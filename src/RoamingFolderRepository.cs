using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ipfs;
using OwlCore.Nomad.Kubo;
using OwlCore.Nomad.Storage.Models;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A repository for managing nomad folders. This class is used to create, delete, and update nomad folders on your Kubo node.
/// </summary>
public class RoamingFolderRepository : NomadKuboRepository<NomadKuboFolder, IFolder, NomadFolderData<DagCid, Cid>, FolderUpdateEvent, string>
{
    /// <summary>
    /// A temp folder for caching during read and persisting writes during flush.  
    /// </summary>
    public required IModifiableFolder TempCacheFolder { get; init; }

    /// <summary>
    /// The prefix used to build local and roaming key names for any given folder.
    /// </summary>
    public string KeyNamePrefix { get; set; } = "Nomad.Kubo.Storage";
    
    /// <inheritdoc/> 
    public override IFolder ReadOnlyFromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>> handlerConfig) => ReadOnlyNomadKuboFolder.FromHandlerConfig(handlerConfig, Client);

    /// <inheritdoc/> 
    public override NomadKuboFolder ModifiableFromHandlerConfig(NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>> handlerConfig) => NomadKuboFolder.FromHandlerConfig(handlerConfig, TempCacheFolder, KuboOptions, Client);

    /// <inheritdoc/> 
    protected override NomadKuboEventStreamHandlerConfig<NomadFolderData<DagCid, Cid>> GetEmptyConfig() => new();

    /// <inheritdoc/> 
    public override Task<(string LocalKeyName, string RoamingKeyName)?> GetExistingKeyNamesAsync(string roamingId, CancellationToken cancellationToken)
    {
        var existingRoamingKey = ManagedKeys.FirstOrDefault(x=> $"{x.Id}" == $"{roamingId}");
        if (existingRoamingKey is null)
            return Task.FromResult<(string LocalKeyName, string RoamingKeyName)?>(null);
        
        // Transform roaming key name into local key name
        // This repository implementation doesn't do anything fancy for this,
        // the names are basically hardcoded to the KeyNamePrefix and the original folderName.
        var localKeyName = existingRoamingKey.Name.Replace(".Roaming.", ".Local.");
        return Task.FromResult<(string LocalKeyName, string RoamingKeyName)?>((localKeyName, existingRoamingKey.Name));
    }

    /// <inheritdoc/> 
    public override (string LocalKeyName, string RoamingKeyName) GetNewKeyNames(string folderName)
    {
        // Get unique key names for the given folder name
        return (LocalKeyName: $"{KeyNamePrefix}.Local.{folderName}", RoamingKeyName: $"{KeyNamePrefix}.Roaming.{folderName}");
    }
    
    /// <inheritdoc/> 
    public override string GetNewEventStreamLabel(string folderName, IKey roamingKey, IKey localKey) => folderName;
    
    /// <inheritdoc/> 
    public override NomadFolderData<DagCid, Cid> GetInitialRoamingValue(string folderName, IKey roamingKey, IKey localKey) => new()
    {
        StorableItemId = roamingKey.Id,
        StorableItemName = folderName,
        Sources = [localKey.Id],
    };
}
