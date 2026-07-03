"""
analyze_plant_growth.py
=======================
Script phan tich toc do sinh truong cay dua tren du lieu CSV export.
Danh cho project CPS Plant Digital Twin.

Cach dung:
    python analyze_plant_growth.py --csv path/to/data.csv
    python analyze_plant_growth.py --csv path/to/data.csv --height-col "Plant Height" --time-col "_time"
    python analyze_plant_growth.py --csv path/to/data.csv --smoothing-window 5 --output-dir results/plots

Author: CPS Plant Digital Twin Project
"""

import argparse
import io
import os
import sys
import warnings
from pathlib import Path

# ── Fix Windows console encoding (emoji, Unicode) ─────────────────────────────
if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding and sys.stderr.encoding.lower() not in ("utf-8", "utf8"):
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

import numpy as np
import pandas as pd
import matplotlib
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
import matplotlib.gridspec as gridspec
from matplotlib.ticker import MaxNLocator

warnings.filterwarnings("ignore")
matplotlib.rcParams["font.family"] = "DejaVu Sans"
matplotlib.rcParams["figure.dpi"] = 150
matplotlib.rcParams["savefig.dpi"] = 150

# ─────────────────────────────────────────────────────────────────────────────
# Màu sắc và theme
# ─────────────────────────────────────────────────────────────────────────────
COLORS = {
    "bg":           "#0D1117",
    "panel":        "#161B22",
    "border":       "#30363D",
    "text":         "#E6EDF3",
    "text_muted":   "#8B949E",
    "green":        "#3FB950",
    "green_light":  "#56D364",
    "yellow":       "#E3B341",
    "orange":       "#F0883E",
    "red":          "#F85149",
    "blue":         "#58A6FF",
    "purple":       "#BC8CFF",
    "cyan":         "#79C0FF",
    "accent":       "#1F6FEB",
    "fast":         "#56D364",
    "normal":       "#58A6FF",
    "slow":         "#E3B341",
    "stopped":      "#F85149",
}

STAGE_COLORS = {
    "fast_growth":   COLORS["fast"],
    "normal_growth": COLORS["normal"],
    "slow_growth":   COLORS["slow"],
    "stopped":       COLORS["stopped"],
}

STAGE_LABELS = {
    "fast_growth":   "Tăng nhanh (Fast)",
    "normal_growth": "Bình thường (Normal)",
    "slow_growth":   "Tăng chậm (Slow)",
    "stopped":       "Dừng/Âm (Stopped)",
}

# Tên cột chiều cao cây (tự nhận diện)
HEIGHT_CANDIDATES = [
    "plant_height_cm",
    "Plant Height",
    "plant_height",
    "PlantHeight",
    "plantHeight",
    "height_cm",
    "height",
    "Height",
]

# Tên cột thời gian (tự nhận diện)
TIME_CANDIDATES = [
    "_time",
    "time",
    "timestamp",
    "Timestamp",
    "datetime",
    "date",
    "Date",
    "Time",
]

# Cột ngày sau gieo
DAYS_CANDIDATES = [
    "days_after_planting",
    "days",
    "day",
    "Days",
]

# Cột môi trường
ENV_CANDIDATES = {
    "temperature":    ["temperature", "temp", "Temperature", "air_temperature"],
    "air_humidity":   ["air_humidity", "humidity", "Humidity", "rh"],
    "soil_moisture":  ["soil_moisture", "soilMoisture", "moisture", "Moisture"],
    "lux":            ["lux", "Lux", "light", "Light", "light_intensity"],
}


# ─────────────────────────────────────────────────────────────────────────────
# Hàm tiện ích: tự tìm cột phù hợp
# ─────────────────────────────────────────────────────────────────────────────

def find_column(df: pd.DataFrame, candidates: list[str], label: str = "column") -> str | None:
    """
    Tìm tên cột đầu tiên trong danh sách candidates xuất hiện trong df.columns.
    Trả về tên cột thực tế hoặc None nếu không tìm thấy.
    """
    cols_lower = {c.lower(): c for c in df.columns}
    for cand in candidates:
        # Khớp chính xác
        if cand in df.columns:
            return cand
        # Khớp không phân biệt hoa thường
        if cand.lower() in cols_lower:
            return cols_lower[cand.lower()]
    # Tìm kiếm từng phần (partial match)
    for cand in candidates:
        for col in df.columns:
            if cand.lower() in col.lower():
                return col
    return None


