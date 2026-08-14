// HTML5 drag & drop support for the cookbook panels.
//
// Two things Blazor's synthetic (async, delegated) events cannot do reliably:
//  1. dragstart: Firefox refuses to start a drag unless dataTransfer.setData is
//     called synchronously — C# handlers can't touch dataTransfer.
//  2. dragover: a drop target must call preventDefault() synchronously on every
//     dragover, or the browser shows the not-allowed cursor and never fires drop.
// This delegated shim owns both natively; Blazor's @ondrop handlers still run
// because preventDefault does not stop propagation.
(function () {
    function dropTarget(e) {
        return e.target && e.target.closest ? e.target.closest('.panel-card.own') : null;
    }

    document.addEventListener('dragstart', function (e) {
        if (e.target && e.target.closest && e.target.closest('[draggable="true"]') && e.dataTransfer) {
            e.dataTransfer.setData('text/plain', 'cookbook');
            e.dataTransfer.effectAllowed = 'copyMove';
            document.body.classList.add('ck-dragging');
        }
    });

    document.addEventListener('dragend', function () {
        document.body.classList.remove('ck-dragging');
    });

    document.addEventListener('dragover', function (e) {
        if (dropTarget(e)) {
            e.preventDefault();
            if (e.dataTransfer) {
                e.dataTransfer.dropEffect = 'copy';
            }
        }
    });

    document.addEventListener('drop', function (e) {
        document.body.classList.remove('ck-dragging');
        if (dropTarget(e)) {
            e.preventDefault();
        }
    });

    // Condense the sticky panels strip once it is actually pinned over the
    // table: CSS collapses each card to just its header while .ck-stuck is on.
    // rAF-throttled; the MutationObserver re-applies the class after Blazor
    // re-renders replace the element (which would otherwise drop it).
    //
    // Crucial: condensing must NOT change the document height. On short pages
    // (heavy filters → few rows) the height loss used to clamp the scroll
    // position back up, which un-pinned the strip, which grew the page again —
    // an oscillation that made scrolling down impossible. The condensed height
    // difference is therefore given back as margin-bottom on the stack.
    var stuckScheduled = false;
    var needsMeasure = true;

    function updateStuck() {
        stuckScheduled = false;
        var stack = document.querySelector('.cookbook-page .sticky-stack');
        if (!stack) {
            return;
        }
        // position: sticky; top: 72px — pinned exactly when the rect reaches it
        var shouldStick = stack.getBoundingClientRect().top <= 76;
        var wasStuck = stack.classList.contains('ck-stuck');

        if (shouldStick) {
            if (!wasStuck || needsMeasure) {
                stack.classList.remove('ck-stuck');
                var expanded = stack.offsetHeight;
                stack.classList.add('ck-stuck');
                stack.style.marginBottom = Math.max(0, expanded - stack.offsetHeight) + 'px';
                needsMeasure = false;
            }
        } else if (wasStuck || stack.style.marginBottom) {
            stack.classList.remove('ck-stuck');
            stack.style.marginBottom = '';
        }
    }

    function scheduleStuck() {
        if (!stuckScheduled) {
            stuckScheduled = true;
            requestAnimationFrame(updateStuck);
        }
    }

    function scheduleRemeasure() {
        needsMeasure = true;
        scheduleStuck();
    }

    window.addEventListener('scroll', scheduleStuck, { passive: true });
    window.addEventListener('resize', scheduleRemeasure);
    new MutationObserver(scheduleRemeasure).observe(document.body, { childList: true, subtree: true });
    scheduleStuck();
})();

// Scroll the first notification-highlighted row into view (called by Cookbook.razor
// after applying a ?highlight= deep link). rAF waits out the Blazor render; 'center'
// keeps the row clear of the sticky toolbar stack.
window.cookbookHighlight = {
    reveal: function () {
        requestAnimationFrame(function () {
            var row = document.querySelector('.cookbook-page .ck-new-flash');
            if (row && row.scrollIntoView) {
                row.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        });
    }
};
