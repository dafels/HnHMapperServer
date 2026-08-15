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
})();

// Scroll the first notification-highlighted row into view (called by Cookbook.razor
// after applying a ?highlight= deep link). rAF waits out the Blazor render.
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
