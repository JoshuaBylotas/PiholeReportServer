/* ============================================================================
   Pi-hole Report Server — shared client behaviour.

   Two independent pieces, both deliberately dependency-free so the page keeps
   working with no CDN and no build step:

     1. A busy overlay that blocks interaction while a query runs.
   Result search and paging live in result-table.js.
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

    /* Result search and paging now live in result-table.js, which supports both
       the server-paged and inline cases. Keeping a second implementation here
       would mean two sets of controls fighting over the same elements. */

    function init() {
        wireBusy();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