def find_env_columns(df: pd.DataFrame) -> dict[str, str]:
    """
    Tự tìm các cột môi trường trong DataFrame.
    Trả về dict { tên_chuẩn: tên_cột_thực_tế }.
    """
    found = {}
    for std_name, candidates in ENV_CANDIDATES.items():
        col = find_column(df, candidates, std_name)
        if col is not None:
            found[std_name] = col
    return found


# ─────────────────────────────────────────────────────────────────────────────
# Hàm đọc và làm sạch dữ liệu
# ─────────────────────────────────────────────────────────────────────────────

def load_and_clean(
    csv_path: str,
    height_col_hint: str | None = None,
    time_col_hint: str | None = None,
) -> tuple[pd.DataFrame, str, str]:
    """
    Đọc CSV, làm sạch dữ liệu, trả về:
    (df_clean, height_col_name, time_col_name)
    """
    print(f"\n📂  Đang đọc file: {csv_path}")
    try:
        df = pd.read_csv(csv_path)
    except Exception as e:
        sys.exit(f"❌  Không thể đọc file CSV: {e}")

    print(f"    → {len(df)} dòng, {len(df.columns)} cột")
    print(f"    → Các cột: {list(df.columns)}")

    # ── Tìm cột chiều cao ──────────────────────────────────────────────────
    candidates_height = ([height_col_hint] if height_col_hint else []) + HEIGHT_CANDIDATES
    height_col = find_column(df, candidates_height, "plant height")
    if height_col is None:
        sys.exit(
            "❌  Không tìm thấy cột chiều cao cây.\n"
            "    Hãy chỉ định bằng --height-col \"tên_cột\".\n"
            f"    Các cột hiện có: {list(df.columns)}"
        )
    print(f"    → Cột chiều cao: '{height_col}'")

    # ── Tìm cột thời gian ──────────────────────────────────────────────────
    candidates_time = ([time_col_hint] if time_col_hint else []) + TIME_CANDIDATES
    time_col = find_column(df, candidates_time, "timestamp")

    # Tìm cột days_after_planting
    days_col = find_column(df, DAYS_CANDIDATES, "days_after_planting")

    # Tìm cột epoch planting start (nếu có)
    epoch_col = find_column(df, ["planting_start_epoch", "planting_start"], "planting_start_epoch")

    # ── Chuyển đổi kiểu dữ liệu ───────────────────────────────────────────
    df[height_col] = pd.to_numeric(df[height_col], errors="coerce")

    # ── Xử lý thời gian ───────────────────────────────────────────────────
    if time_col is not None:
        print(f"    → Cột thời gian: '{time_col}'")
        df[time_col] = pd.to_datetime(df[time_col], errors="coerce", utc=True)
        df = df.dropna(subset=[time_col])
        df = df.sort_values(time_col).reset_index(drop=True)

        # Tính days_after_planting từ timestamp nếu chưa có
        if days_col is None:
            if epoch_col is not None and epoch_col in df.columns:
                # Dùng planting_start_epoch (Unix ms hoặc seconds)
                epoch_val = pd.to_numeric(df[epoch_col].iloc[0], errors="coerce")
                if not np.isnan(epoch_val):
                    # Tự detect ms vs seconds
                    if epoch_val > 1e12:
                        start_ts = pd.Timestamp(epoch_val, unit="ms", tz="UTC")
                    else:
                        start_ts = pd.Timestamp(epoch_val, unit="s", tz="UTC")
                    df["days_after_planting"] = (df[time_col] - start_ts).dt.total_seconds() / 86400
                    days_col = "days_after_planting"
                    print(f"    → Tính days_after_planting từ planting_start_epoch")
            else:
                # Tính từ timestamp đầu tiên trong dữ liệu
                t0 = df[time_col].iloc[0]
                df["days_after_planting"] = (df[time_col] - t0).dt.total_seconds() / 86400
                days_col = "days_after_planting"
                print(f"    → Tính days_after_planting từ timestamp đầu tiên")
    elif days_col is not None:
        df[days_col] = pd.to_numeric(df[days_col], errors="coerce")
        df = df.sort_values(days_col).reset_index(drop=True)
        print(f"    → Dùng cột ngày: '{days_col}'")
        time_col = days_col
    else:
        # Không có cả time lẫn days → dùng index
        print("    ⚠️  Không tìm thấy cột thời gian. Dùng chỉ số hàng làm trục X.")
        df["row_index"] = np.arange(len(df))
        time_col = "row_index"
        df["days_after_planting"] = np.arange(len(df), dtype=float)
        days_col = "days_after_planting"

    # Đảm bảo days_after_planting tồn tại
    if "days_after_planting" not in df.columns:
        if days_col in df.columns:
            df["days_after_planting"] = pd.to_numeric(df[days_col], errors="coerce")
        else:
            df["days_after_planting"] = np.arange(len(df), dtype=float)

    # ── Loại bỏ dòng không hợp lệ ─────────────────────────────────────────
    n_before = len(df)

    # Bỏ dòng thiếu chiều cao
    df = df.dropna(subset=[height_col])

    # Loại giá trị âm
    df = df[df[height_col] >= 0]

    # Loại outlier: chiều cao vượt quá IQR * 3
    q1 = df[height_col].quantile(0.25)
    q3 = df[height_col].quantile(0.75)
    iqr = q3 - q1
    upper_bound = q3 + 3.0 * iqr
    df = df[df[height_col] <= upper_bound]

    # Loại nhảy bất thường: delta_height quá lớn trong 1 bước
    if len(df) > 2:
        delta_h = df[height_col].diff().abs()
        max_normal_jump = delta_h.quantile(0.95) * 5
        if max_normal_jump > 0:
            outlier_jump_mask = (delta_h > max_normal_jump) & (delta_h > 1.0)
            df = df[~outlier_jump_mask]

    df = df.reset_index(drop=True)
    n_removed = n_before - len(df)
    if n_removed > 0:
        print(f"    → Đã loại {n_removed} dòng không hợp lệ/outlier")
    print(f"    → Dữ liệu sạch: {len(df)} dòng")

    if len(df) < 3:
        sys.exit("❌  Không đủ dữ liệu để phân tích (cần ít nhất 3 điểm).")

    # Đổi tên chuẩn hoá
    df = df.rename(columns={height_col: "plant_height_cm"})
    height_col = "plant_height_cm"

    return df, height_col, time_col


