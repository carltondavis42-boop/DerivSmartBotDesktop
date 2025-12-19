import argparse
import os

import pandas as pd


def load_summary(path: str) -> pd.DataFrame:
    if not os.path.exists(path):
        raise SystemExit(f"File not found: {path}")
    df = pd.read_csv(path)
    if "Strategy" not in df.columns:
        raise SystemExit(f"Expected a 'Strategy' column in {path}")
    return df


def compare_runs(
    base: pd.DataFrame,
    candidate: pd.DataFrame,
) -> pd.DataFrame:
    # Merge on Strategy name
    merged = base.merge(
        candidate,
        on="Strategy",
        suffixes=("_base", "_candidate"),
        how="outer",
    )

    # For strategies missing in one side, fill numeric columns with 0
    num_cols = [
        "TotalTrades",
        "Wins",
        "Losses",
        "WinRate",
        "TotalProfit",
        "AvgWin",
        "AvgLoss",
        "Expectancy",
        "MaxDrawdown",
    ]
    for col in num_cols:
        col_b = f"{col}_base"
        col_c = f"{col}_candidate"
        if col_b not in merged.columns:
            merged[col_b] = 0.0
        if col_c not in merged.columns:
            merged[col_c] = 0.0

    # Compute deltas (candidate - base)
    for col in num_cols:
        merged[f"{col}_delta"] = merged[f"{col}_candidate"] - merged[f"{col}_base"]

    return merged


def main():
    parser = argparse.ArgumentParser(
        description=(
            "Compare two performance-summary.csv files "
            "to see how ML / new settings changed results."
        )
    )
    parser.add_argument(
        "--base",
        type=str,
        required=True,
        help="CSV from baseline run (e.g., ML disabled).",
    )
    parser.add_argument(
        "--candidate",
        type=str,
        required=True,
        help="CSV from new run (e.g., ML enabled).",
    )
    parser.add_argument(
        "--output",
        type=str,
        default="./models/performance-diff.csv",
        help="Path to write CSV with differences.",
    )

    args = parser.parse_args()

    base_df = load_summary(args.base)
    cand_df = load_summary(args.candidate)

    diff = compare_runs(base_df, cand_df)

    out_dir = os.path.dirname(args.output) or "."
    os.makedirs(out_dir, exist_ok=True)
    diff.to_csv(args.output, index=False)

    print(f"Saved comparison to: {args.output}")
    # Print a compact view
    cols_to_show = [
        "Strategy",
        "TotalTrades_base",
        "TotalTrades_candidate",
        "WinRate_base",
        "WinRate_candidate",
        "WinRate_delta",
        "TotalProfit_base",
        "TotalProfit_candidate",
        "TotalProfit_delta",
        "Expectancy_base",
        "Expectancy_candidate",
        "Expectancy_delta",
    ]
    existing = [c for c in cols_to_show if c in diff.columns]
    print(diff[existing].to_string(index=False))


if __name__ == "__main__":
    main()
