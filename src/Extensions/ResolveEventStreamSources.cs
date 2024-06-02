using Ipfs;
using Ipfs.CoreApi;
using OwlCore.Nomad;
using OwlCore.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OwlCore.Nomad.Kubo;

namespace OwlCore.Kubo.Nomad.Extensions;

/// <summary>
/// Extension methods for resolving event stream sources.
/// </summary>
public static class ResolveEventStreamSources
{
    /// <summary>
    /// Resolves the full event stream for this folder from all sources, organized by date.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns></returns>
    public static async Task<IEnumerable<KuboNomadEventStreamEntry>> ResolveEventStreamsAsync(this IEnumerable<EventStream<Cid>> eventStreams, ICoreApi client, bool useCache, CancellationToken cancellationToken)
    {
        // Get all event entries across all sources
        var allEventEntries = await eventStreams
            .Select(x => x.Entries)
            .Aggregate((x, y) =>
            {
                foreach (var item in y)
                    x.Add(item);
                return x;
            })
            .InParallel(x => x.ResolveDagCidAsync<KuboNomadEventStreamEntry>(client, nocache: !useCache, cancellationToken));

        var sortedEventEntries = allEventEntries
            .Select(x => x.Result)
            .PruneNull()
            .OrderBy(x => x.TimestampUtc);

        return sortedEventEntries;
    }
}