import argparse
import glob
import os
from dataclasses import dataclass
from typing import Dict, List, Tuple

import numpy as np
import pandas as pd


@dataclass
class PerfSummary:
    strategy: str
    total_trades: int
    wins: int
    losses: int
    win_rate: float
    total_profit: float
    avg_win: float
    avg_loss: float
    expectancy: float
    max_drawdown: float


def load_trade_logs(log_dir: str) -> pd.DataFrame:
    pattern = os.path.join(log_dir, "trades_*.csv")
    files = sorted(glob.glob(pattern))
    if not files:
        raise SystemExit(f"No trade log files found for pattern: {pattern}")

    frames: List[pd.DataFrame] = []
    for path in files:
        try:
            df = pd.read_csv(path)
            frames.append(df)
        except Exception as ex:
            print(f"[WARN] Failed to read {path}: {ex}")

    if not frames:
        raise SystemExit("No valid CSV logs could be loaded.")

    df_all = pd.concat(frames, ignore_index=True)
    print(f"Loaded {len(df_all)} rows from {len(frames)} files.")
    return df_all


def compute_max_drawdown(equity_curve: np.ndarray) -> float:
    peak = -np.inf
    max_dd = 0.0
    for v in equity_curve:
        if v > peak:
            peak = v
        dd = peak - v
        if dd > max_dd:
            max_dd = dd
    return float(max_dd)


def summarize_strategy(df: pd.DataFrame, strategy: str) -> PerfSummary:
    if df.empty:
        return PerfSummary(
            strategy=strategy,
            total_trades=0,
            wins=0,
            losses=0,
            win_rate=0.0,
            total_profit=0.0,
            avg_win=0.0,
            avg_loss=0.0,
            expectancy=0.0,
            max_drawdown=0.0,
        )

    profits = df["Profit"].fillna(0.0).to_numpy(dtype=float)
    total_trades = len(profits)
    wins_mask = profits > 0
    losses_mask = profits < 0

    wins = int(wins_mask.sum())
    losses = int(losses_mask.sum())
    win_rate = wins / total_trades * 100.0 if total_trades > 0 else 0.0
    total_profit = float(profits.sum())

    avg_win = float(profits[wins_mask].mean()) if wins > 0 else 0.0
    avg_loss = float(profits[losses_mask].mean()) if losses > 0 else 0.0

    # Basic expectancy: p(win)*avg_win + p(loss)*avg_loss
    p_win = wins / total_trades if total_trades > 0 else 0.0
    p_loss = losses / total_trades if total_trades > 0 else 0.0
    expectancy = p_win * avg_win + p_loss * avg_loss

    # Equity curve (cumulative P/L) and max drawdown in profit units
    equity = profits.cumsum()
    max_dd = compute_max_drawdown(equity)

    return PerfSummary(
        strategy=strategy,
        total_trades=total_trades,
        wins=wins,
        losses=losses,
        win_rate=win_rate,
        total_profit=total_profit,
        avg_win=avg_win,
        avg_loss=avg_loss,
        expectancy=expectancy,
        max_drawdown=max_dd,
    )


def run_analysis(df: pd.DataFrame) -> Tuple[PerfSummary, List[PerfSummary]]:
    if "Profit" not in df.columns:
        raise SystemExit("Expected 'Profit' column in trade logs.")

    if "Strategy" not in df.columns:
        df = df.copy()
        df["Strategy"] = df.get("StrategyName", "Unknown")

    overall = summarize_strategy(df, strategy="ALL")

    per_strategy: List[PerfSummary] = []
    for strat, group in df.groupby("Strategy"):
        per_strategy.append(summarize_strategy(group, strategy=str(strat)))

    return overall, per_strategy


def main():
    parser = argparse.ArgumentParser(
        description="Analyze performance from DerivSmartBotDesktop trade logs."
    )
    parser.add_argument(
        "--log-dir",
        type=str,
        default="../Data/Trades",
        help="Directory containing trades_YYYYMMDD.csv files.",
    )
    parser.add_argument(
        "--output-csv",
        type=str,
        default="./models/performance-summary.csv",
        help="Path to write CSV summary.",
    )

    args = parser.parse_args()

    df = load_trade_logs(args.log_dir)
    overall, per_strategy = run_analysis(df)

    rows = [
        {
            "Strategy": overall.strategy,
            "TotalTrades": overall.total_trades,
            "Wins": overall.wins,
            "Losses": overall.losses,
            "WinRate": overall.win_rate,
            "TotalProfit": overall.total_profit,
            "AvgWin": overall.avg_win,
            "AvgLoss": overall.avg_loss,
            "Expectancy": overall.expectancy,
            "MaxDrawdown": overall.max_drawdown,
        }
    ]

    for s in per_strategy:
        rows.append(
            {
                "Strategy": s.strategy,
                "TotalTrades": s.total_trades,
                "Wins": s.wins,
                "Losses": s.losses,
                "WinRate": s.win_rate,
                "TotalProfit": s.total_profit,
                "AvgWin": s.avg_win,
                "AvgLoss": s.avg_loss,
                "Expectancy": s.expectancy,
                "MaxDrawdown": s.max_drawdown,
            }
        )

    out_dir = os.path.dirname(args.output_csv)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    out_df = pd.DataFrame(rows)
    out_df.to_csv(args.output_csv, index=False)
    print(f"Saved performance summary to: {args.output_csv}")
    print(out_df.to_string(index=False))


if __name__ == "__main__":
    main()
