using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using OwlCore.Kubo.FolderWatchers;

namespace OwlCore.Nomad.Storage.Kubo;

/// <summary>
/// A timer-based folder watcher that watches a <see cref="ReadOnlyKuboNomadFolder"/>.
/// </summary>
public class TimerBasedNomadFolderWatcher : TimerBasedFolderWatcher
{
    /// <summary>
    /// Creates a new instance of <see cref="TimerBasedNomadFolderWatcher"/>.
    /// </summary>
    /// <param name="kuboNomadFolder">The folder to watch.</param>
    /// <param name="interval">The update interval.</param>
    public TimerBasedNomadFolderWatcher(IReadOnlyKuboBasedNomadFolder kuboNomadFolder, TimeSpan interval) : base(kuboNomadFolder, interval)
    {
    }

    /// <inheritdoc/>
    public override event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc/>
    public override Task ExecuteAsync()
    {
        // TODO
        throw new NotImplementedException();
    }
}