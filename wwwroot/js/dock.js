// Shows the floating bottom nav dock once the top nav has scrolled out of view.
// Re-queries .rc-nav on every check (rather than observing a node once) because
// Blazor Server prerender->hydration and SPA navigation swap the nav DOM node;
// a one-shot IntersectionObserver would end up watching a detached element.
(function () {
    function update() {
        var nav = document.querySelector('.rc-nav');
        if (!nav) return;
        // Dock appears only once the top nav is fully above the viewport.
        var out = nav.getBoundingClientRect().bottom <= 4;
        document.body.classList.toggle('rc-show-dock', out);
    }
    window.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    document.addEventListener('DOMContentLoaded', update);
    // Safety net: corrects the state after hydration or SPA navigation swaps the
    // nav node (those don't fire a scroll event). Cheap — one getBoundingClientRect.
    setInterval(update, 500);
    update();
})();
