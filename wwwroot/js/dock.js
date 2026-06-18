// Shows the floating bottom nav dock once the top nav has scrolled out of view.
// Plain DOM enhancement (works with the prerendered HTML); the nav element lives
// in the persistent layout, so a single observer survives Blazor SPA navigation.
(function () {
    function init() {
        var nav = document.querySelector('.rc-nav');
        if (!nav) return false;
        var io = new IntersectionObserver(function (entries) {
            var visible = entries[0].isIntersecting;
            document.body.classList.toggle('rc-show-dock', !visible);
        }, { threshold: 0 });
        io.observe(nav);
        return true;
    }
    if (!init()) {
        var tries = 0;
        var t = setInterval(function () {
            if (init() || ++tries > 40) clearInterval(t);
        }, 150);
    }
})();
