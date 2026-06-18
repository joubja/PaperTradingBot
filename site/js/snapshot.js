// Bot snapshot pages — ports Pages/Snapshot.razor to vanilla JS. Loads the frozen
// snapshots (/snapshots/{aether,bitgain}.json) and renders read-only Last Run / History /
// Settings. Bot comes from the URL path; view (tab) from the hash.
(function () {
    var KEYS = ["aether", "bitgain"];
    var MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    var SNAPS = {};
    var chart = null;

    function curKey() { return location.pathname.indexOf("bitgain") >= 0 ? "bitgain" : "aether"; }
    function curView() { var h = location.hash.replace("#", "").toLowerCase(); return (h === "history" || h === "settings") ? h : "dashboard"; }

    // ── formatting ──────────────────────────────────────────────────────────
    function signPct(f) { var v = f * 100; var s = v > 0 ? "+" : v < 0 ? "-" : ""; return s + Math.abs(v).toFixed(1) + "%"; }
    function coinColor(d) { return d > 0 ? "#3ddc84" : d < 0 ? "#ff5c7c" : "#9aa0b5"; }
    function trimNum(x, dp) { return parseFloat(Number(x).toFixed(dp)).toString(); }
    function n0(x) { return Math.round(x).toLocaleString("en-US"); }
    function n2(x) { return Number(x).toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
    function pad(x) { return ("0" + x).slice(-2); }
    function fDayMon(d) { return d.getUTCDate() + " " + MON[d.getUTCMonth()]; }
    function fDayMonYr(d) { return d.getUTCDate() + " " + MON[d.getUTCMonth()] + " " + d.getUTCFullYear(); }
    function fTradeTime(d) { return pad(d.getUTCDate()) + " " + MON[d.getUTCMonth()] + " " + pad(d.getUTCHours()) + ":" + pad(d.getUTCMinutes()); }
    function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]; }); }

    // ── derived metrics (ports the C# getters) ──────────────────────────────
    function lastPt(s) { return s.equityCurve.length ? s.equityCurve[s.equityCurve.length - 1] : null; }
    function finalEquityUsd(s) { var l = lastPt(s); return (l && l.equity != null) ? l.equity : (s.session.finalEquityUsd != null ? s.session.finalEquityUsd : 0); }
    function finalCoin(s) { var l = lastPt(s); return (l && l.coinQty != null) ? l.coinQty : (s.session.finalCoin != null ? s.session.finalCoin : 0); }
    function tradesByTimeDesc(s) { return s.trades.slice().sort(function (a, b) { return Date.parse(b.timestamp) - Date.parse(a.timestamp); }); }
    function endPrice(s) {
        if (s.trades.length) return tradesByTimeDesc(s)[0].price;
        return finalCoin(s) > 0 ? finalEquityUsd(s) / finalCoin(s) : 0;
    }
    function coinEquivalent(s) { var ep = endPrice(s); return ep > 0 ? finalEquityUsd(s) / ep : finalCoin(s); }
    function coinDiff(s) { return coinEquivalent(s) - s.session.startingCoin; }
    function coinDiffPct(s) { return s.session.startingCoin > 0 ? coinDiff(s) / s.session.startingCoin : 0; }
    function startEquityUsd(s) { return s.equityCurve.length ? s.equityCurve[0].equity : 0; }
    function usdReturnPct(s) { var st = startEquityUsd(s); return st > 0 ? (finalEquityUsd(s) - st) / st : 0; }
    function holdValueUsd(s) { return s.session.startingCoin * endPrice(s); }
    function holdReturnPct(s) { var st = startEquityUsd(s); return st > 0 ? (holdValueUsd(s) - st) / st : 0; }
    function vsHoldPct(s) { return usdReturnPct(s) - holdReturnPct(s); }
    function duration(s) {
        var end = s.session.stoppedAt ? new Date(s.session.stoppedAt) : new Date();
        var ms = end - new Date(s.session.startedAt);
        var days = ms / 86400000;
        return days >= 1 ? Math.round(days) + " days" : Math.round(ms / 3600000) + " h";
    }

    // ── views ───────────────────────────────────────────────────────────────
    function dashboard(s) {
        var ur = usdReturnPct(s), hr = holdReturnPct(s), vh = vsHoldPct(s), cd = coinDiff(s), cdp = coinDiffPct(s);
        var startCoin = trimNum(s.session.startingCoin, 5), endCoinEq = trimNum(coinEquivalent(s), 5);
        var stopped = s.session.stoppedAt ? fDayMonYr(new Date(s.session.stoppedAt)) : "—";
        var html =
            '<div class="rc-stats">' +
            tile("Ran for", duration(s), fDayMon(new Date(s.session.startedAt)) + " – " + stopped) +
            tile("Starting stake", startCoin + ' <span style="font-size:0.5em;color:var(--faint);">' + esc(s.coin) + '</span>', "strategy: " + esc(s.session.strategy)) +
            tile("Final portfolio value", "$" + n0(finalEquityUsd(s)),
                '<span style="color:' + coinColor(ur) + '">' + signPct(ur) + '</span> in $ · just holding: <span style="color:' + coinColor(hr) + '">' + signPct(hr) + '</span>') +
            tile("Coin pile, after fees", '<span style="color:' + coinColor(cd) + '">' + signPct(cdp) + '</span>', startCoin + " → " + endCoinEq + " " + esc(s.coin) + " equiv.") +
            tile("Trades", s.session.tradeCount, "over the whole run") +
            tile("Win rate", (s.session.winRatePct != null ? trimNum(s.session.winRatePct, 1) : "—") + "%", "of closed trades") +
            '</div>' +
            '<div class="rc-point" style="margin-top:22px;"><div class="rc-point-n">!</div><div class="rc-point-t">' +
            '<b>A high win rate is not the same as winning.</b> The bot booked many tiny profitable cycles while losers sat unrealised — and its dollar value mostly just tracked the coin\'s own price. ' +
            'In dollars it ended <b style="color:' + coinColor(ur) + '">' + signPct(ur) + '</b>, but simply holding the same ' + esc(s.coin) + ' would have returned <b style="color:' + coinColor(hr) + '">' + signPct(hr) + '</b> — ' +
            'so it <b style="color:' + coinColor(vh) + '">' + (vh < 0 ? "trailed" : "beat") + ' holding by ' + (Math.abs(vh) * 100).toFixed(1) + '%</b>. ' +
            'Its real aim was to end with <i>more coin</i> than it started; valued back into ' + esc(s.coin) + ' after fees it ended <b style="color:' + coinColor(cd) + '">' + signPct(cdp) + '</b> — it didn\'t grow the pile. ' +
            'The full verdict is on the <a href="/" style="color:var(--eth-2);">Reality Check</a> page.</div></div>';
        if (s.equityCurve.length) {
            html += '<div class="rc-chartwrap"><div class="rc-chart-title">Portfolio value over the run (USD)</div>' +
                '<div class="rc-chart-sub">mostly follows the ' + esc(s.coin) + ' price — not a measure of skill</div><div id="rc-chart"></div></div>';
        }
        return html;
    }
    function tile(label, val, sub) {
        return '<div class="rc-tile"><div class="rc-tile-label">' + label + '</div><div class="rc-tile-val">' + val + '</div><div class="rc-tile-sub">' + sub + '</div></div>';
    }
    function history(s) {
        var rows = tradesByTimeDesc(s).map(function (t) {
            var sideCls = t.side.toUpperCase() === "BUY" ? "rc-buy" : "rc-sell";
            var pnlCls = t.realizedPnl > 0 ? "rc-num-pos" : t.realizedPnl < 0 ? "rc-num-neg" : "";
            var pnl = t.realizedPnl === 0 ? "—" : "$" + n2(t.realizedPnl);
            return '<tr><td>' + fTradeTime(new Date(t.timestamp)) + '</td><td class="' + sideCls + '">' + esc(t.side.toUpperCase()) + '</td>' +
                '<td>' + trimNum(t.quantity, 5) + '</td><td>$' + n2(t.price) + '</td><td>$' + n2(t.notional) + '</td><td>$' + n2(t.commission) + '</td>' +
                '<td class="' + pnlCls + '">' + pnl + '</td></tr>';
        }).join("");
        return '<div class="rc-tablewrap"><table class="rc-table"><thead><tr><th>Time (UTC)</th><th>Side</th><th>Qty</th><th>Price</th><th>Notional</th><th>Fee</th><th>Realized PnL</th></tr></thead><tbody>' + rows + '</tbody></table></div>';
    }
    function settings(s) {
        // NB: settings keys are PascalCase in the snapshot JSON (unlike session/trades/curve).
        var g = s.settings;
        var rows = [
            ["RSI dip-buy threshold", trimNum(g.RsiDipBuy, 1)],
            ["RSI crossover max", trimNum(g.RsiCrossoverMax, 1)],
            ["RSI cycle-sell", trimNum(g.RsiCycleSell, 1)],
            ["RSI cycle-rebuy", trimNum(g.RsiCycleRebuy, 1)],
            ["Default sell %", trimNum(g.DefaultSellPct * 100, 1) + "%"],
            ["Min abandon rise", trimNum(g.MinAbandonRise * 100, 2) + "%"],
            ["Max abandon rise", trimNum(g.MaxAbandonRise * 100, 2) + "%"],
            ["Cycle cooldown (bars)", g.CycleCooldownBars]
        ];
        return '<p class="rc-lead" style="margin-top:22px;">The exact parameters the self-learning optimiser had landed on when the bot was switched off. ' +
            'These are real tuned values — and per the Reality Check, optimising them still didn\'t beat holding.</p>' +
            '<div class="rc-kv">' + rows.map(function (kv) {
                return '<div class="rc-kvrow"><div class="rc-kvk">' + kv[0] + '</div><div class="rc-kvv">' + kv[1] + '</div></div>';
            }).join("") + '</div>';
    }

    function render() {
        var cur = curKey(), v = curView(), s = SNAPS[cur];
        var root = document.getElementById("rc-snap");
        if (!s) { root.innerHTML = '<div class="rc-section">No bot snapshots loaded.</div>'; return; }

        var switcher = '<div class="rc-ws"><div class="rc-switch">' + KEYS.map(function (k) {
            var sn = SNAPS[k]; if (!sn) return "";
            var cls = (sn.coin === "BTC" ? "btc" : "eth") + (k === cur ? " on" : "");
            var glyph = sn.coin === "BTC" ? "₿" : "Ξ", gc = sn.coin === "BTC" ? "var(--btc)" : "var(--eth-2)";
            var hash = v === "dashboard" ? "" : "#" + v;
            return '<a class="' + cls + '" href="/bots/' + k + hash + '"><span class="rc-cglyph" style="color:' + gc + ';">' + glyph + '</span> ' + esc(sn.bot) + ' <span style="color:var(--faint);font-weight:500;font-size:0.85em;">' + esc(sn.coin) + '</span></a>';
        }).join("") + '</div><span class="rc-ro"><span class="rc-dot"></span>Read-only · bot is switched off</span></div>';

        var tabs = '<div class="rc-tabs">' +
            '<a class="rc-tab ' + (v === "dashboard" ? "on" : "") + '" href="#dashboard">Last Run</a>' +
            '<a class="rc-tab ' + (v === "history" ? "on" : "") + '" href="#history">History (' + s.trades.length + ')</a>' +
            '<a class="rc-tab ' + (v === "settings" ? "on" : "") + '" href="#settings">Settings</a></div>';

        var body = v === "history" ? history(s) : v === "settings" ? settings(s) : dashboard(s);
        root.innerHTML = switcher + tabs + body;

        if (chart) { chart.destroy(); chart = null; }
        if (v === "dashboard" && s.equityCurve.length) {
            chart = new ApexCharts(document.getElementById("rc-chart"), {
                chart: { type: "area", height: 320, toolbar: { show: false }, background: "transparent", animations: { enabled: false }, fontFamily: "Inter, sans-serif" },
                theme: { mode: "dark" }, dataLabels: { enabled: false }, stroke: { width: 2, curve: "smooth" },
                colors: ["#7c5cff"], fill: { type: "gradient", gradient: { opacityFrom: 0.35, opacityTo: 0.0 } },
                xaxis: { type: "datetime" }, grid: { borderColor: "rgba(255,255,255,0.06)" }, tooltip: { theme: "dark" },
                series: [{ name: "Portfolio $", data: s.equityCurve.map(function (p) { return [Date.parse(p.t), Math.round(p.equity * 100) / 100]; }) }]
            });
            chart.render();
        }
        // update document title to the active bot
        document.title = s.bot + " snapshot — Reality Check";
    }

    Promise.all(KEYS.map(function (k) {
        return fetch("/snapshots/" + k + ".json").then(function (r) { return r.json(); }).then(function (d) { SNAPS[k] = d; }).catch(function () { });
    })).then(function () {
        render();
        window.addEventListener("hashchange", render);
    });
})();
