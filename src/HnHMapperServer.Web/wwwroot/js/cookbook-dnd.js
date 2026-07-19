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
    var stuckScheduled = false;

    function updateStuck() {
        stuckScheduled = false;
        var stack = document.querySelector('.cookbook-page .sticky-stack');
        if (!stack) {
            return;
        }
        // position: sticky; top: 72px — pinned exactly when the rect reaches it
        stack.classList.toggle('ck-stuck', stack.getBoundingClientRect().top <= 76);
    }

    function scheduleStuck() {
        if (!stuckScheduled) {
            stuckScheduled = true;
            requestAnimationFrame(updateStuck);
        }
    }

    window.addEventListener('scroll', scheduleStuck, { passive: true });
    window.addEventListener('resize', scheduleStuck);
    new MutationObserver(scheduleStuck).observe(document.body, { childList: true, subtree: true });
    scheduleStuck();
})();
