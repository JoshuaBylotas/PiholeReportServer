/* ============================================================================
   Minimal bar / line / pie rendering, as inline SVG.

   Hand-written rather than a charting library: the whole point of this server is
   that DNS history never leaves the network, and pulling a script from a CDN to
   draw a bar chart would undo that for the sake of a few hundred lines. SVG also
   scales, prints, and needs no canvas sizing dance.

   Values arrive as formatted strings from the server (they are the same cells the
   table shows), so parsing strips separators. A row that will not parse is
   skipped rather than drawn as zero, because a phantom empty bar reads as real
   data.
   ========================================================================= */
(function () {
    "use strict";

    var NS = "http://www.w3.org/2000/svg";

    // Pine and gold, matching the site palette, then muted repeats for long series.
    var COLOURS = [
        "#1b5e4a", "#c08a36", "#428f6c", "#e0b765", "#164b3c",
        "#a06e2a", "#74b394", "#ecd197", "#123c31", "#7d5421"
    ];

    function svgEl(name, attrs) {
        var e = document.createElementNS(NS, name);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
        }
        return e;
    }

    function text(x, y, str, cls, anchor) {
        var t = svgEl("text", { x: x, y: y, "text-anchor": anchor || "start" });
        if (cls) { t.setAttribute("class", cls); }
        t.textContent = str;
        return t;
    }

    function toNumber(v) {
        if (typeof v === "number") { return v; }
        if (v === null || v === undefined) { return NaN; }
        // Server-formatted numbers carry thousands separators.
        return parseFloat(String(v).replace(/[, ]/g, ""));
    }

    function shorten(n) {
        var abs = Math.abs(n);
        if (abs >= 1e9) { return (n / 1e9).toFixed(1) + "B"; }
        if (abs >= 1e6) { return (n / 1e6).toFixed(1) + "M"; }
        if (abs >= 1e3) { return (n / 1e3).toFixed(1) + "k"; }
        return String(n);
    }

    function clip(s, max) {
        s = String(s === null || s === undefined ? "" : s);
        return s.length <= max ? s : s.slice(0, max - 1) + "…";
    }

    /* Pull (label, value) pairs out of the rows, dropping anything unparseable. */
    function series(rows, labelIndex, valueIndex, cap) {
        var out = [];
        for (var i = 0; i < rows.length && out.length < cap; i++) {
            var n = toNumber(rows[i][valueIndex]);
            if (!isFinite(n)) { continue; }
            out.push({ label: String(rows[i][labelIndex] === null ? "null" : rows[i][labelIndex]),
                       value: n });
        }
        return out;
    }

    function bar(data, valueColumn) {
        var rowH = 26, pad = 8, labelW = 150, valueW = 62;
        var w = 720, h = data.length * rowH + pad * 2;
        var plotW = w - labelW - valueW - pad * 2;
        var max = Math.max.apply(null, data.map(function (d) { return d.value; }));
        if (!(max > 0)) { max = 1; }

        var svg = svgEl("svg", {
            viewBox: "0 0 " + w + " " + h, width: "100%", height: h,
            role: "img", "aria-label": "Bar chart of " + valueColumn
        });

        data.forEach(function (d, i) {
            var y = pad + i * rowH;
            var bw = Math.max(1, Math.round(d.value / max * plotW));

            svg.appendChild(text(labelW - 8, y + 17, clip(d.label, 24), "chart-label", "end"));
            svg.appendChild(svgEl("rect", {
                x: labelW, y: y + 5, width: bw, height: rowH - 12, rx: 2,
                fill: COLOURS[i % COLOURS.length]
            }));
            svg.appendChild(text(labelW + bw + 6, y + 17, shorten(d.value), "chart-value"));
        });
        return svg;
    }

    function line(data, valueColumn) {
        var w = 720, h = 260, left = 56, right = 12, top = 12, bottom = 40;
        var plotW = w - left - right, plotH = h - top - bottom;
        var max = Math.max.apply(null, data.map(function (d) { return d.value; }));
        var min = Math.min.apply(null, data.map(function (d) { return d.value; }));
        if (max === min) { max = min + 1; }

        var svg = svgEl("svg", {
            viewBox: "0 0 " + w + " " + h, width: "100%", height: h,
            role: "img", "aria-label": "Line chart of " + valueColumn
        });

        // Three gridlines is enough to read a value off; more is clutter.
        for (var g = 0; g <= 2; g++) {
            var gv = min + (max - min) * (g / 2);
            var gy = top + plotH - (gv - min) / (max - min) * plotH;
            svg.appendChild(svgEl("line", {
                x1: left, y1: gy, x2: w - right, y2: gy, class: "chart-grid"
            }));
            svg.appendChild(text(left - 8, gy + 4, shorten(gv), "chart-value", "end"));
        }

        var step = data.length > 1 ? plotW / (data.length - 1) : 0;
        var points = data.map(function (d, i) {
            var x = left + i * step;
            var y = top + plotH - (d.value - min) / (max - min) * plotH;
            return x.toFixed(1) + "," + y.toFixed(1);
        });

        svg.appendChild(svgEl("polyline", {
            points: points.join(" "), fill: "none",
            stroke: COLOURS[0], "stroke-width": 2,
            "stroke-linejoin": "round", "stroke-linecap": "round"
        }));

        // Only label a few ticks, or a 40-point series becomes unreadable.
        var every = Math.max(1, Math.ceil(data.length / 6));
        data.forEach(function (d, i) {
            var x = left + i * step;
            var y = top + plotH - (d.value - min) / (max - min) * plotH;
            if (data.length <= 40) {
                svg.appendChild(svgEl("circle", { cx: x, cy: y, r: 2.5, fill: COLOURS[0] }));
            }
            if (i % every === 0 || i === data.length - 1) {
                svg.appendChild(text(x, h - 14, clip(d.label, 12), "chart-label", "middle"));
            }
        });
        return svg;
    }

    function pie(data, valueColumn) {
        var size = 240, r = 100, cx = size / 2, cy = size / 2;
        var total = data.reduce(function (a, d) { return a + d.value; }, 0);
        if (!(total > 0)) { return null; }

        var svg = svgEl("svg", {
            viewBox: "0 0 " + (size + 260) + " " + size, width: "100%", height: size,
            role: "img", "aria-label": "Pie chart of " + valueColumn
        });

        var angle = -Math.PI / 2;   // start at twelve o'clock
        data.forEach(function (d, i) {
            var sweep = d.value / total * Math.PI * 2;
            var x1 = cx + r * Math.cos(angle), y1 = cy + r * Math.sin(angle);
            angle += sweep;
            var x2 = cx + r * Math.cos(angle), y2 = cy + r * Math.sin(angle);

            svg.appendChild(svgEl("path", {
                d: "M " + cx + " " + cy + " L " + x1.toFixed(1) + " " + y1.toFixed(1) +
                   " A " + r + " " + r + " 0 " + (sweep > Math.PI ? 1 : 0) + " 1 " +
                   x2.toFixed(1) + " " + y2.toFixed(1) + " Z",
                fill: COLOURS[i % COLOURS.length]
            }));

            var ly = 24 + i * 22;
            svg.appendChild(svgEl("rect", {
                x: size + 20, y: ly - 10, width: 12, height: 12, rx: 2,
                fill: COLOURS[i % COLOURS.length]
            }));
            svg.appendChild(text(size + 40, ly,
                clip(d.label, 22) + "  " + Math.round(d.value / total * 100) + "%",
                "chart-label"));
        });
        return svg;
    }

    /* Renders a chart, or returns null when the data cannot support one. Callers
       fall back to the table, which is never wrong. */
    function render(spec, columns, rows) {
        if (!spec || !rows || rows.length < 2) { return null; }

        var li = spec.labelIndex, vi = spec.valueIndex;
        if (li == null || vi == null || li === vi) { return null; }
        if (li >= columns.length || vi >= columns.length) { return null; }

        // A bar chart past ~30 rows is a grey smear; the table is better.
        var cap = spec.type === "pie" ? 8 : (spec.type === "line" ? 400 : 30);
        var data = series(rows, li, vi, cap);
        if (data.length < 2) { return null; }

        var svg = spec.type === "line" ? line(data, spec.valueColumn)
                : spec.type === "pie"  ? pie(data, spec.valueColumn)
                : bar(data, spec.valueColumn);
        if (!svg) { return null; }

        var box = document.createElement("figure");
        box.className = "chart";
        box.appendChild(svg);

        var cap2 = document.createElement("figcaption");
        cap2.className = "small muted";
        cap2.textContent = spec.valueColumn + " by " + spec.labelColumn +
            (data.length < rows.length
                ? " — top " + data.length + " of " + rows.length.toLocaleString() + " rows"
                : "");
        box.appendChild(cap2);
        return box;
    }

    /* Server-rendered turns carry the spec in a data attribute and the data in the
       table next to it, so the rows are read back out of the DOM. Only the first
       page is in the DOM for a server-paged result, which is fine for a bar or pie
       (capped at 30 and 8 rows) but truncates a long line chart - the caption says
       how many rows were used, so it is stated rather than hidden. */
    function initFromDom(scope) {
        (scope || document).querySelectorAll("[data-chart]").forEach(function (host) {
            if (host.hasAttribute("data-chart-done")) { return; }
            host.setAttribute("data-chart-done", "1");

            var spec;
            try {
                spec = JSON.parse(host.getAttribute("data-chart-spec") || "null");
            } catch (e) { return; }
            if (!spec) { return; }

            // The table for this turn: the next result body after this element.
            var turn = host.closest(".chat-body") || host.parentNode;
            var tbody = turn && turn.querySelector("[data-result-body]");
            var head = turn && turn.querySelector("table.data thead tr");
            if (!tbody || !head) { return; }

            var columns = Array.prototype.map.call(head.cells, function (th) {
                return { name: th.textContent.trim(), numeric: th.classList.contains("num") };
            });
            var rows = Array.prototype.map.call(tbody.rows, function (tr) {
                return Array.prototype.map.call(tr.cells, function (td) {
                    return td.classList.contains("null") ? null : td.textContent.trim();
                });
            });

            var fig = render(spec, columns, rows);
            if (fig) { host.appendChild(fig); }
        });
    }

    window.ResultChart = { render: render, initFromDom: initFromDom };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { initFromDom(); });
    } else {
        initFromDom();
    }
})();
