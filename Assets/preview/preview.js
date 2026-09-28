// Preview page behavior: Mermaid/highlight.js/KaTeX rendering, diagram
// fullscreen viewer, context menu, and link routing to the host app.
// Settings come from data attributes the host puts on <html>.
(function () {
    'use strict';

    const root = document.documentElement;

    // The host only accepts app:// commands carrying this page's CSP nonce, which
    // markup injected through markdown cannot read
    const hostToken = (document.currentScript && document.currentScript.nonce) || '';

    let currentFullscreenEl = null;
    let currentZoom = 1;
    let panX = 0, panY = 0;
    let isDragging = false;
    let dragStartX = 0, dragStartY = 0;
    let panStartX = 0, panStartY = 0;

    // Bumped whenever the content is replaced so in-flight diagram rendering stops
    let renderGeneration = 0;

    // Mermaid source -> rendered SVG markup, so incremental updates only
    // re-render diagrams whose source actually changed
    let diagramCache = new Map();

    function escapeHtml(value) {
        const div = document.createElement('div');
        div.textContent = value || '';
        return div.innerHTML;
    }

    function sendToHost(command, params) {
        const query = new URLSearchParams(Object.assign({ token: hostToken }, params)).toString();
        const url = 'app://' + command + '?' + query;
        // The WebView's message channel works everywhere; navigating to app:// is the
        // fallback, and WebKitGTK shows an error page for it
        if (typeof window.invokeCSharpAction === 'function') {
            window.invokeCSharpAction(url);
        } else {
            window.location.href = url;
        }
    }

    function reuseCachedDiagrams() {
        const previous = diagramCache;
        diagramCache = new Map();
        document.querySelectorAll('#content .mermaid').forEach((el) => {
            const source = el.textContent;
            el.dataset.source = source;
            const svg = previous.get(source);
            if (svg !== undefined && !diagramCache.has(source)) {
                el.innerHTML = svg;
                el.setAttribute('data-processed', 'true');
                diagramCache.set(source, svg);
            }
        });
    }

    async function renderMermaidDiagrams(generation) {
        const diagrams = Array.from(document.querySelectorAll('.mermaid:not(.mermaid-error):not([data-processed])'))
            .filter(el => !el.querySelector('svg'));

        for (const el of diagrams) {
            if (generation !== renderGeneration) return;
            const source = el.dataset.source !== undefined ? el.dataset.source : el.textContent;
            try {
                await mermaid.run({ nodes: [el] });
                // Content was replaced while rendering; this element is gone
                if (generation !== renderGeneration) return;
                diagramCache.set(source, el.innerHTML);
            } catch (e) {
                if (generation !== renderGeneration) return;
                const message = (e && (e.message || e.str || e.toString())) || 'Unknown Mermaid error';
                console.error('Mermaid render error:', e);
                el.classList.add('mermaid-error');
                el.innerHTML = '<strong>Mermaid render error</strong><pre>' + escapeHtml(message) + '</pre>';
            }

            await new Promise(resolve => setTimeout(resolve, 0));
        }
    }

    function attachMermaidFullscreenHandlers() {
        document.querySelectorAll('.mermaid:not(.mermaid-error)').forEach((el) => {
            if (el.dataset.fullscreenReady === 'true') return;
            el.dataset.fullscreenReady = 'true';
            el.addEventListener('click', () => {
                if (!el.classList.contains('fullscreen')) {
                    openFullscreen(el);
                }
            });
        });
    }

    function renderMath(selector, displayMode) {
        document.querySelectorAll(selector).forEach((el) => {
            try {
                const math = el.getAttribute('data-math');
                if (math) {
                    const decoded = new DOMParser().parseFromString(math, 'text/html').body.textContent;
                    katex.render(decoded, el, { displayMode: displayMode, throwOnError: false });
                }
            } catch (e) { console.error('Math render error:', e); }
        });
    }

    function renderContent() {
        const generation = ++renderGeneration;

        reuseCachedDiagrams();
        renderMermaidDiagrams(generation).then(attachMermaidFullscreenHandlers);

        document.querySelectorAll('pre code').forEach((block) => {
            if (!block.closest('.mermaid')) hljs.highlightElement(block);
        });

        // Render preprocessed math elements
        if (typeof katex !== 'undefined') {
            renderMath('.math-display', true);
            renderMath('.math-inline', false);
        }
    }

    function initialize() {
        mermaid.initialize({
            startOnLoad: false,
            theme: root.dataset.mermaidTheme || 'default',
            // strict: diagram labels are escaped and click directives are ignored
            securityLevel: 'strict'
        });

        renderContent();

        // Tell the host which render this page belongs to, so it only pushes
        // incremental updates into a page that has finished loading
        const version = new URLSearchParams(window.location.search).get('v');
        if (version) sendToHost('ready', { v: version });
    }

    // ---- Diagram fullscreen viewer ----

    function openFullscreen(mermaidEl) {
        currentFullscreenEl = mermaidEl;
        currentZoom = 1;
        panX = 0;
        panY = 0;

        document.getElementById('fullscreenOverlay').classList.add('active');
        document.getElementById('fullscreenControls').classList.add('active');

        // Make the diagram fullscreen (no cloning - original element!)
        mermaidEl.classList.add('fullscreen');

        const svg = mermaidEl.querySelector('svg');
        if (svg) {
            svg.style.transform = 'scale(1) translate(0px, 0px)';
            svg.style.cursor = 'grab';
        }
    }

    function closeFullscreen() {
        if (!currentFullscreenEl) return;

        document.getElementById('fullscreenOverlay').classList.remove('active');
        document.getElementById('fullscreenControls').classList.remove('active');

        const svg = currentFullscreenEl.querySelector('svg');
        if (svg) {
            svg.style.transform = '';
            svg.style.cursor = '';
        }

        currentFullscreenEl.classList.remove('fullscreen');
        currentFullscreenEl = null;
        currentZoom = 1;
        panX = 0;
        panY = 0;
    }

    function zoomBy(factor) {
        if (!currentFullscreenEl) return;
        currentZoom = Math.max(0.1, Math.min(currentZoom * factor, 20));
        applyTransform();
    }

    function resetZoom() {
        if (!currentFullscreenEl) return;
        currentZoom = 1;
        panX = 0;
        panY = 0;
        applyTransform();
    }

    function fitToPage() {
        if (!currentFullscreenEl) return;
        const svg = currentFullscreenEl.querySelector('svg');
        if (!svg) return;

        // Container dimensions (viewport minus controls bar)
        const containerWidth = window.innerWidth - 40;
        const containerHeight = window.innerHeight - 100;

        // SVG natural dimensions
        const svgRect = svg.getBoundingClientRect();
        const svgWidth = svgRect.width / currentZoom;
        const svgHeight = svgRect.height / currentZoom;

        const scaleX = containerWidth / svgWidth;
        const scaleY = containerHeight / svgHeight;
        currentZoom = Math.min(scaleX, scaleY, 1) * 0.9; // 90% to add padding

        panX = 0;
        panY = 0;
        applyTransform();
    }

    function applyTransform() {
        if (!currentFullscreenEl) return;
        const svg = currentFullscreenEl.querySelector('svg');
        if (svg) {
            svg.style.transform = `translate(${panX}px, ${panY}px) scale(${currentZoom})`;
        }
    }

    const fullscreenActions = {
        'zoom-in': () => zoomBy(1.25),
        'zoom-out': () => zoomBy(0.8),
        'reset': resetZoom,
        'fit': fitToPage,
        'close': closeFullscreen
    };

    document.querySelectorAll('#fullscreenControls [data-action]').forEach((button) => {
        button.addEventListener('click', () => fullscreenActions[button.dataset.action]());
    });

    // Mouse wheel zoom in fullscreen
    document.addEventListener('wheel', (e) => {
        if (!currentFullscreenEl) return;
        e.preventDefault();
        zoomBy(e.deltaY < 0 ? 1.1 : 0.9);
    }, { passive: false });

    // Mouse drag for panning
    document.addEventListener('mousedown', (e) => {
        if (!currentFullscreenEl) return;
        const svg = currentFullscreenEl.querySelector('svg');
        if (svg && svg.contains(e.target)) {
            isDragging = true;
            dragStartX = e.clientX;
            dragStartY = e.clientY;
            panStartX = panX;
            panStartY = panY;
            svg.style.cursor = 'grabbing';
            e.preventDefault();
        }
    });

    document.addEventListener('mousemove', (e) => {
        if (!isDragging || !currentFullscreenEl) return;
        panX = panStartX + (e.clientX - dragStartX);
        panY = panStartY + (e.clientY - dragStartY);
        applyTransform();
    });

    document.addEventListener('mouseup', () => {
        if (isDragging && currentFullscreenEl) {
            const svg = currentFullscreenEl.querySelector('svg');
            if (svg) svg.style.cursor = 'grab';
        }
        isDragging = false;
    });

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') closeFullscreen();
    });

    // ---- Custom right-click context menu ----

    (function () {
        const menu = document.getElementById('ctxMenu');
        const ctxEdit = document.getElementById('ctxEdit');
        const ctxCopy = document.getElementById('ctxCopy');
        const ctxSelectAll = document.getElementById('ctxSelectAll');

        function hideMenu() { menu.classList.remove('active'); }

        document.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            const sel = window.getSelection();
            const hasSelection = sel && sel.toString().length > 0;
            ctxCopy.style.opacity = hasSelection ? '1' : '0.4';
            ctxCopy.style.pointerEvents = hasSelection ? 'auto' : 'none';

            menu.style.left = Math.min(e.clientX, window.innerWidth - 180) + 'px';
            menu.style.top = Math.min(e.clientY, window.innerHeight - 80) + 'px';
            menu.classList.add('active');
        });

        document.addEventListener('click', hideMenu);
        document.addEventListener('scroll', hideMenu);
        window.addEventListener('blur', hideMenu);

        ctxEdit.addEventListener('click', () => {
            hideMenu();
            sendToHost('toggle-edit');
        });

        ctxCopy.addEventListener('click', () => {
            const sel = window.getSelection();
            if (sel && sel.toString().length > 0) {
                navigator.clipboard.writeText(sel.toString());
            }
            hideMenu();
        });

        ctxSelectAll.addEventListener('click', () => {
            const range = document.createRange();
            range.selectNodeContents(document.getElementById('content'));
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            hideMenu();
        });
    })();

    // ---- Links: route through the host so the preview never navigates away ----

    function handleLinkClick(e) {
        const a = e.target.closest && e.target.closest('a[href]');
        if (!a || (e.type === 'auxclick' && e.button !== 1)) return;
        const raw = a.getAttribute('href') || '';
        e.preventDefault();
        if (raw.startsWith('#')) {
            const id = decodeURIComponent(raw.slice(1));
            const target = document.getElementById(id) || document.getElementsByName(id)[0];
            if (target) target.scrollIntoView();
            return;
        }
        if (/^(https?|mailto|file):/i.test(a.href)) {
            sendToHost('open-link', { url: a.href });
        }
    }
    document.addEventListener('click', handleLinkClick, true);
    document.addEventListener('auxclick', handleLinkClick, true);

    // Host API, called via ExecuteScriptAsync
    window.mdviewer = {
        // Replace the rendered markdown without reloading the page, keeping
        // scroll position and unchanged diagrams
        setContent(html) {
            closeFullscreen();
            document.getElementById('content').innerHTML = html;
            renderContent();
            return true;
        },
        setEditMode(isEditMode) {
            document.getElementById('ctxEdit').childNodes[0].textContent = isEditMode ? 'Exit Edit Mode' : 'Edit';
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initialize);
    } else {
        initialize();
    }
})();
