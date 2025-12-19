import argparse
import glob
import json
import os
from datetime import datetime
from typing import List, Dict, Tuple

import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import classification_report, roc_auc_score, brier_score_loss


META_COLUMNS = {
    "Time",
    "Symbol",
    "Strategy",
    "Signal",
    "Direction",
    "Regime",
    "Profit",
    "NetResult",
}


META_COLUMNS = {
    "Time",
    "Symbol",
    "Strategy",
    "Signal",
    "Direction",
    "Regime",
    "Profit",
    "NetResult",
}


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


def infer_feature_columns(df: pd.DataFrame) -> List[str]:
    feature_cols: List[str] = []
    for col in df.columns:
        if col in META_COLUMNS:
            continue
        if not pd.api.types.is_numeric_dtype(df[col]):
            continue
        feature_cols.append(col)

    if not feature_cols:
        raise SystemExit("No numeric feature columns found for edge training.")

    return feature_cols


def prepare_edge_dataset(df: pd.DataFrame, feature_cols: List[str]) -> Tuple[np.ndarray, np.ndarray]:
    if "Profit" not in df.columns:
        raise SystemExit("Profit column is required for edge training.")

    data = df.dropna(subset=["Profit"] + feature_cols)
    if data.empty:
        raise SystemExit("No usable rows for edge training after filtering Profit.")

    X = data[feature_cols].values.astype(float)
    y = (data["Profit"] > 0.0).astype(int).values
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
    return os.path.join(out_dir, "edge-linear-v1.json")


def export_per_strategy_models(global_model: LogisticRegression,
                               strategy_models: Dict[str, LogisticRegression],
                               feature_names: List[str],
                               out_dir: str,
                               n_samples_total: int,
                               n_samples_per_strategy: Dict[str, int]):
    os.makedirs(out_dir, exist_ok=True)
    config = {
        "created_at": datetime.utcnow().isoformat() + "Z",
        "model_type": "per_strategy_logistic_regression",
        "feature_names": feature_names,
        "global_model": {
            "coef": global_model.coef_.tolist(),
            "intercept": global_model.intercept_.tolist(),
            "n_samples": n_samples_total,
        },
        "strategies": []
    }

    for name, model in strategy_models.items():
        config["strategies"].append({
            "strategy": name,
            "coef": model.coef_.tolist(),
            "intercept": model.intercept_.tolist(),
            "n_samples": n_samples_per_strategy.get(name, 0),
        })

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
    feature_cols = infer_feature_columns(df)
    X, y = prepare_edge_dataset(df, feature_cols)

    print("Training logistic regression for trade outcome (edge)...")
    model = train_edge_model(X, y)

    print("Evaluating on full dataset (for sanity check)...")
    report = evaluate_edge_model(model, X, y)
    print(report)

    # Calibration (Platt scaling) on global model
    global_probs = model.predict_proba(X)[:, 1]
    global_calibration = _fit_platt_scaler(global_probs, y)

    os.makedirs(args.output_dir, exist_ok=True)
    report_path = os.path.join(args.output_dir, "edge-training-report.txt")
    with open(report_path, "w", encoding="utf-8") as f:
        f.write(report)
    print(f"Saved edge training report to: {report_path}")

    # Train per-strategy models where we have enough data
    strategy_models: Dict[str, LogisticRegression] = {}
    n_samples_per_strategy: Dict[str, int] = {}
    for strategy, group in df.groupby("Strategy"):
        if len(group) < 50:
            continue
        X_s, y_s = prepare_edge_dataset(group, feature_cols)
        lm = train_edge_model(X_s, y_s)
        strategy_models[strategy] = lm
        n_samples_per_strategy[strategy] = len(group)

    export_per_strategy_models(
        global_model=model,
        strategy_models=strategy_models,
        feature_names=feature_cols,
        out_dir=args.output_dir,
        n_samples_total=len(df),
        n_samples_per_strategy=n_samples_per_strategy,
    )


if __name__ == "__main__":
    main()
