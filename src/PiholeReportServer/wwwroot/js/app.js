/* ============================================================================
   Pi-hole Report Server — shared client behaviour.

   Two independent pieces, both deliberately dependency-free so the page keeps
   working with no CDN and no build step:

     1. A busy overlay that blocks interaction while a query runs.
     2. Client-side search and paging over an already-rendered result table.

   Search and paging are client-side on purpose. The rows are already
   materialised and row-capped by the server, so filtering in the browser is
   instant and costs nothing; paging server-side would mean re-running an
   expensive aggregate query for every page turn.
   ========================================================================= */
(function () {
    "use strict";

    /* -- 1. Busy overlay --------------------------------------------------- */

    var overlay = null;
    var busyTimer = null;

    function ensureOverlay() {
        if (overlay) {
            return overlay;
        }
        overlay = document.createElement("div");
        overlay.className = "busy-overlay";
        overlay.setAttribute("role", "status");
        overlay.setAttribute("aria-live", "polite");
        overlay.hidden = true;
        overlay.innerHTML =
            '<div class="busy-box">' +
            '<div class="busy-spinner" aria-hidden="true"></div>' +
            '<div class="busy-text">Running query…</div>' +
            '<div class="busy-hint">Large ranges over the full history can take a while.</div>' +
            "</div>";
        document.body.appendChild(overlay);
        return overlay;
    }

    function showBusy(message, autoHideMs) {
        var el = ensureOverlay();
        el.querySelector(".busy-text").textContent = message || "Running query…";
        el.hidden = false;
        document.documentElement.classList.add("is-busy");

        // Stop a second submit landing while the first is in flight. Deferred by a
        // tick: disabling the submitter synchronously inside the submit event can
        // drop its name/value from the payload in some browsers.
        window.setTimeout(function () {
            document.querySelectorAll("form button[type=submit]").forEach(function (b) {
                b.disabled = true;
            });
        }, 0);

        window.clearTimeout(busyTimer);
        if (autoHideMs) {
            // A file download never navigates the page, so nothing would otherwise
            // clear the overlay — it has to time itself out.
            busyTimer = window.setTimeout(hideBusy, autoHideMs);
        }
    }

    function hideBusy() {
        window.clearTimeout(busyTimer);
        if (overlay) {
            overlay.hidden = true;
        }
        document.documentElement.classList.remove("is-busy");
        document.querySelectorAll("form button[type=submit]").forEach(function (b) {
            b.disabled = false;
        });
    }

    function wireBusy() {
        document.addEventListener("submit", function (e) {
            var form = e.target;
            if (!(form instanceof HTMLFormElement) || form.hasAttribute("data-no-busy")) {
                return;
            }

            // An inline onsubmit="return confirm(...)" that the user cancelled has
            // already prevented the default. Showing the overlay then would freeze
            // the page with nothing in flight to clear it.
            if (e.defaultPrevented) {
                return;
            }

            // Which button submitted? Export produces a download rather than a
            // navigation, so it needs the self-clearing variant.
            var submitter = e.submitter;
            var handler = submitter && submitter.getAttribute("asp-page-handler");
            var isExport =
                (submitter && /export/i.test(submitter.getAttribute("formaction") || "")) ||
                (handler && /export/i.test(handler)) ||
                (submitter && /export/i.test(submitter.textContent || ""));
            var isDelete = submitter && /delete/i.test(submitter.textContent || "");

            if (isDelete) {
                showBusy("Deleting…", 15000);
            } else if (isExport) {
                showBusy("Preparing CSV…", 20000);
            } else {
                showBusy("Running query…");
            }
        });

        // Following a link to a saved report also runs a query.
        document.addEventListener("click", function (e) {
            var a = e.target.closest ? e.target.closest("a[href]") : null;
            if (!a || a.hasAttribute("data-no-busy")) {
                return;
            }
            var href = a.getAttribute("href") || "";
            if (href.indexOf("savedId=") !== -1 || a.hasAttribute("data-busy")) {
                showBusy("Loading report…");
            }
        });

        // Restore a usable page when the browser returns via back/forward cache,
        // otherwise the user lands on a frozen spinner.
        window.addEventListener("pageshow", hideBusy);
        window.addEventListener("pagehide", hideBusy);
    }

    /* -- 2. Result search + paging ----------------------------------------- */

    function initResultTable(root) {
        var table = root.querySelector("table.data");
        if (!table) {
            return;
        }
        var tbody = table.tBodies[0];
        if (!tbody) {
            return;
        }

        var rows = Array.prototype.slice.call(tbody.rows);
        if (rows.length === 0) {
            return;
        }

        // Cache each row's searchable text once. Recomputing textContent on every
        // keystroke is what makes naive table filters crawl on a few thousand rows.
        var haystacks = rows.map(function (r) {
            return (r.textContent || "").toLowerCase();
        });

        var searchInput = root.querySelector("[data-result-search]");
        var pageSizeSelect = root.querySelector("[data-result-pagesize]");
        var prevBtn = root.querySelector("[data-result-prev]");
        var nextBtn = root.querySelector("[data-result-next]");
        var status = root.querySelector("[data-result-status]");
        var empty = root.querySelector("[data-result-empty]");

        var matching = rows.slice();
        var page = 0;

        function pageSize() {
            var v = pageSizeSelect ? pageSizeSelect.value : "100";
            return v === "all" ? Number.MAX_SAFE_INTEGER : parseInt(v, 10) || 100;
        }

        function render() {
            var size = pageSize();
            var pages = Math.max(1, Math.ceil(matching.length / size));
            if (page >= pages) {
                page = pages - 1;
            }
            if (page < 0) {
                page = 0;
            }

            var start = page * size;
            var end = Math.min(start + size, matching.length);

            // Hide everything, then reveal just this page's slice.
            for (var i = 0; i < rows.length; i++) {
                rows[i].hidden = true;
            }
            for (var j = start; j < end; j++) {
                matching[j].hidden = false;
            }

            if (status) {
                if (matching.length === 0) {
                    status.textContent = "No rows match";
                } else {
                    var shown = "Showing " + (start + 1).toLocaleString() + "–" +
                        end.toLocaleString() + " of " + matching.length.toLocaleString();
                    status.textContent = matching.length !== rows.length
                        ? shown + " (filtered from " + rows.length.toLocaleString() + ")"
                        : shown;
                }
            }
            if (empty) {
                empty.hidden = matching.length !== 0;
            }
            if (prevBtn) {
                prevBtn.disabled = page === 0 || matching.length === 0;
            }
            if (nextBtn) {
                nextBtn.disabled = page >= pages - 1 || matching.length === 0;
            }
        }

        function applyFilter() {
            var term = (searchInput ? searchInput.value : "").trim().toLowerCase();
            if (term === "") {
                matching = rows.slice();
            } else {
                // Space-separated terms must all be present, which makes
                // "samsung block" a useful narrowing rather than a literal.
                var terms = term.split(/\s+/);
                matching = rows.filter(function (_, i) {
                    var hay = haystacks[i];
                    for (var t = 0; t < terms.length; t++) {
                        if (hay.indexOf(terms[t]) === -1) {
                            return false;
                        }
                    }
                    return true;
                });
            }
            page = 0;
            render();
        }

        if (searchInput) {
            var debounce = null;
            searchInput.addEventListener("input", function () {
                window.clearTimeout(debounce);
                debounce = window.setTimeout(applyFilter, 120);
            });
            searchInput.addEventListener("keydown", function (e) {
                if (e.key === "Escape") {
                    searchInput.value = "";
                    applyFilter();
                }
            });
        }
        if (pageSizeSelect) {
            pageSizeSelect.addEventListener("change", function () {
                page = 0;
                render();
            });
        }
        if (prevBtn) {
            prevBtn.addEventListener("click", function () {
                page--;
                render();
                root.scrollIntoView({ block: "nearest" });
            });
        }
        if (nextBtn) {
            nextBtn.addEventListener("click", function () {
                page++;
                render();
                root.scrollIntoView({ block: "nearest" });
            });
        }

        // Reveal the controls only once JS has taken over, so a no-JS page shows
        // the full table rather than dead widgets.
        var toolbar = root.querySelector("[data-result-toolbar]");
        if (toolbar) {
            toolbar.hidden = false;
        }

        render();
    }

    function init() {
        wireBusy();
        document.querySelectorAll("[data-result-root]").forEach(initResultTable);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
