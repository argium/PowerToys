// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Peek.FilePreviewer.Previewers
{
    internal static class Model3DFile
    {
        private const long MaxFileSize = 256L * 1024 * 1024;
        internal const long MaxArchiveSize = 512L * 1024 * 1024;

        internal static async Task<MemoryStream> ReadAsync(Stream source, bool is3Mf, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long length = source.Length - source.Position;
            if (length < 0 || length > MaxFileSize)
            {
                throw new InvalidDataException("The 3D model exceeds the 256 MiB preview limit.");
            }

            byte[] contents = GC.AllocateUninitializedArray<byte>((int)length);
            await source.ReadExactlyAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (is3Mf)
            {
                using var archiveStream = new MemoryStream(contents, writable: false);
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                long uncompressedSize = 0;
                long compressedSize = 0;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Length > MaxArchiveSize - uncompressedSize ||
                        entry.CompressedLength > MaxArchiveSize - compressedSize)
                    {
                        throw new InvalidDataException("The 3MF archive exceeds the 512 MiB preview limit.");
                    }

                    uncompressedSize += entry.Length;
                    compressedSize += entry.CompressedLength;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new MemoryStream(contents, writable: false);
        }
    }
}