# ─────────────────────────────────────────────────────────────────────────────
# Tính tốc độ tăng trưởng
# ─────────────────────────────────────────────────────────────────────────────

def compute_growth_rate(df: pd.DataFrame, days_col: str = "days_after_planting",
                        smoothing_window: int = 3) -> pd.DataFrame:
    """
    Tính growth_rate_cm_per_day = delta_height / delta_days.
    Áp dụng rolling mean để làm mượt.
    Phân loại trạng thái sinh trưởng.
    """
    df = df.copy()
    days = df[days_col].values.astype(float)
    heights = df["plant_height_cm"].values.astype(float)

    # Delta
    delta_h = np.diff(heights, prepend=heights[0])
    delta_t = np.diff(days, prepend=days[0])

    # Tránh chia cho 0
    delta_t = np.where(np.abs(delta_t) < 1e-9, np.nan, delta_t)
    raw_rate = delta_h / delta_t
    raw_rate[0] = raw_rate[1] if len(raw_rate) > 1 else 0.0

    df["growth_rate_raw_cm_per_day"] = raw_rate

    # Smoothing
    win = max(2, smoothing_window)
    df["growth_rate_cm_per_day"] = (
        df["growth_rate_raw_cm_per_day"]
        .rolling(window=win, center=True, min_periods=1)
        .mean()
    )

    # Phân loại trạng thái sinh trưởng
    rate = df["growth_rate_cm_per_day"]
    q25 = rate.quantile(0.25)
    q75 = rate.quantile(0.75)

    def classify(r):
        if pd.isna(r) or r <= 0.001:
            return "stopped"
        elif r < q25:
            return "slow_growth"
        elif r >= q75:
            return "fast_growth"
        else:
            return "normal_growth"

    df["growth_status"] = df["growth_rate_cm_per_day"].apply(classify)
    return df


# ─────────────────────────────────────────────────────────────────────────────
# Hàm thiết lập style cho figure
# ─────────────────────────────────────────────────────────────────────────────

