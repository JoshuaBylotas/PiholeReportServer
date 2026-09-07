/* ============================================================================
   Analyst chat.

   Submits a turn without reloading the page and appends it to the transcript,
   which is most of what separates "a conversation" from "a form that returns
   prose". The result rows are rendered as a real table under the answer and
   handed to result-table.js, so searching and paging work exactly as they do on
   the report pages.

   The page also renders turns server-side via _ChatTurn.cshtml for a reloaded
   conversation; the DOM built here has to match that shape.
   ========================================================================= */
(function () {
    "use strict";

    var root = document.querySelector("[data-chat]");
    if (!root) { return; }

    var form = root.querySelector("[data-chat-form]");
    var input = root.querySelector("[data-chat-input]");
    var send = root.querySelector("[data-chat-send]");
    var sendLabel = root.querySelector("[data-chat-send-label]");
    var transcript = root.querySelector("[data-chat-transcript]");
    var idField = root.querySelector("[data-chat-id]");
    var pageSize = parseInt(root.getAttribute("data-page-size"), 10) || 100;

    var busy = false;

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text !== undefined && text !== null) { e.textContent = text; }
        return e;
    }

    function grow() {
        // A single-line box that grows is friendlier than a fixed textarea, and a
        // cap stops a pasted paragraph from taking the whole viewport.
        input.style.height = "auto";
        input.style.height = Math.min(input.scrollHeight, 160) + "px";
    }

    function setBusy(on, note) {
        busy = on;
        input.disabled = on;
        send.disabled = on;
        if (sendLabel) { sendLabel.textContent = on ? (note || "Thinking…") : "Ask"; }
        root.classList.toggle("chat-busy", on);
    }

    function scrollToEnd(node) {
        // Bring the start of the new answer into view rather than the very bottom:
        // a large table would otherwise push the answer text off the screen.
        (node || transcript).scrollIntoView({ block: "nearest", behavior: "smooth" });
    }

    function addUserMessage(text) {
        var intro = transcript.querySelector("[data-chat-intro]");
        if (intro) { intro.remove(); }

        var turn = el("div", "chat-turn");
        var msg = el("div", "chat-msg chat-msg-user");
        msg.appendChild(el("div", "chat-bubble", text));
        turn.appendChild(msg);
        transcript.appendChild(turn);
        return turn;
    }

    function addPending(turn) {
        var msg = el("div", "chat-msg chat-msg-ai");
        var body = el("div", "chat-body");
        var wait = el("div", "chat-thinking");
        wait.appendChild(el("span", "spinner"));
        wait.appendChild(el("span", "small muted", "Querying and reading the results…"));
        body.appendChild(wait);
        msg.appendChild(body);
        turn.appendChild(msg);
        return body;
    }

    function renderTable(t) {
        var wrap = el("div", "chat-table");

        var meta = el("div", "result-meta");
        var rows = el("span");
        rows.appendChild(el("strong", null, t.totalRows.toLocaleString()));
        rows.appendChild(document.createTextNode(" row" + (t.totalRows === 1 ? "" : "s")));
        meta.appendChild(rows);
        meta.appendChild(el("span", null, t.columns.length + " column" + (t.columns.length === 1 ? "" : "s")));
        if (t.truncated) { meta.appendChild(el("span", "badge badge-gold", "Capped")); }
        wrap.appendChild(meta);

        var host = el("div");
        host.setAttribute("data-result-root", "");
        host.setAttribute("data-result-pagesize", pageSize);
        host.setAttribute("data-result-total", t.totalRows);
        host.setAttribute("data-result-serverpaged", t.serverPaged ? "1" : "0");
        if (t.token) { host.setAttribute("data-result-token", t.token); }

        // Same toolbar markup result-table.js expects, hidden until it initialises.
        var toolbar = el("div", "result-toolbar");
        toolbar.setAttribute("data-result-toolbar", "");
        toolbar.hidden = true;
        var search = el("div", "result-search");
        var box = el("input");
        box.type = "search";
        box.setAttribute("data-result-search", "");
        box.placeholder = "Search all columns…";
        box.autocomplete = "off";
        search.appendChild(box);
        toolbar.appendChild(search);
        var status = el("span", "small muted nowrap");
        status.setAttribute("data-result-status", "");
        toolbar.appendChild(status);
        toolbar.appendChild(el("span", "spacer"));
        var pager = el("div", "pager");
        var prev = el("button", "btn btn-secondary btn-compact", "← Prev");
        prev.type = "button";
        prev.setAttribute("data-result-prev", "");
        var next = el("button", "btn btn-secondary btn-compact", "Next →");
        next.type = "button";
        next.setAttribute("data-result-next", "");
        pager.appendChild(prev);
        pager.appendChild(next);
        toolbar.appendChild(pager);
        host.appendChild(toolbar);

        var empty = el("div", "notice notice-info", "No rows match that search.");
        empty.setAttribute("data-result-empty", "");
        empty.hidden = true;
        host.appendChild(empty);

        var expired = el("div", "notice notice-warn", "That result has expired. Ask again to page through it.");
        expired.setAttribute("data-result-expired", "");
        expired.hidden = true;
        host.appendChild(expired);

        var tableWrap = el("div", "table-wrap");
        var table = el("table", "data");
        var thead = el("thead");
        var hr = el("tr");
        t.columns.forEach(function (c) {
            var th = el("th", c.numeric ? "num" : null, c.name);
            th.scope = "col";
            hr.appendChild(th);
        });
        thead.appendChild(hr);
        table.appendChild(thead);

        var tbody = el("tbody");
        tbody.setAttribute("data-result-body", "");
        t.rows.forEach(function (row) {
            var tr = el("tr");
            for (var i = 0; i < row.length; i++) {
                var td;
                if (row[i] === null) {
                    td = el("td", "null", "null");
                } else {
                    // textContent throughout: these are database values, and a domain
                    // name is attacker-influenced text.
                    td = el("td", t.columns[i] && t.columns[i].numeric ? "num" : null, row[i]);
                }
                tr.appendChild(td);
            }
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        tableWrap.appendChild(table);
        host.appendChild(tableWrap);
        wrap.appendChild(host);
        return wrap;
    }

    function renderSteps(steps) {
        var det = el("details", "chat-steps");
        var sum = el("summary", "small muted",
            "Show the " + steps.length + " quer" + (steps.length === 1 ? "y" : "ies") + " it ran");
        det.appendChild(sum);

        steps.forEach(function (s) {
            var box = el("div", "agent-step");
            var row = el("div", "row");
            row.appendChild(el("span", "step-number", s.number));
            if (s.kind === "Query") {
                row.appendChild(el("span", "badge badge-pine",
                    s.rowCount.toLocaleString() + " row" + (s.rowCount === 1 ? "" : "s")));
            } else if (s.kind === "Refused") {
                row.appendChild(el("span", "badge badge-gold", "Refused by the guard"));
            } else {
                row.appendChild(el("span", "badge badge-gold", "Query failed"));
            }
            row.appendChild(el("span", "spacer"));
            if (s.milliseconds) {
                row.appendChild(el("span", "small muted", s.milliseconds.toLocaleString() + " ms"));
            }
            box.appendChild(row);
            if (s.reasoning) {
                var p = el("p", "small muted", s.reasoning);
                p.style.margin = ".5rem 0 0";
                box.appendChild(p);
            }
            box.appendChild(el("pre", "ai-sql", s.sql || ""));
            if (s.problem) {
                var warn = el("div", "notice notice-warn", s.problem);
                warn.style.marginBottom = "0";
                box.appendChild(warn);
            }
            det.appendChild(box);
        });
        return det;
    }

    /* Keep the memory panel in step when a turn taught it something, so the user
       sees immediately what was stored rather than after a reload. */
    function refreshMemory(d) {
        if (!d.facts) { return; }

        var panel = document.querySelector("[data-memory-panel]");
        var list = panel && panel.querySelector("[data-memory-list]");
        var count = panel && panel.querySelector("[data-memory-count]");
        if (!list) { return; }

        list.replaceChildren();
        d.facts.forEach(function (f) {
            var li = el("li");
            li.appendChild(el("span", f.kind === "device" ? "badge badge-pine" : "badge badge-gold",
                              f.kind === "device" ? "device" : "note"));
            li.appendChild(el("span", "memory-fact", f.fact));
            if (f.target) { li.appendChild(el("span", "mono small muted", f.target)); }
            list.appendChild(li);
        });
        if (d.facts.length === 0) {
            list.appendChild(el("li", "small muted", "Nothing yet."));
        }
        if (count) { count.textContent = d.facts.length; }
        if (d.facts.length > 0) { panel.open = true; }
    }

    function renderAnswer(body, d) {
        body.replaceChildren();

        if (d.answer) {
            body.appendChild(el("div", "ai-prose", d.answer));
        }

        // Say plainly what was stored. Silent memory is the failure mode here: a
        // wrong fact remembered quietly poisons every later answer.
        if (d.memoryNote) {
            body.appendChild(el("div", "notice notice-info", d.memoryNote));
            refreshMemory(d);
        }

        if (d.error) {
            var err = el("div", "notice notice-error");
            err.appendChild(el("strong", null, "That did not complete. "));
            err.appendChild(document.createTextNode(d.error));
            body.appendChild(err);
        } else if (!d.answer) {
            var note = d.outcome === "StepLimit"
                ? "It ran out of queries before reaching a conclusion. The table below is what it found."
                : d.outcome === "Timeout"
                    ? "It ran out of time. The table below is what it found."
                    : "It finished without an answer.";
            body.appendChild(el("div", "notice notice-warn", note));
        }

        if (d.table && d.table.totalRows > 0) {
            body.appendChild(renderTable(d.table));
        }

        var meta = el("div", "chat-meta small muted");
        meta.appendChild(el("span", null, d.queries + " quer" + (d.queries === 1 ? "y" : "ies")));
        meta.appendChild(el("span", null, Math.round(d.elapsedSeconds) + "s"));
        if (d.model) { meta.appendChild(el("span", "mono", d.model)); }
        body.appendChild(meta);

        if (d.steps && d.steps.length) {
            body.appendChild(renderSteps(d.steps));
        }

        // Hand the new table to the shared script so search and paging bind to it.
        if (window.ResultTable && typeof window.ResultTable.init === "function") {
            window.ResultTable.init(body);
        }
    }

    function ask(question) {
        if (busy || !question) { return; }

        var turn = addUserMessage(question);
        var body = addPending(turn);
        scrollToEnd(turn);

        input.value = "";
        grow();
        setBusy(true);

        var data = new FormData(form);
        data.set("Question", question);
        // Use the input's own name rather than a literal, so this cannot disagree
        // with what the server binds. It did once, and the symptom was silent: every
        // turn began a new conversation and follow-ups lost all context.
        if (idField && idField.value) { data.set(idField.name, idField.value); }

        fetch(form.getAttribute("action") || window.location.pathname + "?handler=Ask", {
            method: "POST",
            body: data,
            headers: { "Accept": "application/json" },
        })
            .then(function (r) {
                if (!r.ok) { throw new Error("HTTP " + r.status); }
                return r.json();
            })
            .then(function (d) {
                if (!d.ok) {
                    body.replaceChildren();
                    var err = el("div", "notice notice-error", d.error || "That request failed.");
                    body.appendChild(err);
                    return;
                }
                // Carry the conversation id forward so the next turn continues this one.
                if (d.conversationId && idField) { idField.value = d.conversationId; }
                renderAnswer(body, d);
            })
            .catch(function (e) {
                body.replaceChildren();
                body.appendChild(el("div", "notice notice-error",
                    "Could not reach the server: " + e.message));
            })
            .finally(function () {
                setBusy(false);
                scrollToEnd(turn);
                input.focus();
            });
    }

    form.addEventListener("submit", function (e) {
        e.preventDefault();
        ask(input.value.trim());
    });

    input.addEventListener("input", grow);

    input.addEventListener("keydown", function (e) {
        // Enter sends, Shift+Enter makes a new line - the convention for a chat box.
        if (e.key === "Enter" && !e.shiftKey) {
            e.preventDefault();
            ask(input.value.trim());
        }
    });

    root.addEventListener("click", function (e) {
        var chip = e.target.closest("[data-chat-suggestion]");
        if (chip) {
            input.value = chip.textContent.trim();
            grow();
            ask(input.value);
        }
    });

    grow();
    if (!input.disabled) { input.focus(); }
})();
