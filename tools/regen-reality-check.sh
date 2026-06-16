#!/bin/bash
# Regenerate the precomputed Reality Check results consumed by the public /reality-check page.
#
# These JSON files (wwwroot/reality-check/*.json) ship WITH the app and are what the page renders —
# we deliberately don't run backtests on the request path, so the public numbers are deterministic
# and pre-vetted. Run this from the repo root after changing a strategy, a dataset, or the cost model.
#
# Requires: the historical CSVs in data/backtest/ (gitignored; pull with tools/FetchKlines) and a
# Release build (`dotnet build -c Release`).
set -euo pipefail
cd "$(dirname "$0")/.."

OUT=wwwroot/reality-check
mkdir -p "$OUT"
rm -f "$OUT"/*.json

# Realistic round-trip cost for liquid BTC/ETH spot: ~3 bps slippage + the configured taker fee.
SLIP=0.0003
DATASETS="uptrend flat crash-ftx bleed-celsius"
STRATS="Technical TrendFollow BuildEthCycling ExposureController"

n=0
for sym in BTCUSDT ETHUSDT; do
  for ds in $DATASETS; do
    f="data/backtest/${sym}-10s-${ds}.csv"
    [ -f "$f" ] || { echo "MISSING $f — skipping"; continue; }
    for st in $STRATS; do
      out="$OUT/${st}__${sym}-10s-${ds}.json"
      BACKTEST_OUT_JSON="$out" dotnet run -c Release --no-build -- \
        --backtest --symbol "$sym" --data "$f" --starting-qty 1 --strategy "$st" --slippage "$SLIP" \
        >/dev/null 2>&1
      [ -f "$out" ] && n=$((n+1)) || echo "FAILED $st $sym $ds"
    done
  done
done
echo "Regenerated $n Reality Check result files in $OUT"