def apply_dark_style(fig, axes_list):
    """Áp dụng dark theme cho figure và danh sách axes."""
    fig.patch.set_facecolor(COLORS["bg"])
    for ax in axes_list:
        if ax is None:
            continue
        ax.set_facecolor(COLORS["panel"])
        ax.tick_params(colors=COLORS["text_muted"], which="both")
        ax.xaxis.label.set_color(COLORS["text"])
        ax.yaxis.label.set_color(COLORS["text"])
        ax.title.set_color(COLORS["text"])
        for spine in ax.spines.values():
            spine.set_edgecolor(COLORS["border"])
        ax.grid(True, color=COLORS["border"], linestyle="--", linewidth=0.6, alpha=0.7)


def save_fig(fig, path: Path, chart_name: str):
    """Lưu figure ra file và in thông báo."""
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    print(f"    ✅  Đã lưu: {path}")


# ─────────────────────────────────────────────────────────────────────────────
# Biểu đồ 1: Plant Height theo thời gian
# ─────────────────────────────────────────────────────────────────────────────

def plot_height_over_time(df: pd.DataFrame, x_col: str, output_dir: Path):
    fig, ax = plt.subplots(figsize=(12, 6))
    apply_dark_style(fig, [ax])

    x = df[x_col]
    y = df["plant_height_cm"]

    # Vùng fill dưới đường cong
    ax.fill_between(x, y, alpha=0.15, color=COLORS["green"])
    ax.plot(x, y, color=COLORS["green_light"], linewidth=2.5, label="Plant Height (cm)", zorder=3)
    ax.scatter(x, y, color=COLORS["green"], s=20, zorder=4, alpha=0.7)

    # Đánh dấu min/max
    idx_max = y.idxmax()
    idx_min = y.idxmin()
    ax.annotate(
        f"  Max: {y[idx_max]:.2f} cm",
        xy=(x[idx_max], y[idx_max]),
        color=COLORS["yellow"],
        fontsize=9, fontweight="bold",
    )
    ax.annotate(
        f"  Min: {y[idx_min]:.2f} cm",
        xy=(x[idx_min], y[idx_min]),
        color=COLORS["cyan"],
        fontsize=9, fontweight="bold",
    )

    x_label = "Days After Planting" if "days" in x_col.lower() else x_col
    ax.set_xlabel(x_label, fontsize=11)
    ax.set_ylabel("Plant Height (cm)", fontsize=11)
    ax.set_title("🌱  Plant Height Over Time — CPS Plant Digital Twin", fontsize=14, fontweight="bold", pad=15)
    ax.legend(facecolor=COLORS["panel"], edgecolor=COLORS["border"], labelcolor=COLORS["text"])

    path = output_dir / "01_plant_height_over_time.png"
    save_fig(fig, path, "01")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# Biểu đồ 2: Growth Rate theo thời gian
# ─────────────────────────────────────────────────────────────────────────────

def plot_growth_rate(df: pd.DataFrame, x_col: str, output_dir: Path):
    fig, ax = plt.subplots(figsize=(12, 6))
    apply_dark_style(fig, [ax])

    x = df[x_col]
    raw = df["growth_rate_raw_cm_per_day"]
    smooth = df["growth_rate_cm_per_day"]

    # Tô màu vùng theo trạng thái
    status = df["growth_status"]
    for i in range(len(df) - 1):
        color = STAGE_COLORS.get(status.iloc[i], COLORS["normal"])
        ax.axvspan(x.iloc[i], x.iloc[i + 1], alpha=0.12, color=color, linewidth=0)

    # Đường raw và smoothed
    ax.plot(x, raw, color=COLORS["text_muted"], linewidth=1.0, alpha=0.5,
            linestyle="--", label="Raw rate")
    ax.fill_between(x, smooth, alpha=0.18, color=COLORS["blue"])
    ax.plot(x, smooth, color=COLORS["blue"], linewidth=2.5, label="Smoothed rate")

    # Đường tham chiếu 0
    ax.axhline(0, color=COLORS["red"], linewidth=1.0, linestyle=":", alpha=0.8)

    # Legend trạng thái
    patches = [
        mpatches.Patch(color=STAGE_COLORS[k], label=STAGE_LABELS[k], alpha=0.7)
        for k in STAGE_COLORS
    ]
    leg1 = ax.legend(facecolor=COLORS["panel"], edgecolor=COLORS["border"],
                     labelcolor=COLORS["text"], loc="upper right")
    ax.legend(handles=patches, facecolor=COLORS["panel"], edgecolor=COLORS["border"],
              labelcolor=COLORS["text"], loc="upper left", fontsize=8)
    ax.add_artist(leg1)

    x_label = "Days After Planting" if "days" in x_col.lower() else x_col
    ax.set_xlabel(x_label, fontsize=11)
    ax.set_ylabel("Growth Rate (cm/day)", fontsize=11)
    ax.set_title("📈  Growth Rate Over Time — CPS Plant Digital Twin", fontsize=14, fontweight="bold", pad=15)

    path = output_dir / "02_growth_rate_over_time.png"
    save_fig(fig, path, "02")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# Biểu đồ 3: Height vs Days scatter + trend line
