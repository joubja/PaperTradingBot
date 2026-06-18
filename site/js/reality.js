// Reality Check interactive explorer — ports Pages/RealityCheck.razor to vanilla JS.
// Loads precomputed results (/data/reality.json) and redraws the regime cards + chart.
(function () {
    var STRAT_ORDER = ["Technical", "TrendFollow", "BuildEthCycling", "ExposureController"];
    var STRAT_LABEL = {
        "Technical": "Classic TA (EMA / RSI / MACD)",
        "TrendFollow": "Trend-following (ride trends, dodge crashes)",
        "BuildEthCycling": "Buy-the-dip / sell-the-bounce cycling",
        "ExposureController": "Crash circuit-breaker (de-risk on drops)"
    };
    var BOT_STRATEGY = "BuildEthCycling";

    var DATA = [];
    var coin = "ETHUSDT", strategy = BOT_STRATEGY, selected = null, chart = null;

    function coinLabel(s) { return s.replace("USDT", ""); }
    function botName(c) { return c === "BTCUSDT" ? "BitGain" : "Aether"; }
    function regimeLabel(d) {
        if (d.indexOf("uptrend") >= 0) return "Bull market";
        if (d.indexOf("flat") >= 0) return "Flat / ranging";
        if (d.indexOf("downtrend") >= 0) return "Downtrend";
        if (d.indexOf("crash-covid") >= 0) return "Crash — COVID 2020";
        if (d.indexOf("crash-ftx") >= 0) return "Crash — FTX 2022";
        if (d.indexOf("crash-yencarry") >= 0) return "Crash — Aug 2024";
        if (d.indexOf("bleed-2021") >= 0) return "Slow bear — 2021";
        if (d.indexOf("bleed-celsius") >= 0) return "Slow bear — 2022";
        if (d.indexOf("bleed-2025") >= 0) return "Slow bear — 2025";
        return d;
    }
    function regimeOrder(d) {
        if (d.indexOf("uptrend") >= 0) return 0;
        if (d.indexOf("flat") >= 0) return 1;
        if (d.indexOf("crash") >= 0) return 2;
        if (d.indexOf("bleed") >= 0 || d.indexOf("downtrend") >= 0) return 3;
        return 9;
    }
    function strategies() {
        var set = {};
        DATA.forEach(function (r) { set[r.Strategy] = 1; });
        return Object.keys(set).sort(function (a, b) {
            var ia = STRAT_ORDER.indexOf(a), ib = STRAT_ORDER.indexOf(b);
            return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib);
        });
    }
    function forCombo(sym, strat) {
        return DATA.filter(function (r) { return r.Symbol === sym && r.Strategy === strat; })
                   .sort(function (a, b) { return regimeOrder(a.Dataset) - regimeOrder(b.Dataset); });
    }
    function summaryFor(c) {
        var rows = forCombo(c, BOT_STRATEGY);
        var worse = rows.filter(function (r) { return r.EdgeReturn < 0; }).length;
        return worse + " / " + rows.length;
    }
    function signPct(f) { var v = f * 100; var s = v > 0 ? "+" : v < 0 ? "-" : ""; return s + Math.abs(v).toFixed(1) + "%"; }
    function returnColor(r) { return r > 0 ? "#3ddc84" : r < 0 ? "#ff5c7c" : "#9aa0b5"; }
    function trimNum(x, dp) { return parseFloat(x.toFixed(dp)).toString(); }
    function edgeWord(r) {
        if (r.TradeCount === 0) return "it never traded — identical to just holding";
        return r.EdgeReturn >= 0
            ? (r.StrategyReturn < 0 ? "still a loss, just smaller than holding's" : "actually beat holding")
            : "worse than holding";
    }

    function renderVerdicts() {
        var e = document.getElementById("verdict-eth"), b = document.getElementById("verdict-btc");
        if (e) e.textContent = summaryFor("ETHUSDT");
        if (b) b.textContent = summaryFor("BTCUSDT");
    }
    function renderCards() {
        document.querySelectorAll(".rc-bot").forEach(function (el) {
            el.classList.toggle("sel", el.getAttribute("data-coin") === coin);
        });
    }
    function renderPills() {
        var pills = document.getElementById("rc-pills");
        pills.innerHTML = "";
        strategies().forEach(function (s) {
            var d = document.createElement("div");
            d.className = "rc-pill" + (s === strategy ? " sel" : "");
            d.textContent = STRAT_LABEL[s] || s;
            d.addEventListener("click", function () { strategy = s; refresh(); });
            pills.appendChild(d);
        });
    }
    function renderRegimes(rows) {
        var wrap = document.getElementById("rc-regimes");
        wrap.innerHTML = "";
        rows.forEach(function (r) {
            var div = document.createElement("div");
            div.className = "rc-reg" + (r === selected ? " sel" : "");
            var name = document.createElement("div"); name.className = "rc-reg-name"; name.textContent = regimeLabel(r.Dataset);
            var edge = document.createElement("div"); edge.className = "rc-reg-edge"; edge.style.color = returnColor(r.StrategyReturn);
            var ev = document.createElement("span"); ev.textContent = signPct(r.StrategyReturn);
            var es = document.createElement("small"); es.textContent = " " + (r.StrategyReturn < 0 ? "you'd have lost" : "you'd have made");
            edge.appendChild(ev); edge.appendChild(es);
            var det = document.createElement("div"); det.className = "rc-reg-detail";
            det.style.color = r.EdgeReturn < 0 ? "#ff5c7c" : "#9aa0b5"; det.style.fontWeight = r.EdgeReturn < 0 ? "600" : "400";
            det.textContent = signPct(r.EdgeReturn) + " vs holding — " + edgeWord(r);
            var meta = document.createElement("div"); meta.className = "rc-reg-meta";
            meta.textContent = "holding " + signPct(r.BuyHoldReturn) + " · " + r.TradeCount + " trades";
            div.appendChild(name); div.appendChild(edge); div.appendChild(det); div.appendChild(meta);
            div.addEventListener("click", function () { selected = r; renderRegimes(rows); renderChart(); });
            wrap.appendChild(div);
        });
    }
    function renderChart() {
        var titleEl = document.getElementById("rc-chart-title"), subEl = document.getElementById("rc-chart-sub");
        var host = document.getElementById("rc-chart");
        if (!selected) { titleEl.textContent = ""; subEl.textContent = ""; if (chart) { chart.destroy(); chart = null; } host.innerHTML = ""; return; }
        titleEl.textContent = regimeLabel(selected.Dataset) + " — $100 invested";
        subEl.textContent = "after a realistic " + (selected.SlippagePercent * 100).toFixed(3) + "% slippage + " + trimNum(selected.TakerFeePercent, 2) + "% fee per trade";
        var hold = selected.Curve.map(function (p) { return [Date.parse(p.T), Math.round(p.BuyHold * 1000) / 1000]; });
        var strat = selected.Curve.map(function (p) { return [Date.parse(p.T), Math.round(p.Strategy * 1000) / 1000]; });
        var opts = {
            chart: { type: "line", height: 320, toolbar: { show: false }, background: "transparent", animations: { enabled: false }, fontFamily: "Inter, sans-serif" },
            theme: { mode: "dark" },
            stroke: { width: 2, curve: "smooth" },
            colors: ["#6b7088", "#7c5cff"],
            xaxis: { type: "datetime" },
            legend: { labels: { colors: "#9aa0b5" } },
            grid: { borderColor: "rgba(255,255,255,0.06)" },
            tooltip: { theme: "dark", x: { format: "dd MMM HH:mm" } },
            series: [{ name: "Just holding", data: hold }, { name: "The bot's strategy", data: strat }]
        };
        if (chart) chart.destroy();
        chart = new ApexCharts(host, opts);
        chart.render();
    }
    function refresh() {
        renderCards();
        document.getElementById("rc-explorer-h2").textContent = botName(coin) + " on " + coinLabel(coin) + " — strategy vs. just holding";
        renderPills();
        var rows = forCombo(coin, strategy);
        selected = rows[0] || null;
        renderRegimes(rows);
        renderChart();
    }

    fetch("/data/reality.json").then(function (r) { return r.json(); }).then(function (d) {
        DATA = d;
        var coins = {}; DATA.forEach(function (r) { coins[r.Symbol] = 1; });
        if (!coins["ETHUSDT"]) coin = Object.keys(coins).sort()[0];
        if (strategies().indexOf(strategy) < 0) strategy = strategies()[0];
        renderVerdicts();
        document.querySelectorAll(".rc-bot").forEach(function (el) {
            el.addEventListener("click", function (e) {
                if (e.target.closest("a")) return;
                coin = el.getAttribute("data-coin");
                refresh();
            });
        });
        refresh();
    }).catch(function (e) {
        document.getElementById("rc-regimes").textContent = "Couldn't load results.";
    });
})();
