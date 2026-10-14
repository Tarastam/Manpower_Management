// Sidebar behaviour: collapse to icons (remembered per browser), expandable groups, phone drawer.
(function () {
    var root = document.documentElement;
    var sidebar = document.getElementById("sidebar");
    if (!sidebar) return;

    var collapseBtn = document.getElementById("sbCollapse");
    var phone = window.matchMedia("(max-width: 767.98px)");

    function syncCollapseLabel() {
        if (!collapseBtn) return;
        var collapsed = root.classList.contains("sb-collapsed");
        collapseBtn.title = collapsed ? "Expand menu" : "Collapse menu";
        collapseBtn.setAttribute("aria-label", collapseBtn.title);
    }

    if (collapseBtn) {
        collapseBtn.addEventListener("click", function () {
            var collapsed = root.classList.toggle("sb-collapsed");
            try { localStorage.setItem("sidebarCollapsed", collapsed ? "1" : "0"); } catch (e) { }
            sidebar.querySelectorAll(".sb-group.flyout").forEach(function (g) { g.classList.remove("flyout"); });
            syncCollapseLabel();
        });
    }
    syncCollapseLabel();

    // Groups (Report / Management): open in place when expanded, flyout when collapsed.
    sidebar.querySelectorAll(".sb-toggle").forEach(function (btn) {
        btn.addEventListener("click", function (e) {
            e.stopPropagation();
            var group = btn.closest(".sb-group");
            var iconOnly = root.classList.contains("sb-collapsed") && !phone.matches;
            if (iconOnly) {
                var show = !group.classList.contains("flyout");
                sidebar.querySelectorAll(".sb-group.flyout").forEach(function (g) { g.classList.remove("flyout"); });
                group.classList.toggle("flyout", show);
            } else {
                var open = group.classList.toggle("open");
                btn.setAttribute("aria-expanded", open ? "true" : "false");
            }
        });
    });
    // A click-opened flyout closes as soon as the mouse leaves it (hover alone never needs this).
    sidebar.querySelectorAll(".sb-group").forEach(function (g) {
        g.addEventListener("mouseleave", function (e) {
            if (e.pointerType === "touch") return;
            g.classList.remove("flyout");
            var active = document.activeElement;
            if (active && g.contains(active) && !active.matches(":focus-visible")) active.blur();
        });
    });
    document.addEventListener("click", function (e) {
        if (!e.target.closest(".sb-group")) {
            sidebar.querySelectorAll(".sb-group.flyout").forEach(function (g) { g.classList.remove("flyout"); });
        }
    });

    // Phone drawer.
    function closeDrawer() { root.classList.remove("sb-open"); }
    var burger = document.getElementById("sbBurger");
    if (burger) burger.addEventListener("click", function () { root.classList.toggle("sb-open"); });
    var backdrop = document.getElementById("sbBackdrop");
    if (backdrop) backdrop.addEventListener("click", closeDrawer);
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") closeDrawer(); });
    phone.addEventListener("change", closeDrawer);
})();
