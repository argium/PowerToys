// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ManagedCommon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Peek.Common.Helpers;
using Peek.FilePreviewer.Previewers;
using Windows.Storage.Streams;
using Windows.UI;

namespace Peek.FilePreviewer.Controls
{
    /// <summary>
    /// Displays an interactive 3D model using locally embedded viewer assets.
    /// </summary>
    public sealed partial class Model3DControl : UserControl, IDisposable
    {
        private const string Host = "peek-model.invalid";
        private const string ContentSecurityPolicy =
            "default-src 'none'; script-src 'self' 'sha256-HZ/sxa3bGmI5PVCRoRXXwAZZHxkM0/pkVez+UAeR2Es='; " +
            "style-src 'self'; connect-src 'self'; img-src blob:; object-src 'none'; base-uri 'none'; form-action 'none'";

        private readonly List<(IRandomAccessStream Adapter, Stream Source)> responseStreams = [];
        private CancellationTokenSource? sessionCancellation;
        private Model3DPreviewer? sessionPreviewer;
        private string? sessionPrefix;
        private string? pageUri;
        private string? format;
        private ulong? navigationId;
        private int generation;
        private bool isLoaded;
        private bool isConfigured;
        private bool isDisposed;

        /// <summary>
        /// Identifies the previewer bound to the control.
        /// </summary>
        public static readonly DependencyProperty PreviewerProperty = DependencyProperty.Register(
            nameof(Previewer),
            typeof(Model3DPreviewer),
            typeof(Model3DControl),
            new PropertyMetadata(null, (d, e) => ((Model3DControl)d).OnPreviewerChanged((Model3DPreviewer?)e.OldValue, (Model3DPreviewer?)e.NewValue)));

        /// <summary>
        /// Creates the 3D model viewer.
        /// </summary>
        public Model3DControl()
        {
            InitializeComponent();
            AutomationProperties.SetName(ModelBrowser, ResourceLoaderInstance.ResourceLoader.GetString("Model3DViewer_Name"));
            AutomationProperties.SetHelpText(ModelBrowser, ResourceLoaderInstance.ResourceLoader.GetString("Model3DViewer_Help"));
        }

        /// <summary>
        /// Gets or sets the previewer supplying the model.
        /// </summary>
        public Model3DPreviewer? Previewer
        {
            get => (Model3DPreviewer?)GetValue(PreviewerProperty);
            set => SetValue(PreviewerProperty, value);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            if (Previewer != null)
            {
                Previewer.PropertyChanged -= Previewer_PropertyChanged;
            }

            Clear();
            if (isConfigured)
            {
                var core = ModelBrowser.CoreWebView2;
                core.WebResourceRequested -= Core_WebResourceRequested;
                core.WebMessageReceived -= Core_WebMessageReceived;
                core.NavigationStarting -= Core_NavigationStarting;
                core.FrameNavigationStarting -= Core_FrameNavigationStarting;
                core.NewWindowRequested -= Core_NewWindowRequested;
                core.PermissionRequested -= Core_PermissionRequested;
                core.DownloadStarting -= Core_DownloadStarting;
                core.ProcessFailed -= Core_ProcessFailed;
            }
        }

        internal void Clear()
        {
            bool hadSession = sessionPreviewer != null;
            generation++;
            sessionCancellation?.Cancel();
            sessionCancellation?.Dispose();
            sessionCancellation = null;
            sessionPreviewer = null;
            sessionPrefix = null;
            pageUri = null;
            format = null;
            navigationId = null;
            DisposeResponseStreams();

            if (isConfigured && !isDisposed && hadSession)
            {
                try
                {
                    ModelBrowser.CoreWebView2.Navigate("about:blank");
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    Logger.LogError("3D viewer cleanup failed: " + ex.GetType().Name);
                }
            }
        }

        private void OnPreviewerChanged(Model3DPreviewer? oldValue, Model3DPreviewer? newValue)
        {
            if (oldValue != null)
            {
                oldValue.PropertyChanged -= Previewer_PropertyChanged;
            }

            Clear();
            if (newValue != null && !isDisposed)
            {
                newValue.PropertyChanged += Previewer_PropertyChanged;
                StartPreview();
            }
        }

