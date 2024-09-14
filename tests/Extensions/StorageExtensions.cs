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

    public static async Task WriteRandomBytes(this IFile file, long numberOfBytes, int bufferSize, CancellationToken cancellationToken)
    {
        var rnd = new Random();

        await using var fileStream = await file.OpenWriteAsync(cancellationToken);

        var bytes = new byte[bufferSize];
        var bytesWritten = 0L;
        while (bytesWritten < numberOfBytes)
        {
            var remaining = numberOfBytes - bytesWritten;
            
            // Always runs if there are bytes left, even if there's fewer bytes left than the buffer.
            // Truncate the buffer size to remaining length if smaller than buffer.
            if (bufferSize > remaining)
                bufferSize = (int)remaining;

            if (bytes.Length != bufferSize)
                bytes = new byte[bufferSize];
            
            rnd.NextBytes(bytes);

            await fileStream.WriteAsync(bytes, cancellationToken);
            bytesWritten += bufferSize;
        }
    }
}