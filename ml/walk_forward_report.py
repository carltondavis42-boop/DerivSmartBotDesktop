import argparse
import glob
import os
from datetime import datetime

import pandas as pd


def load_logs(log_dir: str) -> pd.DataFrame:
    pattern = os.path.join(log_dir, "trades-*.csv")
    files = sorted(glob.glob(pattern))
    if not files:
        raise SystemExit(f"No trade logs found under {pattern}")

    frames = []
    for path in files:
        try:
            frames.append(pd.read_csv(path))
        except Exception as ex:
            print(f"[WARN] Failed to read {path}: {ex}")

    if not frames:
        raise SystemExit("No usable CSV logs found.")

    df = pd.concat(frames, ignore_index=True)
    df["Time"] = pd.to_datetime(df["Time"], errors="coerce")
    df = df.dropna(subset=["Time"])
    return df


def summarize(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    df["Date"] = df["Time"].dt.date
    df["Win"] = df["Profit"] > 0

    daily = df.groupby("Date").agg(
        trades=("Profit", "count"),
        wins=("Win", "sum"),
        net_pl=("Profit", "sum"),
        avg_pl=("Profit", "mean"),
    )
    daily["win_rate"] = (daily["wins"] / daily["trades"]) * 100.0

    regime = df.groupby("Regime").agg(
        trades=("Profit", "count"),
        wins=("Win", "sum"),
        net_pl=("Profit", "sum"),
        avg_pl=("Profit", "mean"),
    )
    regime["win_rate"] = (regime["wins"] / regime["trades"]) * 100.0

    strategy = df.groupby("Strategy").agg(
        trades=("Profit", "count"),
        wins=("Win", "sum"),
        net_pl=("Profit", "sum"),
        avg_pl=("Profit", "mean"),
    )
    strategy["win_rate"] = (strategy["wins"] / strategy["trades"]) * 100.0

    return daily, regime, strategy


def main():
    parser = argparse.ArgumentParser(description="Walk-forward performance report.")
    parser.add_argument("--log-dir", default="../Data/Trades", help="Directory with trades-*.csv")
    parser.add_argument("--out-dir", default="../Data/ML", help="Directory to write report CSVs")
    args = parser.parse_args()

    df = load_logs(args.log_dir)
    daily, regime, strategy = summarize(df)

    os.makedirs(args.out_dir, exist_ok=True)
    ts = datetime.utcnow().strftime("%Y%m%d_%H%M%S")

    daily_path = os.path.join(args.out_dir, f"walk_forward_daily_{ts}.csv")
    regime_path = os.path.join(args.out_dir, f"walk_forward_regime_{ts}.csv")
    strat_path = os.path.join(args.out_dir, f"walk_forward_strategy_{ts}.csv")

    daily.to_csv(daily_path)
    regime.to_csv(regime_path)
    strategy.to_csv(strat_path)

    print(f"Wrote daily report: {daily_path}")
    print(f"Wrote regime report: {regime_path}")
    print(f"Wrote strategy report: {strat_path}")


if __name__ == "__main__":
    main()
