// Preview page behavior: Mermaid/highlight.js/KaTeX rendering, diagram
// fullscreen viewer, context menu, and link routing to the host app.
// Settings come from data attributes the host puts on <html>.
(function () {
    'use strict';

    const root = document.documentElement;

    let currentFullscreenEl = null;
    let currentZoom = 1;
    let panX = 0, panY = 0;
    let isDragging = false;
    let dragStartX = 0, dragStartY = 0;
    let panStartX = 0, panStartY = 0;
    let contentRendered = false;

    function escapeHtml(value) {
        const div = document.createElement('div');
        div.textContent = value || '';
        return div.innerHTML;
    }

    function sendToHost(command, params) {
        const query = params ? '?' + new URLSearchParams(params).toString() : '';
        window.location.href = 'app://' + command + query;
    }

    async function renderMermaidDiagrams() {
        const diagrams = Array.from(document.querySelectorAll('.mermaid:not(.mermaid-error):not([data-processed])'))
            .filter(el => !el.querySelector('svg'));

        for (const el of diagrams) {
            try {
                await mermaid.run({ nodes: [el] });
            } catch (e) {
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
        if (contentRendered) return;
        contentRendered = true;

        mermaid.initialize({
            startOnLoad: false,
            theme: root.dataset.mermaidTheme || 'default',
            securityLevel: 'loose'
        });

        renderMermaidDiagrams().then(attachMermaidFullscreenHandlers);

        document.querySelectorAll('pre code').forEach((block) => {
            if (!block.closest('.mermaid')) hljs.highlightElement(block);
        });

        // Render preprocessed math elements
        if (typeof katex !== 'undefined') {
            renderMath('.math-display', true);
            renderMath('.math-inline', false);
        }
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

    // The host updates the context menu label after edit mode toggles
    window.mdviewer = {
        setEditMode(isEditMode) {
            document.getElementById('ctxEdit').childNodes[0].textContent = isEditMode ? 'Exit Edit Mode' : 'Edit';
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', renderContent);
    } else {
        renderContent();
    }
})();