# ─────────────────────────────────────────────────────────────────────────────

def plot_height_vs_days(df: pd.DataFrame, output_dir: Path):
    if "days_after_planting" not in df.columns:
        print("    ⚠️  Bỏ qua biểu đồ 03 (không có cột days_after_planting)")
        return None

    fig, ax = plt.subplots(figsize=(10, 6))
    apply_dark_style(fig, [ax])

    x = df["days_after_planting"].values
    y = df["plant_height_cm"].values

    # Tô màu scatter theo growth_status
    color_map = [STAGE_COLORS.get(s, COLORS["normal"]) for s in df["growth_status"]]
    ax.scatter(x, y, c=color_map, s=50, alpha=0.85, zorder=3, edgecolors="none")

    # Trend line (polynomial degree 3)
    try:
        if len(x) >= 4:
            deg = min(3, len(x) - 1)
            coeffs = np.polyfit(x, y, deg)
            x_fit = np.linspace(x.min(), x.max(), 200)
            y_fit = np.polyval(coeffs, x_fit)
            ax.plot(x_fit, y_fit, color=COLORS["yellow"], linewidth=2.0,
                    linestyle="-", label=f"Trend (poly deg={deg})")
    except Exception:
        pass

    patches = [
        mpatches.Patch(color=STAGE_COLORS[k], label=STAGE_LABELS[k], alpha=0.9)
        for k in STAGE_COLORS
    ]
    ax.legend(handles=patches, facecolor=COLORS["panel"], edgecolor=COLORS["border"],
              labelcolor=COLORS["text"], fontsize=8)

    ax.set_xlabel("Days After Planting", fontsize=11)
    ax.set_ylabel("Plant Height (cm)", fontsize=11)
    ax.set_title("🔵  Height vs Days After Planting — CPS Plant Digital Twin",
                 fontsize=14, fontweight="bold", pad=15)

    path = output_dir / "03_height_vs_days_scatter.png"
    save_fig(fig, path, "03")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# Biểu đồ 4: Correlation Heatmap
# ─────────────────────────────────────────────────────────────────────────────

