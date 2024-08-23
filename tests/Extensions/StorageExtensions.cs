using System.Runtime.CompilerServices;
using OwlCore.Storage;

namespace OwlCore.Nomad.Storage.Kubo.Tests;

public static class StorageExtensions
{
    public static async IAsyncEnumerable<IChildFile> CreateFilesAsync(this IModifiableFolder folder, int fileCount, Func<int, string> getFileName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < fileCount; i++)
            yield return await folder.CreateFileAsync(getFileName(i), overwrite: true, cancellationToken: cancellationToken);
    }

    public static async Task WriteRandomBytes(this IFile file, int numberOfBytes, CancellationToken cancellationToken)
    {
        var rnd = new Random();
        var bytes = new byte[numberOfBytes];
        rnd.NextBytes(bytes);
        
        await file.WriteBytesAsync(bytes, cancellationToken: cancellationToken);
    }
}