#!/usr/bin/env python3
"""
train_models.py

Offline training script for DerivSmartBotDesktop.

- Reads trade logs from Data/Trades/trades-*.csv
- Infers numeric feature columns automatically
- Trains:
    * Multiclass logistic regression for market regime
    * Per-strategy logistic regression for win probability ("edge")

- Saves:
    * Data/ML/regime-linear-v1.json
    * Data/ML/edge-linear-v1.json

Only overwrites models if validation metrics improve. Keeps a backup of the
last good models in Data/ML/archive/.
"""

import argparse
import json
import os
import glob
from datetime import datetime
from typing import List, Dict, Tuple, Optional

import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import train_test_split
from sklearn.metrics import accuracy_score


# ---------- CONFIG ----------

# Relative paths from this script
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_DIR = os.path.join(BASE_DIR, "Data", "Trades")
ML_DIR = os.path.join(BASE_DIR, "Data", "ML")
ARCHIVE_DIR = os.path.join(ML_DIR, "archive")

# Filenames for saved models
REGIME_MODEL_PATH = os.path.join(ML_DIR, "regime-linear-v1.json")
EDGE_MODEL_PATH = os.path.join(ML_DIR, "edge-linear-v1.json")
METRICS_PATH = os.path.join(ML_DIR, "metrics.json")

# Minimum samples to train per-strategy model
MIN_SAMPLES_PER_STRATEGY = 50
MIN_TOTAL_SAMPLES = 200

# Name of key columns as written by CsvTradeDataLogger
# If your logger uses different names, adjust these.
COL_TIME = "Time"
COL_SYMBOL = "Symbol"
COL_STRATEGY = "Strategy"
COL_PROFIT = "Profit"
COL_REGIME = "Regime"       # string name of MarketRegime enum
COL_STAKE = "Stake"
COL_CONFIDENCE = "Confidence"
COL_DIRECTION = "Direction"

# Columns that are *not* features (metadata)
META_COLUMNS = {
    COL_TIME,
    COL_SYMBOL,
    COL_STRATEGY,
    COL_PROFIT,
    COL_REGIME,
    COL_STAKE,
    COL_CONFIDENCE,
    COL_DIRECTION,
}

# Some regime labels we expect
# (your logs may include a subset of these)
KNOWN_REGIMES = [
    "TrendingUp",
    "TrendingDown",
    "RangingLowVol",
    "RangingHighVol",
    "VolatileChoppy",
    "Unknown",
]

# ---------- HELPERS ----------

def load_all_logs(log_dir: str) -> pd.DataFrame:
    pattern = os.path.join(log_dir, "trades-*.csv")
    files = sorted(glob.glob(pattern))
    if not files:
        raise FileNotFoundError(
            f"No trade logs found matching {pattern}. "
            f"Let the bot run to generate Data/Trades/trades-*.csv first."
        )

    dfs = []
    for f in files:
        try:
            df = pd.read_csv(f)
            if not df.empty:
                dfs.append(df)
        except Exception as e:
            print(f"WARNING: failed to read {f}: {e}")

    if not dfs:
        raise RuntimeError("No usable trade rows found in any trades-*.csv files.")

    all_df = pd.concat(dfs, ignore_index=True)
    print(f"Loaded {len(all_df)} rows from {len(files)} files.")
    return all_df


def infer_feature_columns(df: pd.DataFrame) -> List[str]:
    """
    Treat any numeric column that is not in META_COLUMNS as a feature.
    This matches how FeatureVector is logged: numeric features become numeric columns.
    """
    feature_cols = []
    for col in df.columns:
        if col in META_COLUMNS:
            continue
        if pd.api.types.is_numeric_dtype(df[col]):
            feature_cols.append(col)

    if not feature_cols:
        raise RuntimeError("No numeric feature columns found. "
                           "Check your CSV headers or logger implementation.")

    print("Using feature columns:", feature_cols)
    return feature_cols


def prepare_regime_data(df: pd.DataFrame, feature_cols: List[str]):
    """
    Prepare X, y for regime classifier.
    - y is string regime label, excluding null / empty.
    """
    if COL_REGIME not in df.columns:
        raise RuntimeError(
            f"Regime column '{COL_REGIME}' not found in logs. "
            "Ensure CsvTradeDataLogger writes a Regime column from CurrentDiagnostics.Regime."
        )

    reg_df = df.copy()

    # Drop rows with missing regime
    reg_df = reg_df.dropna(subset=[COL_REGIME])
    reg_df = reg_df[reg_df[COL_REGIME].astype(str).str.strip() != ""]
    if reg_df.empty:
        raise RuntimeError("No rows with non-empty Regime label for training.")

    X = reg_df[feature_cols].values.astype(float)
    y = reg_df[COL_REGIME].astype(str).values

    print(f"Regime training set: {X.shape[0]} samples, {X.shape[1]} features.")
    return X, y