def plot_correlation_heatmap(df: pd.DataFrame, env_map: dict, output_dir: Path):
    """
    Vẽ heatmap tương quan giữa chiều cao cây, growth rate, và các yếu tố môi trường.
    Dùng matplotlib thuần (không cần seaborn).
    """
    # Chọn các cột có trong df
    cols = ["plant_height_cm", "growth_rate_cm_per_day"]
    for std, real in env_map.items():
        if real in df.columns:
            df_temp = df.rename(columns={real: std})
            df[std] = df_temp[std]
            if std not in cols:
                cols.append(std)

    # Lọc cột số thực sự có dữ liệu
    avail_cols = [c for c in cols if c in df.columns and df[c].notna().sum() > 2]
    if len(avail_cols) < 3:
        print("    ⚠️  Không đủ dữ liệu môi trường để vẽ heatmap (cần ít nhất 3 cột số).")
        return None

    corr = df[avail_cols].corr()

    # Nhãn hiển thị
    label_map = {
        "plant_height_cm": "Height (cm)",
        "growth_rate_cm_per_day": "Growth Rate",
        "temperature": "Temperature",
        "air_humidity": "Air Humidity",
        "soil_moisture": "Soil Moisture",
        "lux": "Light (Lux)",
    }
    labels = [label_map.get(c, c) for c in avail_cols]
    n = len(labels)

    fig, ax = plt.subplots(figsize=(max(7, n * 1.2), max(6, n * 1.0)))
    apply_dark_style(fig, [ax])

    # Vẽ heatmap
    data = corr.values
    cmap = plt.cm.RdYlGn
    im = ax.imshow(data, cmap=cmap, vmin=-1, vmax=1, aspect="auto")

    ax.set_xticks(np.arange(n))
    ax.set_yticks(np.arange(n))
    ax.set_xticklabels(labels, rotation=35, ha="right", fontsize=9, color=COLORS["text"])
    ax.set_yticklabels(labels, fontsize=9, color=COLORS["text"])

    # Ghi số vào ô
    for i in range(n):
        for j in range(n):
            val = data[i, j]
            text_color = "black" if abs(val) > 0.5 else COLORS["text"]
            ax.text(j, i, f"{val:.2f}", ha="center", va="center",
                    fontsize=9, fontweight="bold", color=text_color)

    # Colorbar
    cbar = fig.colorbar(im, ax=ax, fraction=0.04, pad=0.04)
    cbar.ax.tick_params(colors=COLORS["text_muted"])
    cbar.set_label("Correlation", color=COLORS["text"], fontsize=10)

    ax.set_title("🔥  Correlation Heatmap — Plant Growth & Environment Factors",
                 fontsize=13, fontweight="bold", pad=15)
    fig.tight_layout()

    path = output_dir / "04_correlation_heatmap.png"
    save_fig(fig, path, "04")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# Biểu đồ 5: Growth Stage Summary Bar Chart
# ─────────────────────────────────────────────────────────────────────────────

def plot_growth_stage_summary(df: pd.DataFrame, output_dir: Path):
    stages_order = ["fast_growth", "normal_growth", "slow_growth", "stopped"]
    counts = df["growth_status"].value_counts()
    values = [counts.get(s, 0) for s in stages_order]
    colors = [STAGE_COLORS[s] for s in stages_order]
    labels = [STAGE_LABELS[s] for s in stages_order]

    fig, ax = plt.subplots(figsize=(10, 5))
    apply_dark_style(fig, [ax])

    bars = ax.bar(labels, values, color=colors, width=0.55, zorder=3,
                  edgecolor=COLORS["border"], linewidth=0.8)

    # Ghi số lên đầu cột
    for bar, val in zip(bars, values):
        if val > 0:
            ax.text(
                bar.get_x() + bar.get_width() / 2,
                bar.get_height() + max(values) * 0.02,
                str(val),
                ha="center", va="bottom",
                color=COLORS["text"], fontsize=11, fontweight="bold",
            )

    # Tỉ lệ phần trăm
    total = sum(values)
    for bar, val in zip(bars, values):
        if val > 0 and total > 0:
            pct = val / total * 100
            ax.text(
                bar.get_x() + bar.get_width() / 2,
                bar.get_height() / 2,
                f"{pct:.1f}%",
                ha="center", va="center",
                color="black", fontsize=9, fontweight="bold", alpha=0.8,
            )

    ax.set_ylabel("Số mẫu (samples)", fontsize=11)
    ax.set_title("📊  Growth Stage Distribution — CPS Plant Digital Twin",
                 fontsize=14, fontweight="bold", pad=15)
    ax.yaxis.set_major_locator(MaxNLocator(integer=True))
    ax.tick_params(axis="x", colors=COLORS["text"])

    path = output_dir / "05_growth_stage_summary.png"
    save_fig(fig, path, "05")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# Xuất CSV phân tích
# ─────────────────────────────────────────────────────────────────────────────

def export_analysis_csv(df: pd.DataFrame, x_col: str, output_dir: Path) -> Path:
    """
    Xuất file growth_analysis.csv với các cột chuẩn.
    """
    out_cols = {
        "time_col":         x_col if x_col in df.columns else None,
        "days_after_plant": "days_after_planting" if "days_after_planting" in df.columns else None,
        "height":           "plant_height_cm",
        "rate":             "growth_rate_cm_per_day",
        "status":           "growth_status",
    }

    result = pd.DataFrame()
    result["timestamp"] = df[out_cols["time_col"]] if out_cols["time_col"] else np.nan
    result["days_after_planting"] = (
        df[out_cols["days_after_plant"]] if out_cols["days_after_plant"] else np.nan
    )
    result["plant_height_cm"] = df["plant_height_cm"]
    result["growth_rate_cm_per_day"] = df["growth_rate_cm_per_day"].round(4)
    result["growth_status"] = df["growth_status"]

    path = output_dir / "growth_analysis.csv"
    result.to_csv(path, index=False, encoding="utf-8-sig")
    print(f"    ✅  Đã xuất phân tích CSV: {path}")
    return path


