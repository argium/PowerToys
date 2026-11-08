// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Peek.FilePreviewer.Previewers;

namespace Peek.FilePreviewer.UnitTests
{
    [TestClass]
    public class Model3DFileTests
    {
        [TestMethod]
        public async Task ReadAsync_Stl_PreservesFileContents()
        {
            byte[] contents = Encoding.UTF8.GetBytes("solid model\nendsolid model");
            using var input = new MemoryStream(contents);

            using var result = await Model3DFile.ReadAsync(input, is3Mf: false, CancellationToken.None);

            CollectionAssert.AreEqual(contents, result.ToArray());
            Assert.AreEqual(0, result.Position);
        }

        [TestMethod]
        public async Task ReadAsync_OversizedFile_RejectsBeforeReading()
        {
            using var input = new OversizedStream();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await Model3DFile.ReadAsync(input, is3Mf: false, CancellationToken.None));
        }

        [TestMethod]
        public async Task ReadAsync_3MfWithinLimit_PreservesArchive()
        {
            byte[] contents = CreateArchive("one", "two");
            using var input = new MemoryStream(contents);

            using var result = await Model3DFile.ReadAsync(input, is3Mf: true, CancellationToken.None);

            CollectionAssert.AreEqual(contents, result.ToArray());
            Assert.AreEqual(0, result.Position);
        }

        [TestMethod]
        public async Task ReadAsync_3MfExpandedSizeExceedsLimit_RejectsArchive()
        {
            byte[] contents = CreateArchive("one", "two");
            int entryCount = 0;
            for (int i = 0; i <= contents.Length - 46; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(i, 4)) == 0x02014b50)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(i + 24, 4), (256U * 1024 * 1024) + 1);
                    entryCount++;
                }
            }

            Assert.AreEqual(2, entryCount);
            using var input = new MemoryStream(contents);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await Model3DFile.ReadAsync(input, is3Mf: true, CancellationToken.None));
        }

        [TestMethod]
        public async Task ReadAsync_Invalid3Mf_RejectsArchive()
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("not a zip archive"));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await Model3DFile.ReadAsync(input, is3Mf: true, CancellationToken.None));
        }

        [TestMethod]
        public async Task ReadAsync_Cancelled_DoesNotRead()
        {
            using var input = new MemoryStream([1, 2, 3]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await Model3DFile.ReadAsync(input, is3Mf: false, cancellation.Token));

            Assert.AreEqual(0, input.Position);
        }

        private static byte[] CreateArchive(params string[] contents)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int i = 0; i < contents.Length; i++)
                {
                    using var writer = new StreamWriter(archive.CreateEntry($"part{i}.model").Open());
                    writer.Write(contents[i]);
                }
            }

            return output.ToArray();
        }

        private sealed class OversizedStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => (256L * 1024 * 1024) + 1;

            public override long Position { get; set; }

            public override void Flush() => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The oversized stream must not be read.");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
