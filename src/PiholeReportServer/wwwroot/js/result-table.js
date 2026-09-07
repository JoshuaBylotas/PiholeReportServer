/* ============================================================================
   Result search and paging.

   Two modes, chosen by whether the server gave us a token:

     server-paged   The result is held in the server's cache. Only one page of
                    rows is ever in the DOM, and search runs against the cached
                    rows server-side. This is what makes a 100,000-row cap
                    usable: rendering that many rows freezes the browser, and
                    re-running the query per page would scan 24M rows to fetch
                    the next hundred.

     inline         No token, so the whole result is already rendered. Used for
                    the small transient samples in an agent trace. Filtering
                    just hides rows.

   Replaces the earlier version, which only ever did the inline case.
   ========================================================================= */
(function () {
    "use strict";

    function initInline(root) {
        var tbody = root.querySelector("[data-result-body]");
        if (!tbody) { return; }

        var rows = Array.prototype.slice.call(tbody.rows);
        if (rows.length === 0) { return; }

        // Lowercase each row once rather than on every keystroke.
        var hay = rows.map(function (r) { return (r.textContent || "").toLowerCase(); });

        var search = root.querySelector("[data-result-search]");
        var status = root.querySelector("[data-result-status]");
        var empty = root.querySelector("[data-result-empty]");
        var prev = root.querySelector("[data-result-prev]");
        var next = root.querySelector("[data-result-next]");

        // Paging is meaningless when everything is already on screen.
        if (prev) { prev.hidden = true; }
        if (next) { next.hidden = true; }

        function apply() {
            var term = (search ? search.value : "").trim().toLowerCase();
            var terms = term === "" ? [] : term.split(/\s+/);
            var shown = 0;

            for (var i = 0; i < rows.length; i++) {
                var match = true;
                for (var t = 0; t < terms.length; t++) {
                    if (hay[i].indexOf(terms[t]) === -1) { match = false; break; }
                }
                rows[i].hidden = !match;
                if (match) { shown++; }
            }
            if (status) {
                status.textContent = shown === rows.length
                    ? "Showing all " + rows.length.toLocaleString()
                    : "Showing " + shown.toLocaleString() + " of " + rows.length.toLocaleString();
            }
            if (empty) { empty.hidden = shown !== 0; }
        }

        if (search) {
            var debounce = null;
            search.addEventListener("input", function () {
                window.clearTimeout(debounce);
                debounce = window.setTimeout(apply, 120);
            });
            search.addEventListener("keydown", function (e) {
                if (e.key === "Escape") { search.value = ""; apply(); }
            });
        }
        apply();
    }

    function initServerPaged(root) {
        var token = root.getAttribute("data-result-token");
        var tbody = root.querySelector("[data-result-body]");
        var search = root.querySelector("[data-result-search]");
        var status = root.querySelector("[data-result-status]");
        var empty = root.querySelector("[data-result-empty]");
        var expired = root.querySelector("[data-result-expired]");
        var prev = root.querySelector("[data-result-prev]");
        var next = root.querySelector("[data-result-next]");

        var page = 0;
        var pageCount = 1;
        var inFlight = null;

        function render(d) {
            var frag = document.createDocumentFragment();
            d.rows.forEach(function (row) {
                var tr = document.createElement("tr");
                for (var i = 0; i < row.length; i++) {
                    var td = document.createElement("td");
                    var col = d.columns[i];
                    if (row[i] === null) {
                        td.className = "null";
                        td.textContent = "null";
                    } else {
                        if (col && col.numeric) { td.className = "num"; }
                        // textContent, never innerHTML: these are database values.
                        td.textContent = row[i];
                    }
                    tr.appendChild(td);
                }
                frag.appendChild(tr);
            });
            tbody.replaceChildren(frag);

            page = d.page;
            pageCount = d.pageCount;

            if (status) {
                if (d.matchingRows === 0) {
                    status.textContent = "No rows match";
                } else {
                    var s = "Showing " + d.first.toLocaleString() + "–" +
                            d.last.toLocaleString() + " of " + d.matchingRows.toLocaleString();
                    if (d.isFiltered) {
                        s += " (filtered from " + d.totalRows.toLocaleString() + ")";
                    }
                    s += "  ·  page " + (d.page + 1).toLocaleString() +
                         " of " + d.pageCount.toLocaleString();
                    status.textContent = s;
                }
            }
            if (empty) { empty.hidden = d.matchingRows !== 0; }
            if (prev) { prev.disabled = d.page <= 0; }
            if (next) { next.disabled = d.page >= d.pageCount - 1; }
        }

        function load(targetPage) {
            if (inFlight) { inFlight.abort(); }
            var controller = new AbortController();
            inFlight = controller;

            var url = "/Results/Page?rs=" + encodeURIComponent(token) +
                      "&p=" + targetPage +
                      "&q=" + encodeURIComponent(search ? search.value.trim() : "");

            if (status) { status.textContent = "Loading…"; }

            fetch(url, { signal: controller.signal, headers: { "Accept": "application/json" } })
                .then(function (r) {
                    if (!r.ok) { throw new Error("HTTP " + r.status); }
                    return r.json();
                })
                .then(function (d) {
                    if (!d.ok) {
                        // An expired cache entry is normal after a while, not an error
                        // worth alarming about - say what to do instead.
                        if (d.expired && expired) {
                            expired.hidden = false;
                            if (prev) { prev.disabled = true; }
                            if (next) { next.disabled = true; }
                            if (search) { search.disabled = true; }
                            if (status) { status.textContent = ""; }
                            return;
                        }
                        if (status) { status.textContent = d.error || "Could not load that page."; }
                        return;
                    }
                    if (expired) { expired.hidden = true; }
                    render(d);
                })
                .catch(function (e) {
                    if (e.name === "AbortError") { return; }   // superseded by a newer request
                    if (status) { status.textContent = "Could not load that page: " + e.message; }
                })
                .finally(function () {
                    if (inFlight === controller) { inFlight = null; }
                });
        }

        if (search) {
            var debounce = null;
            search.addEventListener("input", function () {
                window.clearTimeout(debounce);
                // Longer than the inline case: each keystroke is a round-trip that
                // scans the cached rows, so coalesce more aggressively.
                debounce = window.setTimeout(function () { load(0); }, 300);
            });
            search.addEventListener("keydown", function (e) {
                if (e.key === "Escape") { search.value = ""; load(0); }
            });
        }
        if (prev) {
            prev.addEventListener("click", function () {
                if (page > 0) { load(page - 1); root.scrollIntoView({ block: "nearest" }); }
            });
        }
        if (next) {
            next.addEventListener("click", function () {
                if (page < pageCount - 1) { load(page + 1); root.scrollIntoView({ block: "nearest" }); }
            });
        }

        // Fetch page 1 so the status line and pager reflect real totals; the
        // server-rendered first page stays visible until it arrives.
        load(0);
    }

    function init() {
        document.querySelectorAll("[data-result-root]").forEach(function (root) {
            var toolbar = root.querySelector("[data-result-toolbar]");
            if (toolbar) { toolbar.hidden = false; }

            if (root.getAttribute("data-result-serverpaged") === "1" &&
                root.getAttribute("data-result-token")) {
                initServerPaged(root);
            } else {
                initInline(root);
            }
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
