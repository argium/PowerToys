// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Peek.Common.Extensions;
using Peek.Common.Helpers;
using Peek.Common.Models;
using Peek.FilePreviewer.Models;
using Windows.Foundation;

namespace Peek.FilePreviewer.Previewers
{
    /// <summary>
    /// Provides an STL or 3MF file to the interactive 3D viewer.
    /// </summary>
    public partial class Model3DPreviewer : ObservableObject, IPreviewer
    {
        private readonly Func<Func<Task>, Task> runOnUiThread;

        [ObservableProperty]
        private string? preview;

        [ObservableProperty]
        private PreviewState state;

        /// <summary>
        /// Creates a previewer for a 3D model file.
        /// </summary>
        /// <param name="file">The file to preview.</param>
        public Model3DPreviewer(IFileSystemItem file)
            : this(file, DispatcherQueue.GetForCurrentThread().RunOnUiThread)
        {
        }

        internal Model3DPreviewer(IFileSystemItem file, Func<Func<Task>, Task> runOnUiThread)
        {
            Item = file;
            this.runOnUiThread = runOnUiThread;
        }

        private IFileSystemItem Item { get; }

        /// <summary>
        /// Determines whether the item is an STL or 3MF file.
        /// </summary>
        /// <param name="item">The item to inspect.</param>
        /// <returns>Whether the item is a supported file.</returns>
        public static bool IsItemSupported(IFileSystemItem item)
        {
            return item is FileItem &&
                (string.Equals(item.Extension, ".stl", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Extension, ".3mf", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public Task<PreviewSize> GetPreviewSizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PreviewSize { MonitorSize = new Size(800, 500), UseEffectivePixels = true });
        }

        /// <inheritdoc />
        public Task LoadPreviewAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PreviewState.Loading;
            Preview = Item.Path;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task CopyAsync()
        {
            await runOnUiThread(async () =>
            {
                var storageItem = await Item.GetStorageItemAsync();
                ClipboardHelper.SaveToClipboard(storageItem);
            });
        }
    }
}
