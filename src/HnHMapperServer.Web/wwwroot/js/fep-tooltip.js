// Shared styled tooltip for FEP bar segments and pills (cookbook).
// Any element with data-ft-stat gets a designed hover card instead of the native title.
// Pure DOM event delegation: survives Blazor re-renders, one floating element for the page.
(function () {
    let tip = null;

    function ensureTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'fep-tt';
            document.body.appendChild(tip);
        }
        return tip;
    }

    function hide() {
        if (tip) {
            tip.classList.remove('fep-tt-show');
        }
    }

    document.addEventListener('mouseover', function (e) {
        const el = e.target.closest ? e.target.closest('[data-ft-stat]') : null;
        if (!el) {
            hide();
            return;
        }

        const t = ensureTip();
        const d = el.dataset;
        const tier2 = d.ftTier === '2';

        t.innerHTML =
            '<div class="fep-tt-head">' +
                '<span class="fep-tt-dot" style="background:' + (d.ftColor || '#ccc') + '"></span>' +
                '<span class="fep-tt-name"></span>' +
                (tier2 ? '<span class="fep-tt-t2">+2</span>' : '') +
            '</div>' +
            '<div class="fep-tt-body"></div>';
        // Stat names/values come from our own catalog, but set them as text anyway.
        t.querySelector('.fep-tt-name').textContent = d.ftStat + (tier2 ? '' : ' +1');
        t.querySelector('.fep-tt-body').textContent = d.ftValue + ' FEP · ' + d.ftShare + ' of total';

        t.classList.add('fep-tt-show');

        const r = el.getBoundingClientRect();
        const tw = t.offsetWidth;
        const th = t.offsetHeight;
        let x = r.left + r.width / 2 - tw / 2;
        x = Math.max(8, Math.min(x, window.innerWidth - tw - 8));
        let y = r.top - th - 9;
        if (y < 8) {
            y = r.bottom + 9;
        }
        t.style.left = x + 'px';
        t.style.top = y + 'px';
    });

    document.addEventListener('mouseout', function (e) {
        if (!e.relatedTarget) {
            hide();
        }
    });

    document.addEventListener('scroll', hide, true);
})();