def train_regime_model(X: np.ndarray, y: np.ndarray) -> Dict:
    """
    Train a multinomial logistic regression:
        P(regime | features)
    Returns JSON-serializable dict.
    """
    # Restrict to regimes we know (if present)
    unique_labels = sorted(set(y))
    print("Unique regimes in data:", unique_labels)

    # You can optionally filter to exclude "Unknown" from training
    # (but keep it in the mapping so C# can handle it)
    # For now we keep all labels present in data.
    clf = LogisticRegression(
        multi_class="multinomial",
        solver="lbfgs",
        max_iter=1000
    )
    clf.fit(X, y)

    classes = clf.classes_.tolist()
    coefs = clf.coef_.tolist()       # shape: [n_classes, n_features]
    intercepts = clf.intercept_.tolist()

    model_dict = {
        "model_type": "multinomial_logistic_regression",
        "feature_names": [],  # filled later
        "classes": classes,
        "coefficients": coefs,
        "intercepts": intercepts,
    }

    return model_dict


def prepare_edge_data(df: pd.DataFrame, feature_cols: List[str]):
    """
    Prepare data grouped by Strategy for win-probability models.
    y = 1 if Profit > 0, else 0.
    Returns: dict[strategy] -> (X, y)
    """
    if COL_STRATEGY not in df.columns:
        raise RuntimeError(
            f"Strategy column '{COL_STRATEGY}' not found in logs."
        )
    if COL_PROFIT not in df.columns:
        raise RuntimeError(
            f"Profit column '{COL_PROFIT}' not found in logs."
        )

    # Only keep rows with finite profit & valid features
    df = df.copy()
    df = df.dropna(subset=[COL_STRATEGY, COL_PROFIT])
    for col in feature_cols:
        df = df.dropna(subset=[col])

    if df.empty:
        raise RuntimeError("No rows with complete Strategy/Profit/features for edge training.")

    # Binary label: win = 1, loss = 0
    df["LabelWin"] = (df[COL_PROFIT] > 0).astype(int)

    per_strategy = {}
    for strategy_name, g in df.groupby(COL_STRATEGY):
        if len(g) < MIN_SAMPLES_PER_STRATEGY:
            print(f"Skipping strategy '{strategy_name}' ({len(g)} samples < {MIN_SAMPLES_PER_STRATEGY}).")
            continue

        X = g[feature_cols].values.astype(float)
        y = g["LabelWin"].values.astype(int)
        per_strategy[strategy_name] = (X, y)
        print(f"Edge training set for '{strategy_name}': {X.shape[0]} samples.")

    if not per_strategy:
        print("WARNING: no strategy has enough samples for edge model. "
              "Edge JSON will be empty (selector will fall back to rule-based).")

    return per_strategy


def train_edge_models(per_strategy: Dict[str, tuple]) -> Dict:
    """
    Train a binary logistic regression per strategy:
        P(win | features, strategy)
    Returns JSON-serializable dict.
    """
    strategies_dict = {}

    for strategy_name, (X, y) in per_strategy.items():
        clf = LogisticRegression(
            solver="lbfgs",
            max_iter=1000
        )
        clf.fit(X, y)

        coef = clf.coef_[0].tolist()      # shape: [n_features]
        intercept = float(clf.intercept_[0])

        strategies_dict[strategy_name] = {
            "coef": coef,
            "intercept": intercept,
        }

    model_dict = {
        "model_type": "per_strategy_logistic_regression",
        "feature_names": [],  # filled later
        "strategies": strategies_dict,
    }

    return model_dict


def evaluate_regime_model(X: np.ndarray, y: np.ndarray, min_total: int) -> float:
    if X.shape[0] < min_total:
        raise RuntimeError(f"Not enough samples for regime model ({X.shape[0]} < {min_total}).")

    X_train, X_test, y_train, y_test = train_test_split(
        X, y, test_size=0.2, random_state=42, stratify=y
    )

    clf = LogisticRegression(
        multi_class="multinomial",
        solver="lbfgs",
        max_iter=1000
    )
    clf.fit(X_train, y_train)
    preds = clf.predict(X_test)
    return float(accuracy_score(y_test, preds))