# ─────────────────────────────────────────────────────────────────────────────
# In nhận xét tự động ra terminal
# ─────────────────────────────────────────────────────────────────────────────

def print_analysis_report(df: pd.DataFrame, env_map: dict):
    print()
    print("=" * 60)
    print("📋  BÁO CÁO PHÂN TÍCH SINH TRƯỞNG — CPS PLANT DIGITAL TWIN")
    print("=" * 60)

    h = df["plant_height_cm"]
    r = df["growth_rate_cm_per_day"]
    days = df.get("days_after_planting", pd.Series(np.arange(len(df))))

    print(f"\n🌱  Chiều cao ban đầu   : {h.iloc[0]:.2f} cm")
    print(f"🌿  Chiều cao cuối cùng : {h.iloc[-1]:.2f} cm")
    print(f"📏  Tổng tăng trưởng   : {h.iloc[-1] - h.iloc[0]:.2f} cm")
    print(f"⚡  Growth rate TB     : {r.mean():.3f} cm/day")
    print(f"🔺  Growth rate max    : {r.max():.3f} cm/day")
    print(f"🔻  Growth rate min    : {r.min():.3f} cm/day")

    # Giai đoạn tăng mạnh nhất
    fast_mask = df["growth_status"] == "fast_growth"
    if fast_mask.any():
        fast_days = days[fast_mask]
        print(f"\n🚀  Giai đoạn tăng mạnh nhất:")
        print(f"    Ngày {fast_days.min():.1f} → {fast_days.max():.1f} "
              f"({fast_mask.sum()} mẫu, chiều cao "
              f"{h[fast_mask].min():.2f}–{h[fast_mask].max():.2f} cm)")
    else:
        print("\n🚀  Không có giai đoạn tăng nhanh rõ rệt.")

    # Giai đoạn dừng/chậm
    stop_mask = df["growth_status"] == "stopped"
    slow_mask = df["growth_status"] == "slow_growth"
    if stop_mask.any():
        stop_days = days[stop_mask]
        print(f"\n🛑  Giai đoạn gần dừng tăng trưởng:")
        print(f"    Ngày {stop_days.min():.1f} → {stop_days.max():.1f} "
              f"({stop_mask.sum()} mẫu)")
    if slow_mask.any():
        slow_days = days[slow_mask]
        print(f"\n🐢  Giai đoạn tăng chậm:")
        print(f"    Ngày {slow_days.min():.1f} → {slow_days.max():.1f} "
              f"({slow_mask.sum()} mẫu)")

    # Phân bố growth stages
    print(f"\n📊  Phân bố trạng thái sinh trưởng:")
    for stage, label in STAGE_LABELS.items():
        cnt = (df["growth_status"] == stage).sum()
        pct = cnt / len(df) * 100
        print(f"    {label:<35} : {cnt:4d} mẫu ({pct:.1f}%)")

    # Tương quan môi trường
    if env_map:
        print(f"\n🌡️  Tương quan với chiều cao cây:")
        for std, real in env_map.items():
            col = std if std in df.columns else real
            if col in df.columns:
                try:
                    corr = df["plant_height_cm"].corr(pd.to_numeric(df[col], errors="coerce"))
                    arrow = "↑ tương quan dương" if corr > 0.3 else ("↓ tương quan âm" if corr < -0.3 else "↔ ít tương quan")
                    print(f"    {std:<20} : r = {corr:+.3f}  {arrow}")
                except Exception:
                    pass

        # Tìm yếu tố tương quan mạnh nhất
        best_corr = 0
        best_factor = None
        for std, real in env_map.items():
            col = std if std in df.columns else real
            if col in df.columns:
                try:
                    corr = abs(df["plant_height_cm"].corr(
                        pd.to_numeric(df[col], errors="coerce")
                    ))
                    if not np.isnan(corr) and corr > best_corr:
                        best_corr = corr
                        best_factor = std
                except Exception:
                    pass
        if best_factor:
            print(f"\n    ⭐ Yếu tố tương quan mạnh nhất với chiều cao: "
                  f"'{best_factor}' (|r| = {best_corr:.3f})")

    print("\n" + "=" * 60)


