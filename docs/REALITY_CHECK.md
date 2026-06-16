# Can you beat "just holding" on Binance? We spent real effort finding out.

**Short answer: on the liquid spot pairs (BTC, ETH), almost certainly not by predicting price — and we have the receipts.**

This is not another signal service. We built a trading bot, tried hard to make it beat buy-and-hold,
and it didn't. Rather than hide that, we're publishing exactly what we tried, on real data, after real
costs — because the entire "make money trading crypto" ecosystem is paid to sell you the opposite.

---

## TL;DR

- We tested four families of strategy against **buy-and-hold (B&H), after fees and slippage**, on
  **real Binance data including actual crashes** (COVID-2020, FTX-2022, the 2024 yen-carry unwind, and
  several slow bear-grinds).
- **Every prediction/timing strategy lost to simply holding.** Trend-following, dip-cycling, and
  crash-timing all underperformed once you pay the costs of trading.
- The only thing that made money **without predicting anything** was **delta-neutral funding carry**
  (~6–10%/yr) — and that's a *yield* product that still loses to holding in a bull market.
- If you take one thing away: **the cost of being clever is usually higher than the cost of being patient.**

---

## How we tested (so you can trust the numbers)

- **Real data, not a simulator's fantasy.** 10-second bars rebuilt from Binance's own 1-second public
  data dumps, plus tick-level trade data for execution realism.
- **Real costs.** Every trade pays a taker fee and slippage. No "frictionless" backtests.
- **Multiple regimes, including crashes.** We deliberately included violent crashes and slow bleeds,
  not just the easy bull-market windows that make any strategy look good.
- **The benchmark is brutal and honest: buy-and-hold.** A strategy only "wins" if it beats doing nothing
  after costs. Most strategies are quietly compared to *cash*, which flatters them. We don't.

---

## The graveyard: what we tried, and how it died

| Strategy family | What it tries to do | Result vs buy-and-hold |
|---|---|---|
| **Coin-cycling** (buy dips, sell bounces to accumulate more coin) | Out-trade the chop | No edge; gains too small to survive fees/slippage |
| **Trend-following** (go long in uptrends, cash in downtrends) | Ride trends, dodge crashes | **Lost in every regime** — whipsawed by noise, paid the spread on each flip |
| **Crash circuit-breaker** (de-risk when price falls fast) | Avoid the big drawdowns | **Conditional and untradeable** — see below |
| **Maker / "capture the spread"** execution | Earn the bid-ask instead of paying it | Spread on BTC/ETH is ~0.5 bp — too thin; retail maker fees = taker fees |

### The most instructive failure: crash-timing

A drawdown circuit-breaker *sounds* obviously good — step aside when the market falls. On real crashes
it actually **helped on violent single-day crashes** (e.g. COVID, the Aug-2024 plunge: +5–14% better than
holding) but **hurt on slow grinding bear markets** (FTX, 2021/2025 bleeds: −1 to −10% worse than holding),
because it sells into dead-cat bounces and buys back right before the next leg down.

We then tested the make-or-break question: *at the moment you must decide to de-risk, can you tell a
"good" crash from a "bad" one?* **No.** The winning and losing cases look identical in every signal
available at decision time (drawdown speed, volatility, momentum). What separates them is the *future*
price path — which is exactly the thing nobody can predict. **Net across all crashes: roughly a wash
versus just holding.**

---

## The one thing that worked (without predicting anything)

**Delta-neutral funding carry**: hold spot, short the perpetual future of equal size. You have *no* price
exposure, and you collect the funding rate that crowded leveraged longs pay every 8 hours.

On 2024 Binance data this paid roughly **+12% (BTC) / +13% (ETH) per year on notional**, positive
**92–96%** of the time. After realistic capital and fees, call it **~6–10%/yr on capital, market-neutral.**

**The honest catch:** this is a *yield* product, not a *growth* product. In 2024 BTC itself rose ~120% —
so simply holding crushed the carry trade. Carry only beats holding in flat or falling markets, it
requires futures + careful margin management (liquidation risk), and the yield shrinks when everyone
piles in. It is real, but it is not a magic money machine.

---

## What this means for you, an actual Binance trader

1. **If you're trading spot to "build coin" or "beat the market" — the data says you're most likely
   paying fees to underperform holding.** That's not an insult; it's what happens to nearly everyone,
   including us, with serious tooling.
2. **Your fees are the silent killer.** Trading at ~0.1% per side, flipping a few times a week, can quietly
   cost you double-digit percent a year versus holding.
3. **The boring options are the winning options:** hold, dollar-cost-average, and — if you want yield
   without gambling on direction — transparent, fully-understood, market-neutral carry.
4. **Be deeply skeptical of anyone selling you signals or "profitable bots."** If a real edge on the most
   liquid market on earth were that easy, they'd run it quietly, not sell it to you.

---

## Why we're giving this away

Because it's true, and almost nobody will tell you for free. We're not selling a course, a signal group,
or a bot subscription.

**If this saved you money — or just saved you from a bad idea — and you'd like to say thanks, a small
donation is genuinely appreciated (but never expected):**

> **ETH / any EVM token:** `<DONATION_ADDRESS_PENDING — ETH wallet, to be supplied by project owner>`

This same address is shown alongside both the **BTC** and **ETH** analyses — wherever the data was
useful to you, that's where you can say thanks. That's the entire business model: be useful, be honest,
and let people who found it valuable decide.

---

### Disclaimer

This is educational analysis of historical data, **not financial advice**. Past results do not predict
future returns. Crypto is volatile and you can lose money, including by holding. Funding-carry involves
leverage and liquidation risk. Do your own research.
