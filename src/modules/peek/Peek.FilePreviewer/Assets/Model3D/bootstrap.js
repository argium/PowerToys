// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

(() => {
    const webview = window.chrome?.webview;
    const canvas = document.getElementById('model');

    window.addEventListener('error', () => webview?.postMessage('error'), true);
    window.addEventListener('unhandledrejection', () => webview?.postMessage('error'));
    webview?.addEventListener('message', ({ data }) => {
        canvas.setAttribute('aria-label', data.label);
        canvas.setAttribute('title', data.help);
    });
    webview?.postMessage('initialized');
})();
