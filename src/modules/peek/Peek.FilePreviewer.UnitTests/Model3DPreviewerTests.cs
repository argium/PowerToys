// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Peek.Common.Models;
using Peek.FilePreviewer.Previewers;
using Windows.Storage;

namespace Peek.FilePreviewer.UnitTests
{
    [TestClass]
    public class Model3DPreviewerTests
    {
        [DataTestMethod]
        [DataRow("model.stl")]
        [DataRow("model.STL")]
        [DataRow("model.3mf")]
        [DataRow("model.3MF")]
        public void IsItemSupported_3DModel_ReturnsTrue(string name)
        {
            var item = new FileItem(@"C:\Models\" + name, name);

            Assert.IsTrue(Model3DPreviewer.IsItemSupported(item));
        }

        [TestMethod]
        public void IsItemSupported_OtherFileAndFolder_ReturnsFalse()
        {
            var file = new FileItem(@"C:\Models\model.obj", "model.obj");
            var folder = new FolderItem(@"C:\Models\model.stl", "model.stl", "model.stl");

            Assert.IsFalse(Model3DPreviewer.IsItemSupported(file));
            Assert.IsFalse(Model3DPreviewer.IsItemSupported(folder));
        }

        [DataTestMethod]
        [DataRow("model.stl")]
        [DataRow("model.3mf")]
        public async Task LoadPreviewAsync_Model_ProvidesFileToViewer(string name)
        {
            var path = @"C:\Models\" + name;
            var previewer = new Model3DPreviewer(new FileItem(path, name), work => work());

            await previewer.LoadPreviewAsync(CancellationToken.None);

            Assert.AreEqual(path, previewer.Preview);
            Assert.AreEqual(PreviewState.Loading, previewer.State);
        }

        [TestMethod]
        public async Task LoadPreviewAsync_Cancelled_DoesNotStartPreview()
        {
            var previewer = new Model3DPreviewer(new FileItem(@"C:\Models\model.stl", "model.stl"), work => work());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await previewer.LoadPreviewAsync(cancellation.Token));

            Assert.IsNull(previewer.Preview);
        }

        [TestMethod]
        public async Task CopyAsync_DispatchesAndPropagatesStorageFailure()
        {
            var expected = new IOException("Storage item unavailable.");
            var dispatched = false;
            var previewer = new Model3DPreviewer(
                new FailingStorageItem(expected),
                async work =>
                {
                    dispatched = true;
                    await work();
                });

            var actual = await Assert.ThrowsExactlyAsync<IOException>(() => previewer.CopyAsync());

            Assert.IsTrue(dispatched);
            Assert.AreSame(expected, actual);
        }

        private sealed class FailingStorageItem(Exception error) : IFileSystemItem
        {
            public string Name => "model.stl";

            public string ParsingName => string.Empty;

            public string Path => @"C:\Models\model.stl";

            public string Extension => ".stl";

            public Task<IStorageItem> GetStorageItemAsync() => Task.FromException<IStorageItem>(error);
        }
    }
}