def evaluate_edge_models(per_strategy: Dict[str, tuple], min_samples: int) -> Optional[float]:
    if not per_strategy:
        return None

    scores = []
    weights = []

    for _, (X, y) in per_strategy.items():
        if len(y) < min_samples:
            continue

        X_train, X_test, y_train, y_test = train_test_split(
            X, y, test_size=0.2, random_state=42, stratify=y
        )
        clf = LogisticRegression(solver="lbfgs", max_iter=1000)
        clf.fit(X_train, y_train)
        preds = clf.predict(X_test)
        acc = float(accuracy_score(y_test, preds))
        scores.append(acc)
        weights.append(len(y))

    if not scores:
        return None

    weighted = sum(s * w for s, w in zip(scores, weights)) / sum(weights)
    return float(weighted)


def load_metrics(path: str) -> Optional[Dict]:
    if not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def should_update_models(new_score: float, old_metrics: Optional[Dict], force: bool) -> bool:
    if force:
        return True
    if not old_metrics:
        return True
    old_score = old_metrics.get("composite_score", 0.0)
    return new_score > old_score


def archive_existing_models(paths: List[str], archive_dir: str):
    os.makedirs(archive_dir, exist_ok=True)
    ts = datetime.utcnow().strftime("%Y%m%d_%H%M%S")
    for path in paths:
        if os.path.exists(path):
            name = os.path.basename(path)
            os.replace(path, os.path.join(archive_dir, f"{ts}_{name}"))


# ---------- MAIN ----------

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--log-dir", default=LOG_DIR)
    parser.add_argument("--ml-dir", default=ML_DIR)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--min-total", type=int, default=MIN_TOTAL_SAMPLES)
    parser.add_argument("--min-per-strategy", type=int, default=MIN_SAMPLES_PER_STRATEGY)
    args = parser.parse_args()

    log_dir = args.log_dir
    ml_dir = args.ml_dir
    regime_model_path = os.path.join(ml_dir, "regime-linear-v1.json")
    edge_model_path = os.path.join(ml_dir, "edge-linear-v1.json")
    metrics_path = os.path.join(ml_dir, "metrics.json")
    min_total_samples = args.min_total
    min_per_strategy = args.min_per_strategy

    os.makedirs(ml_dir, exist_ok=True)

    # 1) Load data
    df = load_all_logs(log_dir)

    # 2) Infer feature columns
    feature_cols = infer_feature_columns(df)

    # 3) Train regime model
    X_reg, y_reg = prepare_regime_data(df, feature_cols)
    per_strategy = prepare_edge_data(df, feature_cols)

    regime_acc = evaluate_regime_model(X_reg, y_reg, min_total_samples)
    edge_acc = evaluate_edge_models(per_strategy, min_per_strategy)

    composite = regime_acc if edge_acc is None else (0.6 * regime_acc + 0.4 * edge_acc)

    metrics = {
        "timestamp": datetime.utcnow().isoformat() + "Z",
        "regime_accuracy": regime_acc,
        "edge_accuracy": edge_acc,
        "composite_score": composite,
        "samples": int(len(df)),
    }

    existing = load_metrics(metrics_path)
    if not should_update_models(composite, existing, args.force):
        print(f"No improvement. New={composite:.4f}, Old={existing.get('composite_score', 0.0):.4f}")
        return

    regime_json = train_regime_model(X_reg, y_reg)
    regime_json["feature_names"] = feature_cols

    edge_json = train_edge_models(per_strategy)
    edge_json["feature_names"] = feature_cols

    archive_existing_models(
        [regime_model_path, edge_model_path, metrics_path],
        os.path.join(ml_dir, "archive")
    )

    with open(regime_model_path, "w", encoding="utf-8") as f:
        json.dump(regime_json, f, indent=2)
    with open(edge_model_path, "w", encoding="utf-8") as f:
        json.dump(edge_json, f, indent=2)
    with open(metrics_path, "w", encoding="utf-8") as f:
        json.dump(metrics, f, indent=2)

    print(f"Saved regime model to {regime_model_path}")
    print(f"Saved edge model to {edge_model_path}")
    print(f"Saved metrics to {metrics_path}")


if __name__ == "__main__":
    main()
