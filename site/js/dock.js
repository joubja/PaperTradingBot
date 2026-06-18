// Shows the floating bottom nav dock once the top nav scrolls out of view, and
// stamps the footer year. Shared across all pages.
(function () {
    function update() {
        var nav = document.querySelector('.rc-nav');
        if (!nav) return;
        document.body.classList.toggle('rc-show-dock', nav.getBoundingClientRect().bottom <= 4);
    }
    window.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    document.addEventListener('DOMContentLoaded', update);
    setInterval(update, 500);
    update();

    var y = document.getElementById('rc-year');
    if (y) y.textContent = new Date().getFullYear();
})();