# ─────────────────────────────────────────────────────────────────────────────
# Hàm main
# ─────────────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Phân tích tốc độ sinh trưởng cây từ dữ liệu CSV — CPS Plant Digital Twin",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    parser.add_argument(
        "--csv", required=True, metavar="PATH",
        help="Đường dẫn tới file CSV cần phân tích.\n"
             "Ví dụ: --csv data/plant_data.csv",
    )
    parser.add_argument(
        "--height-col", default=None, metavar="COL",
        help="Tên cột chiều cao cây (nếu muốn chỉ định thủ công).\n"
             "Ví dụ: --height-col 'Plant Height'",
    )
    parser.add_argument(
        "--time-col", default=None, metavar="COL",
        help="Tên cột thời gian (timestamp hoặc days).\n"
             "Ví dụ: --time-col '_time'",
    )
    parser.add_argument(
        "--smoothing-window", type=int, default=3, metavar="N",
        help="Kích thước cửa sổ rolling mean để làm mượt growth rate.\n"
             "Mặc định: 3. Khuyến nghị: 3–5 cho dữ liệu thưa, 7–10 cho dày.",
    )
    parser.add_argument(
        "--output-dir", default="outputs/plots", metavar="DIR",
        help="Thư mục lưu biểu đồ và CSV phân tích.\n"
             "Mặc định: outputs/plots",
    )
    args = parser.parse_args()

    # ── Kiểm tra file CSV tồn tại ─────────────────────────────────────────
    csv_path = Path(args.csv)
    if not csv_path.exists():
        sys.exit(f"❌  Không tìm thấy file CSV: {csv_path}")

    # ── Tạo thư mục output ─────────────────────────────────────────────────
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"\n📁  Output directory: {output_dir.resolve()}")

    # ── Đọc và làm sạch dữ liệu ───────────────────────────────────────────
    df, height_col, time_col = load_and_clean(
        str(csv_path),
        height_col_hint=args.height_col,
        time_col_hint=args.time_col,
    )

    # Xác định x_col tốt nhất để dùng làm trục X
    if "days_after_planting" in df.columns:
        x_col = "days_after_planting"
    elif time_col in df.columns:
        x_col = time_col
    else:
        x_col = "row_index"
        df["row_index"] = np.arange(len(df))

    # ── Tính growth rate và phân loại ─────────────────────────────────────
    print("\n⚙️   Tính toán growth rate...")
    df = compute_growth_rate(df, days_col=x_col, smoothing_window=args.smoothing_window)

    # ── Tìm cột môi trường ────────────────────────────────────────────────
    env_map = find_env_columns(df)
    if env_map:
        print(f"    → Tìm thấy cột môi trường: {list(env_map.keys())}")
    else:
        print("    → Không tìm thấy cột môi trường.")

    # ── Vẽ biểu đồ ───────────────────────────────────────────────────────
    print("\n🎨  Đang vẽ biểu đồ...")
    saved_files = []

    p1 = plot_height_over_time(df, x_col, output_dir)
    saved_files.append(p1)

    p2 = plot_growth_rate(df, x_col, output_dir)
    saved_files.append(p2)

    p3 = plot_height_vs_days(df, output_dir)
    if p3:
        saved_files.append(p3)

    if env_map:
        p4 = plot_correlation_heatmap(df.copy(), env_map, output_dir)
        if p4:
            saved_files.append(p4)

    p5 = plot_growth_stage_summary(df, output_dir)
    saved_files.append(p5)

    # ── Xuất CSV phân tích ────────────────────────────────────────────────
    print("\n📊  Xuất CSV phân tích...")
    export_analysis_csv(df, x_col, output_dir)

    # ── In báo cáo ────────────────────────────────────────────────────────
    print_analysis_report(df, env_map)

    # ── Tóm tắt output ───────────────────────────────────────────────────
    print("\n📂  Files đã tạo:")
    for f in saved_files:
        print(f"    {f}")
    print(f"    {output_dir / 'growth_analysis.csv'}")
    print()
    print("✅  Hoàn tất phân tích!")


if __name__ == "__main__":
    main()
