# ML Training Pipeline for DerivSmartBotDesktop

This folder contains a **simple offline training pipeline** for the market regime
ML model used by the app.

The goal is to train a light-weight multinomial logistic regression model that
predicts the current `MarketRegime` from the trade logs produced by the bot.

## Trade logs

At runtime, the bot writes CSV trade logs via `CsvTradeDataLogger` to:

`Data/Trades/trades_YYYYMMDD.csv` (relative to the application folder).

Each row contains (among others) these columns:

- `Price`
- `Volatility`
- `TrendSlope`
- `RSI`
- `ATR`
- `MarketHeat`
- `Regime`
- `Strategy`
- `Signal`
- `Confidence`
- `Stake`
- `Profit`

## Training the regime model

1. Install Python 3.9+ and create a virtual environment.
2. From this `ml` folder, install dependencies:

   ```bash
   pip install -r requirements.txt
   ```

3. Make sure you have some trade logs under `../Data/Trades`.

4. Run the training script:

   ```bash
   python train_regime_model.py --log-dir "../Data/Trades" --output-dir "./models"
   ```

5. If training succeeds, you will get:

   - `models/regime-linear-v1.json`  → logistic regression weights
   - `models/regime-training-report.txt` → basic metrics

6. Copy `models/regime-linear-v1.json` into the app's data folder so the bot
   can load it at runtime:

   - Create folder: `Data/ML` next to `Data/Trades`
   - Copy the file as: `Data/ML/regime-linear-v1.json`

When this file is present, the app will automatically enable the **ML-based
regime classifier** using `JsonRegimeModel` + `MLMarketRegimeClassifier`. If the
file is missing or cannot be loaded, the bot falls back to the built‑in
heuristic `AiMarketRegimeClassifier`.


## Training the strategy edge model (optional, for Phase 3)

You can also train a simple binary logistic regression model that estimates the
probability that a given trade will be profitable.

1. From this `ml` folder, run:

   ```bash
   python train_edge_model.py --log-dir "../Data/Trades" --output-dir "./models"
   ```

2. Copy `models/edge-linear-v1.json` to the app folder as:

   `Data/ML/edge-linear-v1.json`

If this file exists, the app will use `JsonStrategyEdgeModel` + `MlStrategySelector`
to help choose between competing strategies. If it is missing or cannot be
loaded, the app falls back to the standard `RuleBasedStrategySelector`.


## Performance analysis from live / paper logs (Phase 4)

To get a quick "backtest-style" performance summary from your actual trade logs
(live or demo), you can run:

```bash
cd ml
python analyze_performance.py --log-dir "../Data/Trades" --output-csv "./models/performance-summary.csv"
```

This will:

- Load all `trades_*.csv` files.
- Compute overall and per-strategy stats:
  - Total trades, wins, losses, win rate
  - Total profit, average win, average loss, expectancy
  - Max drawdown (on cumulative P/L)
- Write everything to `./models/performance-summary.csv` and print a table.

You can use this to compare runs with ML regime / ML strategy enabled vs
disabled, and iterate on your model training based on real performance.


## Comparing two runs (Phase 5 – experiment tuning)

Once you have two `performance-summary.csv` files (for example:
- one from a baseline run with ML disabled, and
- one from a run with ML regime/strategy enabled),

you can compare them like this:

```bash
cd ml
python compare_performance.py \
  --base "./models/performance-summary-baseline.csv" \
  --candidate "./models/performance-summary-ml.csv" \
  --output "./models/performance-diff.csv"
```

This will create a `performance-diff.csv` that:

- aligns strategies by name
- shows before/after metrics
- adds *_delta columns (candidate - base) for:
  - WinRate, TotalProfit, Expectancy, MaxDrawdown, etc.

Use this to quickly tell whether the ML changes are actually helping, and by
how much, per strategy and overall.