        private void Previewer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Model3DPreviewer.Preview))
            {
                StartPreview();
            }
        }

        private void ModelBrowser_Loaded(object sender, RoutedEventArgs e)
        {
            isLoaded = true;
            StartPreview();
        }

        private async void StartPreview()
        {
            var previewer = Previewer;
            if (isDisposed || !isLoaded || previewer?.Preview == null)
            {
                return;
            }

            Clear();
            int currentGeneration = generation;
            var cancellation = new CancellationTokenSource();
            sessionCancellation = cancellation;
            sessionPreviewer = previewer;
            format = Path.GetExtension(previewer.Preview).Equals(".3mf", StringComparison.OrdinalIgnoreCase) ? "3mf" : "stl";
            sessionPrefix = $"/{Guid.NewGuid():N}/";
            pageUri = $"https://{Host}{sessionPrefix}index.html?format={format}&maxExpanded={Model3DFile.MaxArchiveSize.ToString(CultureInfo.InvariantCulture)}";

            try
            {
                await ModelBrowser.EnsureCoreWebView2Async();
                if (!IsCurrent(currentGeneration, previewer))
                {
                    return;
                }

                if (!isConfigured)
                {
                    ConfigureBrowser();
                }

                ModelBrowser.CoreWebView2.Navigate(pageUri);
            }
            catch (Exception ex)
            {
                if (IsCurrent(currentGeneration, previewer))
                {
                    Logger.LogError("3D viewer initialization failed: " + ex.GetType().Name);
                    FailPreview();
                }
            }
        }

        private void ConfigureBrowser()
        {
            var core = ModelBrowser.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            ModelBrowser.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Core_WebResourceRequested;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationStarting += Core_NavigationStarting;
            core.FrameNavigationStarting += Core_FrameNavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.PermissionRequested += Core_PermissionRequested;
            core.DownloadStarting += Core_DownloadStarting;
            core.ProcessFailed += Core_ProcessFailed;
            isConfigured = true;
        }

        private async void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            using var deferral = args.GetDeferral();
            int currentGeneration = generation;
            var previewer = sessionPreviewer;
            var cancellationToken = sessionCancellation?.Token ?? CancellationToken.None;
            try
            {
                if (args.Request.Uri.StartsWith($"blob:https://{Host}/", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (previewer?.Preview == null || sessionPrefix == null || format == null ||
                    !string.Equals(args.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) ||
                    !uri.IsDefaultPort ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    !uri.AbsolutePath.StartsWith(sessionPrefix, StringComparison.Ordinal))
                {
                    args.Response = CreateEmptyResponse(sender, 403, "Forbidden");
                    return;
                }

                string path = uri.AbsolutePath[sessionPrefix.Length..];
                Stream? content;
                if (path == $"model.{format}")
                {
                    string modelPath = previewer.Preview;
                    bool is3Mf = format == "3mf";
                    content = await Task.Run(
                        async () =>
                        {
                            using var file = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            return await Model3DFile.ReadAsync(file, is3Mf, cancellationToken).ConfigureAwait(false);
                        },
                        cancellationToken);
                }
                else
                {
                    content = typeof(Model3DControl).Assembly.GetManifestResourceStream("Model3D\\" + path.Replace('/', '\\'));
                }

                if (!IsCurrent(currentGeneration, previewer))
                {
                    content?.Dispose();
                    args.Response = CreateEmptyResponse(sender, 403, "Forbidden");
                    return;
                }

                if (content == null)
                {
                    args.Response = CreateEmptyResponse(sender, 404, "Not Found");
                    Logger.LogError("An embedded 3D viewer resource was not found.");
                    FailPreview();
                    return;
                }

                var adapter = content.AsRandomAccessStream();
                responseStreams.Add((adapter, content));
                string contentType = Path.GetExtension(path) switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    _ => "application/octet-stream",
                };
                string headers = $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: {ContentSecurityPolicy}";
                args.Response = sender.Environment.CreateWebResourceResponse(adapter, 200, "OK", headers);
            }
            catch (OperationCanceledException)
            {
                args.Response = CreateEmptyResponse(sender, 403, "Forbidden");
            }
            catch (Exception ex)
            {
                args.Response = CreateEmptyResponse(sender, 500, "Internal Server Error");
                if (previewer != null && IsCurrent(currentGeneration, previewer))
                {
                    Logger.LogError("3D model resource failed: " + ex.GetType().Name);
                    FailPreview();
                }
            }
        }

        private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (sessionPreviewer == null || !string.Equals(args.Source, pageUri, StringComparison.Ordinal))
            {
                return;
            }

            switch (args.TryGetWebMessageAsString())
            {
                case "initialized":
                    sender.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        label = ResourceLoaderInstance.ResourceLoader.GetString("Model3DViewer_Name"),
                        help = ResourceLoaderInstance.ResourceLoader.GetString("Model3DViewer_Help"),
                    }));
                    break;
                case "loaded":
                    DisposeResponseStreams();
                    sessionPreviewer.State = PreviewState.Loaded;
                    break;
                case "error":
                    Logger.LogError("3D model could not be rendered.");
                    FailPreview();
                    break;
            }
        }

        private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (string.Equals(args.Uri, "about:blank", StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(args.Uri, pageUri, StringComparison.Ordinal))
            {
                args.Cancel = true;
                return;
            }

            navigationId = args.NavigationId;
        }

        private void ModelBrowser_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess && navigationId == args.NavigationId && sessionPreviewer != null)
            {
                Logger.LogError("3D viewer navigation failed: " + args.WebErrorStatus);
                FailPreview();
            }
        }

        private void Core_FrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;

        private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

        private void Core_PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;

        private void Core_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

        private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (sessionPreviewer != null)
            {
                Logger.LogError("3D viewer process failed: " + args.ProcessFailedKind);
                FailPreview();
            }
        }

        private bool IsCurrent(int currentGeneration, Model3DPreviewer previewer)
        {
            return !isDisposed && generation == currentGeneration && ReferenceEquals(sessionPreviewer, previewer);
        }

        private void FailPreview()
        {
            DisposeResponseStreams();
            if (sessionPreviewer != null)
            {
                sessionPreviewer.State = PreviewState.Error;
            }
        }

        private void DisposeResponseStreams()
        {
            foreach (var (adapter, source) in responseStreams)
            {
                adapter.Dispose();
                source.Dispose();
            }

            responseStreams.Clear();
        }

        private static CoreWebView2WebResourceResponse CreateEmptyResponse(CoreWebView2 sender, int statusCode, string reason)
        {
            return sender.Environment.CreateWebResourceResponse(null, statusCode, reason, "Cache-Control: no-store");
        }
    }
}
