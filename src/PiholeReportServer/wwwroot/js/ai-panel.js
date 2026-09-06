/* ============================================================================
   The shared "Ask the data" panel.

   Two operations, deliberately different in privilege:

     Explain  - narrates a digest of the result already on screen. Needs no more
                access than viewing that result.
     Ask      - turns a question into SQL. That is arbitrary querying with extra
                steps, so the endpoint requires the Report.SqlAuthor role and a
                403 here is a meaningful answer, not a transport failure.

   Nothing generated runs automatically. A suggestion is screened server-side by
   the same guard the console uses, shown to the user, and only executed if they
   click through to the console.
   ========================================================================= */
(function () {
    "use strict";

    function token(panel) {
        var f = panel.querySelector('input[name="__RequestVerificationToken"]');
        return f ? f.value : "";
    }

    function initAiPanel(panel) {
        var output = panel.querySelector("[data-ai-output]");
        var explainBtn = panel.querySelector("[data-ai-explain]");
        var askBtn = panel.querySelector("[data-ai-ask]");
        var question = panel.querySelector("[data-ai-question]");

        function setOutput(node) {
            output.replaceChildren(node);
            output.hidden = false;
        }

        function showThinking(what) {
            var box = document.createElement("div");
            box.className = "ai-thinking";
            var spin = document.createElement("span");
            spin.className = "busy-spinner ai-spinner";
            spin.setAttribute("aria-hidden", "true");
            var label = document.createElement("span");
            label.textContent = what;
            box.append(spin, label);
            setOutput(box);
        }

        function showError(message) {
            var box = document.createElement("div");
            box.className = "notice notice-error";
            box.style.margin = "0";
            // textContent, not innerHTML: this can carry a server message.
            box.textContent = message;
            setOutput(box);
        }

        function busy(on) {
            [explainBtn, askBtn].forEach(function (b) { if (b) { b.disabled = on; } });
            if (question) { question.disabled = on; }
        }

        function post(url, payload) {
            return fetch(url, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": token(panel)
                },
                body: JSON.stringify(payload)
            }).then(function (r) {
                if (!r.ok) {
                    if (r.status === 403) {
                        throw new Error("You do not hold the Report.SqlAuthor role, which asking questions requires.");
                    }
                    throw new Error("The server returned HTTP " + r.status + ".");
                }
                return r.json();
            });
        }

        function footnote(text) {
            var f = document.createElement("div");
            f.className = "small muted";
            f.style.marginTop = "0.6rem";
            f.textContent = text;
            return f;
        }

        /* -- Explain ------------------------------------------------------- */

        if (explainBtn) {
            explainBtn.addEventListener("click", function () {
                busy(true);
                showThinking("Reading the numbers… this runs on CPU, so give it a moment.");

                post("/Ai/Explain", { digest: explainBtn.getAttribute("data-ai-digest") })
                    .then(function (d) {
                        if (!d.ok) {
                            showError(d.error || "The summary failed.");
                            return;
                        }
                        var box = document.createElement("div");
                        box.className = "ai-answer";

                        var prose = document.createElement("div");
                        prose.className = "ai-prose";
                        prose.textContent = d.summary || "(no summary returned)";
                        box.appendChild(prose);

                        box.appendChild(footnote(
                            "Written locally by " + (d.model || "the model") +
                            ". The figures it was given were computed from the result itself, not by the model."));
                        setOutput(box);
                    })
                    .catch(function (e) { showError(e.message); })
                    .finally(function () { busy(false); });
            });
        }

        /* -- Ask ----------------------------------------------------------- */

        if (askBtn && question) {
            var ask = function () {
                var q = question.value.trim();
                if (q === "") {
                    question.focus();
                    return;
                }

                busy(true);
                showThinking("Writing a query… expect 30–60 seconds on a CPU-only host.");

                post("/Ai/Ask", { question: q })
                    .then(function (d) {
                        if (!d.ok) {
                            showError(d.error || "The question failed.");
                            return;
                        }

                        var box = document.createElement("div");
                        box.className = "ai-answer";

                        if (d.notes) {
                            var n = document.createElement("p");
                            n.className = "small muted";
                            n.style.marginTop = "0";
                            n.textContent = d.notes;
                            box.appendChild(n);
                        }

                        var pre = document.createElement("pre");
                        pre.className = "ai-sql";
                        pre.textContent = d.sql;
                        box.appendChild(pre);

                        if (d.allowed) {
                            // Running is a separate, deliberate click: the suggestion is
                            // reviewed before anything touches the database, and the
                            // console screens it again on arrival.
                            var form = document.createElement("form");
                            form.method = "post";
                            form.action = "/Query/Sql";

                            var tok = document.createElement("input");
                            tok.type = "hidden";
                            tok.name = "__RequestVerificationToken";
                            tok.value = token(panel);

                            var sqlField = document.createElement("input");
                            sqlField.type = "hidden";
                            sqlField.name = "Sql";
                            sqlField.value = d.sql;

                            var run = document.createElement("button");
                            run.type = "submit";
                            run.className = "btn btn-primary";
                            run.textContent = "Run this in the SQL console";

                            form.append(tok, sqlField, run);
                            box.appendChild(form);
                        } else {
                            var bad = document.createElement("div");
                            bad.className = "notice notice-warn";
                            bad.style.marginBottom = "0";
                            bad.textContent =
                                "The screening rejected this suggestion, so it cannot be run: " +
                                (d.reason || "no reason given.");
                            box.appendChild(bad);
                        }

                        box.appendChild(footnote(
                            "Suggested by " + (d.model || "the model") +
                            ", running locally. Read a generated query before you run it."));
                        setOutput(box);
                    })
                    .catch(function (e) { showError(e.message); })
                    .finally(function () { busy(false); });
            };

            askBtn.addEventListener("click", ask);
            question.addEventListener("keydown", function (e) {
                if (e.key === "Enter") {
                    e.preventDefault();
                    ask();
                }
            });
        }
    }

    function init() {
        document.querySelectorAll("[data-ai-panel]").forEach(initAiPanel);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
