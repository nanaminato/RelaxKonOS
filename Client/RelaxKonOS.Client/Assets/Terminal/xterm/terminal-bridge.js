/*
 * RelaxKonOS xterm.js terminal page.
 *
 * This page is a renderer only: the C# surface owns the transport (a server PTY over SignalR, an SSH
 * channel or a local PTY) and pushes the session's raw bytes here. Everything the user does that
 * touches the host — input, resize, clipboard, window title — travels back as a JSON message.
 *
 * Messages to the host:  { type: "ready" | "input" | "resize" | "title" | "selection" | "bell" |
 *                          "copy" | "paste" | "exit" }
 * Messages from the host: window.rkTerminal.* (see below)
 */
(function () {
    'use strict';

    var textEncoder = new TextEncoder();

    /// Posts one JSON message to the host. WebView2 exposes window.chrome.webview; the iframe-hosted
    /// Avalonia backends shim the same name onto window.parent.postMessage.
    function post(message) {
        var text = JSON.stringify(message);
        var webview = window.chrome && window.chrome.webview;
        if (webview && typeof webview.postMessage === 'function') {
            webview.postMessage(text);
            return;
        }
        if (window.parent && window.parent !== window) {
            window.parent.postMessage(text, '*');
        }
    }

    function decodeBase64(base64) {
        var binary = atob(base64);
        var bytes = new Uint8Array(binary.length);
        for (var index = 0; index < binary.length; index++)
            bytes[index] = binary.charCodeAt(index);
        return bytes;
    }

    function encodeBase64(bytes) {
        var text = '';
        for (var index = 0; index < bytes.length; index++)
            text += String.fromCharCode(bytes[index]);
        return btoa(text);
    }

    var options = window.__relaxkonosTerminal || {};
    var labels = options.labels || {};

    var terminal = new Terminal({
        allowProposedApi: true,
        convertEol: false,
        cursorBlink: true,
        fontFamily: options.fontFamily || 'monospace',
        fontSize: options.fontSize || 14,
        scrollback: options.scrollback || 10000,
        theme: options.theme || undefined
    });

    var fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(document.getElementById('terminal'));

    var menu = document.getElementById('menu');

    function fitNow() {
        try {
            fitAddon.fit();
        } catch (error) {
            // The container can still be unmeasurable during the first layout pass.
        }
    }

    function pixelSize() {
        var ratio = window.devicePixelRatio || 1;
        return {
            width: Math.round(document.documentElement.clientWidth * ratio),
            height: Math.round(document.documentElement.clientHeight * ratio)
        };
    }

    // ----- host -> page -------------------------------------------------------

    window.rkTerminal = {
        write: function (base64) { terminal.write(decodeBase64(base64)); },
        setOptions: function (next) {
            next = next || {};
            if (next.fontFamily) terminal.options.fontFamily = next.fontFamily;
            if (next.fontSize) terminal.options.fontSize = next.fontSize;
            if (next.theme) terminal.options.theme = next.theme;
            if (next.scrollback) terminal.options.scrollback = next.scrollback;
            if (next.labels) { labels = next.labels; renderMenu(); }
            fitNow();
        },
        selectAll: function () { terminal.selectAll(); },
        clear: function () { terminal.clear(); },
        reset: function () { terminal.reset(); },
        focus: function () { terminal.focus(); },
        // The host owns the system clipboard. Routing a paste through terminal.paste() keeps
        // line-ending normalisation and bracketed paste identical to a keystroke-typed paste.
        paste: function (base64) { terminal.paste(new TextDecoder().decode(decodeBase64(base64))); },
        getSelection: function () { return terminal.getSelection(); },
        dispose: function () { terminal.dispose(); }
    };

    // ----- page -> host -------------------------------------------------------

    terminal.onData(function (data) { post({ type: 'input', data: encodeBase64(textEncoder.encode(data)) }); });
    terminal.onTitleChange(function (title) { post({ type: 'title', title: title }); });
    terminal.onBell(function () { post({ type: 'bell' }); });
    terminal.onSelectionChange(function () {
        post({ type: 'selection', text: terminal.getSelection(), hasSelection: terminal.hasSelection() });
    });
    terminal.onResize(function (size) {
        var pixels = pixelSize();
        post({ type: 'resize', columns: size.cols, rows: size.rows, width: pixels.width, height: pixels.height });
    });

    // Copy and paste are host operations: the page cannot read or write the system clipboard from a
    // native WebView surface, so the shortcuts only forward the intent.
    terminal.attachCustomKeyEventHandler(function (event) {
        if (event.type !== 'keydown') return true;
        var accelerator = (event.ctrlKey || event.metaKey);
        if (!accelerator) return true;
        var key = (event.key || '').toLowerCase();
        if (!event.shiftKey && !event.metaKey) return true;
        if (key === 'c') { if (terminal.hasSelection()) { post({ type: 'copy' }); return false; } return true; }
        if (key === 'v') { post({ type: 'paste' }); return false; }
        return true;
    });

    var menuItems = [
        { id: 'copy', enabled: function () { return terminal.hasSelection(); }, run: function () { post({ type: 'copy' }); } },
        { id: 'paste', run: function () { post({ type: 'paste' }); } },
        { id: 'selectAll', run: function () { terminal.selectAll(); } },
        { id: 'clear', separatorBefore: true, run: function () { terminal.clear(); } }
    ];

    function renderMenu() {
        while (menu.firstChild) menu.removeChild(menu.firstChild);
        menuItems.forEach(function (item) {
            if (item.separatorBefore) {
                var separator = document.createElement('div');
                separator.className = 'rk-menu-separator';
                menu.appendChild(separator);
            }
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'rk-menu-item';
            button.textContent = labels[item.id] || item.id;
            if (item.enabled) button.disabled = !item.enabled();
            button.addEventListener('mousedown', function (event) { event.preventDefault(); event.stopPropagation(); });
            button.addEventListener('click', function () { hideMenu(); item.run(); });
            menu.appendChild(button);
        });
    }

    function showMenu(x, y) {
        renderMenu();
        menu.style.display = 'block';
        menu.style.left = Math.min(x, Math.max(0, document.documentElement.clientWidth - menu.offsetWidth - 4)) + 'px';
        menu.style.top = Math.min(y, Math.max(0, document.documentElement.clientHeight - menu.offsetHeight - 4)) + 'px';
    }

    function hideMenu() { menu.style.display = 'none'; }

    document.addEventListener('contextmenu', function (event) {
        event.preventDefault();
        showMenu(event.clientX, event.clientY);
    });
    document.addEventListener('mousedown', function (event) {
        if (!menu.contains(event.target)) hideMenu();
    });
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') hideMenu();
    });

    if (window.ResizeObserver) {
        new ResizeObserver(fitNow).observe(document.getElementById('terminal'));
    }
    window.addEventListener('resize', fitNow);
    window.addEventListener('focus', function () { terminal.focus(); });

    fitNow();
    terminal.focus();
    post({ type: 'ready' });
})();
