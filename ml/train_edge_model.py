import argparse
import glob
import json
import os
from datetime import datetime

import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import classification_report, roc_auc_score


def load_trade_logs(log_dir: str) -> pd.DataFrame:
    pattern = os.path.join(log_dir, "trades_*.csv")
    files = sorted(glob.glob(pattern))
    if not files:
        raise SystemExit(f"No trade log files found under {pattern}")

    frames = []
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


def prepare_edge_dataset(df: pd.DataFrame):
    required_cols = ["Price", "Volatility", "TrendSlope", "Profit"]
    missing = [c for c in required_cols if c not in df.columns]
    if missing:
        raise SystemExit(f"Missing required columns for edge training: {missing}")

    data = df[required_cols].dropna()

    if data.empty:
        raise SystemExit("No usable rows for edge training after filtering.")

    # Binary label: was this trade profitable?
    y = (data["Profit"] > 0.0).astype(int).values

    X = data[["Price", "Volatility", "TrendSlope"]].values.astype(float)
    return X, y


def train_edge_model(X: np.ndarray, y: np.ndarray):
    model = LogisticRegression(
        max_iter=500,
        n_jobs=None,
    )
    model.fit(X, y)
    return model


def evaluate_edge_model(model: LogisticRegression, X: np.ndarray, y: np.ndarray) -> str:
    y_pred = model.predict(X)
    y_prob = model.predict_proba(X)[:, 1]
    report = classification_report(y, y_pred)
    try:
        auc = roc_auc_score(y, y_prob)
        report += f"\nAUC: {auc:.4f}\n"
    except Exception:
        pass
    return report


def export_edge_model(model: LogisticRegression, out_dir: str):
    os.makedirs(out_dir, exist_ok=True)
    config = {
        "created_at": datetime.utcnow().isoformat() + "Z",
        "model_type": "binary_logistic_regression",
        "feature_names": ["Price", "Volatility", "TrendSlope"],
        "coef": model.coef_.tolist(),
        "intercept": model.intercept_.tolist(),
    }

    out_path = os.path.join(out_dir, "edge-linear-v1.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2)

    print(f"Saved edge model to: {out_path}")
    return out_path


def main():
    parser = argparse.ArgumentParser(description="Train ML edge model from trade logs.")
    parser.add_argument("--log-dir", type=str, default="../Data/Trades",
                        help="Directory containing trades_YYYYMMDD.csv files.")
    parser.add_argument("--output-dir", type=str, default="./models",
                        help="Directory to write trained model artifacts.")
    args = parser.parse_args()

    df = load_trade_logs(args.log_dir)
    X, y = prepare_edge_dataset(df)

    print("Training logistic regression for trade outcome (edge)...")
    model = train_edge_model(X, y)

    print("Evaluating on full dataset (for sanity check)...")
    report = evaluate_edge_model(model, X, y)
    print(report)

    os.makedirs(args.output_dir, exist_ok=True)
    report_path = os.path.join(args.output_dir, "edge-training-report.txt")
    with open(report_path, "w", encoding="utf-8") as f:
        f.write(report)
    print(f"Saved edge training report to: {report_path}")

    export_edge_model(model, args.output_dir)


if __name__ == "__main__":
    main()
