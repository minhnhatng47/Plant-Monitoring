"""
analyze_growth_from_old_csv.py
==============================
Script phan tich toc do sinh truong cay tu cac file CSV cu (export tu InfluxDB).
Du lieu duoc lam sach, resample theo thoi gian, tinh chieu cao va growth rate,
va ve cac bieu do truc quan phuc vu bao cao.

Cach dung:
    python analyze_growth_from_old_csv.py --input "path/to/file_csv" --duration-days 5 --sample-interval "30min"

Author: CPS Plant Digital Twin Project
"""

import os
import sys
import glob
import argparse
import datetime
import warnings
from pathlib import Path
import numpy as np
import pandas as pd
import matplotlib
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
import matplotlib.colors as mcolors

# Thiet lap font va thong so hien thi
warnings.filterwarnings("ignore")
matplotlib.rcParams["font.family"] = "DejaVu Sans"
matplotlib.rcParams["figure.dpi"] = 150
matplotlib.rcParams["savefig.dpi"] = 150

# Ep buoc dau ra terminal UTF-8 de tranh loi encoding tren Windows khi in tieng Viet
import io
if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
if sys.stderr.encoding and sys.stderr.encoding.lower() not in ("utf-8", "utf8"):
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

# ─────────────────────────────────────────────────────────────────────────────
# Màu sắc và theme (Sleek Dark Theme)
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

STATUS_LABELS = {
    "fast_growth":   "Tăng nhanh (Fast)",
    "normal_growth": "Bình thường (Normal)",
    "slow_growth":   "Tăng chậm (Slow)",
    "stopped":       "Dừng/Âm (Stopped)",
}

# ─────────────────────────────────────────────────────────────────────────────
# Hàm sinh trưởng Logistic (Giống PlantGrowthSimulator.cs)
# ─────────────────────────────────────────────────────────────────────────────

def compute_plant_height(days_after_planting, cycle_days=5.0, max_height_cm=12.0):
    """
    Tinh chieu cao cay dua tren days_after_planting bang mo hinh Logistic.
    Tra ve mot array/Series chua gia tri chieu cao.
    """
    days = np.clip(np.array(days_after_planting, dtype=float), 0, None)
    
    # Cac tham so mo phong
    m0 = 0.02
    target = 0.98
    mMax = 1.0
    
    # Tinh toan he so tang truong r tu m0 va target tai thoi diem cuoi chu ky (cycle_days)
    a = (mMax - m0) / m0
    b = (mMax / target) - 1.0
    r = -np.log(b / a) / cycle_days
    
    # Tinh toan sinh khoi va tien do sinh truong
    biomass = mMax / (1.0 + a * np.exp(-r * days))
    progress = (biomass - m0) / (target - m0)
    progress = np.clip(progress, 0.0, 1.0)
    
    # Chieu cao
    heights = progress * max_height_cm
    return heights

def classify_growth_stage(h):
    if h <= 0.3:
        return 'seed'
    elif h <= 3.0:
        return 'sprout'
    elif h <= 7.0:
        return 'growing'
    else:
        return 'mature'

# ─────────────────────────────────────────────────────────────────────────────
# Hàm tiện ích
# ─────────────────────────────────────────────────────────────────────────────

def get_mode_or_last(series):
    """Lay gia tri mode (pho bien nhat), neu khong co lay gia tri cuoi cung."""
    series_clean = series.dropna()
    if series_clean.empty:
        return np.nan
    modes = series_clean.mode()
    if not modes.empty:
        return modes.iloc[0]
    return series_clean.iloc[-1]

def apply_dark_style(fig, axes_list):
    """Ap dung dark theme cho do thi."""
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

# ─────────────────────────────────────────────────────────────────────────────
# Đọc và xử lý dữ liệu
# ─────────────────────────────────────────────────────────────────────────────

def read_and_clean_csv_files(input_dir: str, duration_days: float) -> tuple[pd.DataFrame, int]:
    """
    Doc tat ca cac file CSV trong thu muc input, gop lai va lam sach co ban.
    """
    csv_pattern = os.path.join(input_dir, "*.csv")
    csv_files = glob.glob(csv_pattern)
    
    if not csv_files:
        sys.exit(f"❌ Khong tim thay file CSV nao trong thu muc: {input_dir}")
        
    print(f"📚 Tim thay {len(csv_files)} file CSV. Dang tien hanh doc...")
    
    dfs = []
    total_raw_rows = 0
    for idx, f in enumerate(csv_files, 1):
        try:
            # Doc file CSV va bo qua cac dong chu thich co dau # o dau file (annotated InfluxDB CSV)
            df_temp = pd.read_csv(f, comment="#")
            
            # Xoa cot Unnamed
            df_temp = df_temp.loc[:, ~df_temp.columns.str.contains('^Unnamed|^$')]
            
            # Strip khoang trang trong ten cot
            df_temp.columns = [c.strip() for c in df_temp.columns]
            
            total_raw_rows += len(df_temp)
            dfs.append(df_temp)
            print(f"   [{idx}/{len(csv_files)}] Đoc thanh cong: {os.path.basename(f)} ({len(df_temp)} dong)")
        except Exception as e:
            print(f"   ⚠️ Lỗi khi doc file {f}: {e}")
            
    if not dfs:
        sys.exit("❌ Khong co du lieu hop le duoc doc tu cac file CSV.")
        
    # Gop tat ca cac DataFrame
    df = pd.concat(dfs, ignore_index=True)
    
    if 'time' not in df.columns:
        sys.exit("❌ File CSV thieu cot 'time' bat buoc cua InfluxDB.")
        
    # Chuyen time sang datetime
    df['time'] = pd.to_datetime(df['time'], errors='coerce')
    df = df.dropna(subset=['time'])
    
    # Sort theo time va xoa cac dong lap
    df = df.sort_values(by='time').reset_index(drop=True)
    df = df.drop_duplicates(subset=['time']).reset_index(drop=True)
    
    # Neu thieu days_after_planting, tu tinh tu time
    if 'days_after_planting' not in df.columns:
        print("   ⚠️ Cot 'days_after_planting' bi thieu trong file. Tien hanh tu tinh tu thoi gian dau tien...")
        t0 = df['time'].min()
        df['days_after_planting'] = (df['time'] - t0).dt.total_seconds() / 86400.0
    else:
        df['days_after_planting'] = pd.to_numeric(df['days_after_planting'], errors='coerce')
        
    # Loc theo days_after_planting tu 0 den duration_days
    df = df[(df['days_after_planting'] >= 0.0) & (df['days_after_planting'] <= duration_days)]
    
    # Ep kieu cac cot numeric
    num_cols = ["temperature", "air_humidity", "lux", "soil_moisture", "soil_moisture_fused", "days_after_planting"]
    for col in num_cols:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors='coerce')
            
    return df, total_raw_rows

# ─────────────────────────────────────────────────────────────────────────────
# Resample dữ liệu
# ─────────────────────────────────────────────────────────────────────────────

def resample_data(df: pd.DataFrame, sample_interval: str) -> pd.DataFrame:
    """
    Resample du lieu theo sample_interval.
    """
    df_temp = df.copy()
    df_temp = df_temp.set_index('time')
    
    # Khai bao cach aggregate cho tung loai cot
    num_cols = ["days_after_planting", "temperature", "air_humidity", "lux", "soil_moisture", "soil_moisture_fused"]
    cat_cols = ["phase", "pump_state", "light_state"]
    
    agg_dict = {}
    for col in num_cols:
        if col in df_temp.columns:
            agg_dict[col] = 'mean'
            
    for col in cat_cols:
        if col in df_temp.columns:
            agg_dict[col] = get_mode_or_last
            
    # Thuc hien resample
    resampled = df_temp.resample(sample_interval).agg(agg_dict)
    
    # Interpolate du lieu thieu cho cac cot numeric
    num_cols_present = [c for c in num_cols if c in resampled.columns]
    resampled[num_cols_present] = resampled[num_cols_present].interpolate(method='linear')
    resampled[num_cols_present] = resampled[num_cols_present].ffill().bfill()
    
    # Forward fill va backward fill cho cac cot categorical
    cat_cols_present = [c for c in cat_cols if c in resampled.columns]
    if cat_cols_present:
        resampled[cat_cols_present] = resampled[cat_cols_present].ffill().bfill()
        
    # Loai bo cac dong co days_after_planting bi NaN sau khi resample
    resampled = resampled.dropna(subset=['days_after_planting'])
    
    return resampled

# ─────────────────────────────────────────────────────────────────────────────
# Vẽ biểu đồ
# ─────────────────────────────────────────────────────────────────────────────

def plot_plant_height(df: pd.DataFrame, output_dir: Path):
    fig, ax = plt.subplots(figsize=(11, 5.5))
    apply_dark_style(fig, [ax])
    
    x = df["days_after_planting"]
    y = df["plant_height_cm"]
    
    ax.fill_between(x, y, alpha=0.15, color=COLORS["green"])
    ax.plot(x, y, color=COLORS["green_light"], linewidth=2.5, label="Plant Height (cm)", zorder=3)
    ax.scatter(x, y, color=COLORS["green"], s=15, zorder=4, alpha=0.6)
    
    # Ve cac line danh dau stage
    ax.axhline(0.3, color=COLORS["cyan"], linestyle=":", alpha=0.5)
    ax.axhline(3.0, color=COLORS["yellow"], linestyle=":", alpha=0.5)
    ax.axhline(7.0, color=COLORS["purple"], linestyle=":", alpha=0.5)
    
    # Add text label cho cac stage
    ax.text(x.min() + 0.1, 0.15, "Seed (<0.3 cm)", color=COLORS["cyan"], fontsize=8, alpha=0.8)
    ax.text(x.min() + 0.1, 1.5, "Sprout (0.3 - 3.0 cm)", color=COLORS["yellow"], fontsize=8, alpha=0.8)
    ax.text(x.min() + 0.1, 5.0, "Growing (3.0 - 7.0 cm)", color=COLORS["purple"], fontsize=8, alpha=0.8)
    ax.text(x.min() + 0.1, 9.5, "Mature (>7.0 cm)", color=COLORS["green_light"], fontsize=8, alpha=0.8)
    
    # Annotate max
    idx_max = y.idxmax()
    ax.annotate(
        f" Max: {y[idx_max]:.2f} cm",
        xy=(x[idx_max], y[idx_max]),
        xytext=(x[idx_max] - 0.8, y[idx_max] - 1.2),
        color=COLORS["yellow"],
        fontsize=9, fontweight="bold",
        arrowprops=dict(arrowstyle="->", color=COLORS["yellow"], lw=0.8)
    )
    
    ax.set_xlabel("Days After Planting (days)", fontsize=10)
    ax.set_ylabel("Plant Height (cm)", fontsize=10)
    ax.set_title("🌱  Plant Height Over Time — Logistic Growth Model", fontsize=13, fontweight="bold", pad=15)
    ax.legend(facecolor=COLORS["panel"], edgecolor=COLORS["border"], labelcolor=COLORS["text"])
    
    path = output_dir / "01_plant_height_over_time.png"
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    return path

def plot_growth_rate(df: pd.DataFrame, output_dir: Path):
    fig, ax = plt.subplots(figsize=(11, 5.5))
    apply_dark_style(fig, [ax])
    
    x = df["days_after_planting"]
    raw = df["growth_rate_cm_per_day"]
    smooth = df["growth_rate_smooth"]
    
    # To mau cac vung growth_status cho dep mat
    status = df["growth_status"]
    for i in range(len(df) - 1):
        color = STAGE_COLORS.get(status.iloc[i], COLORS["normal"])
        ax.axvspan(x.iloc[i], x.iloc[i + 1], alpha=0.08, color=color, linewidth=0)
        
    ax.plot(x, raw, color=COLORS["text_muted"], linewidth=1.0, alpha=0.4, linestyle="--", label="Raw Rate")
    ax.plot(x, smooth, color=COLORS["blue"], linewidth=2.5, label="Smoothed Rate", zorder=3)
    ax.fill_between(x, smooth, alpha=0.15, color=COLORS["blue"])
    
    ax.axhline(0, color=COLORS["red"], linewidth=0.8, linestyle=":", alpha=0.8)
    
    # Legend cho growth status
    patches = [
        mpatches.Patch(color=STAGE_COLORS[k], label=STATUS_LABELS[k], alpha=0.6)
        for k in STAGE_COLORS
    ]
    leg1 = ax.legend(facecolor=COLORS["panel"], edgecolor=COLORS["border"], labelcolor=COLORS["text"], loc="upper right")
    ax.legend(handles=patches, facecolor=COLORS["panel"], edgecolor=COLORS["border"], labelcolor=COLORS["text"], loc="upper left", fontsize=8)
    ax.add_artist(leg1)
    
    ax.set_xlabel("Days After Planting (days)", fontsize=10)
    ax.set_ylabel("Growth Rate (cm/day)", fontsize=10)
    ax.set_title("📈  Plant Growth Rate Over Time", fontsize=13, fontweight="bold", pad=15)
    
    path = output_dir / "02_growth_rate_over_time.png"
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    return path

def plot_growth_status_timeline(df: pd.DataFrame, output_dir: Path):
    fig, ax = plt.subplots(figsize=(11, 4.5))
    apply_dark_style(fig, [ax])
    
    x = df["days_after_planting"]
    status = df["growth_status"]
    
    # Convert status sang gia tri so de de ve
    status_map = {"stopped": 0, "slow_growth": 1, "normal_growth": 2, "fast_growth": 3}
    y_vals = status.map(status_map).values
    
    # Ve duong step
    ax.step(x, y_vals, where="post", color=COLORS["purple"], linewidth=2, zorder=2, alpha=0.8)
    
    # Ve cac marker tuong ung voi tung status de de quan sat
    colors_list = [STAGE_COLORS.get(s, COLORS["normal"]) for s in status]
    ax.scatter(x, y_vals, c=colors_list, s=35, zorder=3, edgecolors=COLORS["border"], linewidths=0.5)
    
    # Set y-ticks voi nhan de doc
    ax.set_yticks([0, 1, 2, 3])
    ax.set_yticklabels(["Dừng (Stopped)", "Chậm (Slow)", "Bình thường (Normal)", "Nhanh (Fast)"], color=COLORS["text"])
    
    # Highlight cac khoang thoi gian bang cac dai mau nhe o background
    for i in range(len(df) - 1):
        color = STAGE_COLORS.get(status.iloc[i], COLORS["normal"])
        ax.axvspan(x.iloc[i], x.iloc[i + 1], alpha=0.06, color=color, linewidth=0)
        
    ax.set_xlabel("Days After Planting (days)", fontsize=10)
    ax.set_title("⏱️  Growth Status Timeline", fontsize=13, fontweight="bold", pad=15)
    
    path = output_dir / "03_growth_status_over_time.png"
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    return path

def plot_correlation_heatmap(df: pd.DataFrame, output_dir: Path):
    cols = ["plant_height_cm", "growth_rate_cm_per_day", "temperature", "air_humidity", "lux", "soil_moisture", "soil_moisture_fused"]
    # Loc ra cac cot co trong df va khong bi null hoan toan
    valid_cols = [c for c in cols if c in df.columns and df[c].notna().sum() > 2 and df[c].std() > 1e-6]
    
    if len(valid_cols) < 3:
        print("   ⚠️ Khong du du lieu moi truong hoac khong co bien dong de ve correlation heatmap.")
        return None
        
    corr = df[valid_cols].corr()
    n = len(valid_cols)
    
    fig, ax = plt.subplots(figsize=(max(6.5, n * 1.1), max(5.5, n * 0.9)))
    apply_dark_style(fig, [ax])
    
    data = corr.values
    cmap = plt.cm.RdYlGn
    im = ax.imshow(data, cmap=cmap, vmin=-1, vmax=1, aspect="auto")
    
    # Set labels
    label_map = {
        "plant_height_cm": "Height (cm)",
        "growth_rate_cm_per_day": "Growth Rate",
        "temperature": "Temp (°C)",
        "air_humidity": "Humidity (%)",
        "lux": "Light (Lux)",
        "soil_moisture": "Soil Moist (%)",
        "soil_moisture_fused": "Soil Fused (%)"
    }
    labels = [label_map.get(c, c) for c in valid_cols]
    
    ax.set_xticks(np.arange(n))
    ax.set_yticks(np.arange(n))
    ax.set_xticklabels(labels, rotation=30, ha="right", fontsize=9, color=COLORS["text"])
    ax.set_yticklabels(labels, fontsize=9, color=COLORS["text"])
    
    # Ghi gia tri vao tung o
    for i in range(n):
        for j in range(n):
            val = data[i, j]
            text_color = "black" if abs(val) > 0.4 else COLORS["text"]
            ax.text(j, i, f"{val:.2f}", ha="center", va="center", fontsize=9, fontweight="bold", color=text_color)
            
    cbar = fig.colorbar(im, ax=ax, fraction=0.046, pad=0.04)
    cbar.ax.tick_params(colors=COLORS["text_muted"])
    cbar.set_label("Correlation Coefficient", color=COLORS["text"], fontsize=9)
    
    ax.set_title("🔥  Correlation Heatmap (Plant & Environment)", fontsize=12, fontweight="bold", pad=15)
    fig.tight_layout()
    
    path = output_dir / "04_correlation_heatmap.png"
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    return path

def plot_growth_stage_summary(df: pd.DataFrame, output_dir: Path):
    stages_order = ["fast_growth", "normal_growth", "slow_growth", "stopped"]
    counts = df["growth_status"].value_counts()
    values = [counts.get(s, 0) for s in stages_order]
    colors = [STAGE_COLORS[s] for s in stages_order]
    labels = [STATUS_LABELS[s] for s in stages_order]
    
    fig, ax = plt.subplots(figsize=(9, 5))
    apply_dark_style(fig, [ax])
    
    bars = ax.bar(labels, values, color=colors, width=0.5, edgecolor=COLORS["border"], linewidth=0.8, zorder=3)
    
    # Ghi so luong va phan tram vao bieu do
    total = sum(values)
    for bar, val in zip(bars, values):
        if val > 0:
            pct = val / total * 100
            # Text tren dau bar
            ax.text(
                bar.get_x() + bar.get_width() / 2,
                bar.get_height() + max(values) * 0.02,
                f"{val} ({pct:.1f}%)",
                ha="center", va="bottom",
                color=COLORS["text"], fontsize=9, fontweight="bold"
            )
            
    ax.set_ylabel("Số mẫu (samples)", fontsize=10)
    ax.set_title("📊  Growth Status Sample Distribution", fontsize=13, fontweight="bold", pad=15)
    ax.yaxis.set_major_locator(plt.MaxNLocator(integer=True))
    ax.tick_params(axis="x", colors=COLORS["text"])
    
    path = output_dir / "05_growth_stage_summary.png"
    fig.savefig(path, bbox_inches="tight", facecolor=fig.get_facecolor())
    plt.close(fig)
    return path

# ─────────────────────────────────────────────────────────────────────────────
# Xuất báo cáo text
# ─────────────────────────────────────────────────────────────────────────────

def generate_text_report(df: pd.DataFrame, num_files: int, initial_rows: int,
                         sample_interval: str, output_dir: Path, duration_days: float) -> Path:
    """
    Tao file growth_report_summary.txt nhan xet tu dong.
    """
    h = df["plant_height_cm"]
    r = df["growth_rate_cm_per_day"]
    days = df["days_after_planting"]
    
    # Thong ke co ban
    h_start = h.iloc[0]
    h_end = h.iloc[-1]
    total_growth = h_end - h_start
    avg_growth_rate = r.mean()
    
    # Tim thoi gian tang truong manh nhat (growth_status == fast_growth)
    fast_growth_df = df[df["growth_status"] == "fast_growth"]
    if not fast_growth_df.empty:
        fast_start = fast_growth_df["days_after_planting"].min()
        fast_end = fast_growth_df["days_after_planting"].max()
        fast_text = f"Ngày {fast_start:.2f} đến Ngày {fast_end:.2f} (chiều cao: {fast_growth_df['plant_height_cm'].min():.2f} - {fast_growth_df['plant_height_cm'].max():.2f} cm)"
    else:
        fast_text = "Không xác định rõ giai đoạn tăng nhanh vượt trội"
        
    # Tim thoi gian tang truong cham/dung (growth_status == stopped)
    stopped_df = df[df["growth_status"] == "stopped"]
    slow_df = df[df["growth_status"] == "slow_growth"]
    
    stopped_text = "Không có giai đoạn dừng tăng trưởng"
    if not stopped_df.empty:
        stopped_start = stopped_df["days_after_planting"].min()
        stopped_end = stopped_df["days_after_planting"].max()
        stopped_text = f"Ngày {stopped_start:.2f} đến Ngày {stopped_end:.2f}"
    elif not slow_df.empty:
        slow_start = slow_df["days_after_planting"].min()
        slow_end = slow_df["days_after_planting"].max()
        stopped_text = f"Cây tăng trưởng chậm từ Ngày {slow_start:.2f} đến Ngày {slow_end:.2f} (không dừng hẳn)"
        
    # Tinh toan tuong quan voi chieu cao va growth rate
    env_cols = ["temperature", "air_humidity", "lux", "soil_moisture", "soil_moisture_fused"]
    valid_env_cols = [c for c in env_cols if c in df.columns and df[c].notna().sum() > 2 and df[c].std() > 1e-6]
    
    height_corr_text = "Không đủ dữ liệu môi trường để tính tương quan"
    rate_corr_text = "Không đủ dữ liệu môi trường để tính tương quan"
    
    if valid_env_cols:
        height_corrs = {col: df["plant_height_cm"].corr(df[col]) for col in valid_env_cols}
        # Loc bo cac bien bi NaN
        height_corrs = {k: v for k, v in height_corrs.items() if not pd.isna(v)}
        if height_corrs:
            best_h_col = max(height_corrs, key=lambda k: abs(height_corrs[k]))
            best_h_val = height_corrs[best_h_col]
            h_dir = "tương quan dương" if best_h_val > 0 else "tương quan âm"
            height_corr_text = f"'{best_h_col}' với hệ số r = {best_h_val:+.3f} ({h_dir})"
            
        rate_corrs = {col: df["growth_rate_cm_per_day"].corr(df[col]) for col in valid_env_cols}
        rate_corrs = {k: v for k, v in rate_corrs.items() if not pd.isna(v)}
        if rate_corrs:
            best_r_col = max(rate_corrs, key=lambda k: abs(rate_corrs[k]))
            best_r_val = rate_corrs[best_r_col]
            r_dir = "tương quan dương" if best_r_val > 0 else "tương quan âm"
            rate_corr_text = f"'{best_r_col}' với hệ số r = {best_r_val:+.3f} ({r_dir})"

    report_content = f"""============================================================
📋 BÁO CÁO PHÂN TÍCH TĂNG TRƯỞNG CÂY (TỪ DỮ LIỆU CŨ)
============================================================
Thời gian xuất báo cáo : {datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")}
Thư mục chứa file gốc  : {output_dir.resolve()}
Số lượng file đã đọc   : {num_files}
Tổng số dòng ban đầu   : {initial_rows}
Số điểm sau resample   : {len(df)}
Khoảng thời gian mẫu   : {sample_interval}
Thời gian bắt đầu dữ liệu : {df.index.min()}
Thời gian kết thúc dữ liệu: {df.index.max()}
Thời gian phân tích    : {duration_days} ngày

------------------------------------------------------------
🌱 CHỈ SỐ SINH TRƯỞNG CHÍNH
------------------------------------------------------------
Chiều cao ban đầu      : {h_start:.2f} cm
Chiều cao cuối cùng    : {h_end:.2f} cm
Tổng tăng trưởng thực tế: {total_growth:.2f} cm
Tốc độ tăng trưởng TB  : {avg_growth_rate:.3f} cm/ngày
Tốc độ tăng trưởng cực đại (smoothed): {df['growth_rate_smooth'].max():.3f} cm/ngày

📈 Giai đoạn tăng trưởng mạnh nhất (Fast Growth):
   → {fast_text}

🛑 Giai đoạn dừng hoặc tăng trưởng rất chậm (Stopped/Slow):
   → {stopped_text}
   * Ghi chú: Sau ngày thứ 5 (nếu phân tích kéo dài), cây tiến gần đến chiều cao tối đa (12cm)
     theo mô hình Logistic sinh trưởng, do đó tốc độ tăng trưởng sẽ giảm mạnh.

------------------------------------------------------------
🌡️ PHÂN TÍCH TƯƠNG QUAN MÔI TRƯỜNG
------------------------------------------------------------
Yếu tố môi trường ảnh hưởng lớn nhất đến:
   - Chiều cao cây (Plant Height) : {height_corr_text}
   - Tốc độ tăng trưởng (Growth Rate): {rate_corr_text}

============================================================
❗ LƯU Ý QUAN TRỌNG:
- Cột 'plant_height_cm' trong phân tích này KHÔNG phải là dữ liệu đo đạc trực tiếp từ cảm biến vật lý.
- Đây là giá trị được tính toán lại (reconstructed) dựa trên mô hình sinh trưởng Logistic chuẩn 
  từ cột 'days_after_planting' có sẵn trong file dữ liệu gốc, nhằm phục vụ mục đích kiểm chứng 
  và vẽ biểu đồ minh họa sinh động.
============================================================
"""
    
    path = output_dir / "growth_report_summary.txt"
    with open(path, "w", encoding="utf-8") as f:
        f.write(report_content)
        
    print(f"   ✅ Đã lưu báo cáo: {path}")
    return path

# ─────────────────────────────────────────────────────────────────────────────
# Hàm main
# ─────────────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Phân tích tăng trưởng cây từ tệp dữ liệu CSV cũ của InfluxDB — CPS Plant Digital Twin",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    parser.add_argument(
        "--input", default=r"D:\Nhat An\CPS\Unity\CPS_Plant_DigitalTwin\file_csv",
        help="Đường dẫn đến thư mục chứa các file CSV cũ cần phân tích.",
    )
    parser.add_argument(
        "--duration-days", type=float, default=5.0,
        help="Số ngày sinh trưởng cần phân tích tính từ ngày 0. Mặc định: 5.0",
    )
    parser.add_argument(
        "--sample-interval", default="30min",
        help="Khoảng thời gian lấy mẫu lại (resample interval), ví dụ: 30min, 1H, 4H. Mặc định: 30min",
    )
    args = parser.parse_args()
    
    input_path = Path(args.input)
    if not input_path.exists():
        sys.exit(f"❌ Thư mục đầu vào không tồn tại: {input_path}")
        
    # Tạo thư mục output riêng có timestamp
    timestamp_str = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
    output_dir = Path("outputs") / f"plant_growth_clean_{timestamp_str}"
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"📁 Tạo thư mục kết quả: {output_dir.resolve()}")
    
    # 1. Đọc và gộp các file CSV
    df_raw, initial_rows = read_and_clean_csv_files(str(input_path), args.duration_days)
    
    num_csv_files = len(glob.glob(os.path.join(str(input_path), "*.csv")))
    
    # Lưu raw_merged_data.csv
    raw_merged_path = output_dir / "raw_merged_data.csv"
    df_raw.to_csv(raw_merged_path, index=False, encoding="utf-8-sig")
    print(f"   ✅ Đã xuất dữ liệu gộp thô: {raw_merged_path}")
    
    # 2. Resample dữ liệu theo khoảng thời gian chỉ định
    print(f"⚙️ Đang tiến hành lấy mẫu lại (resampling) với chu kỳ: {args.sample_interval}...")
    resampled = resample_data(df_raw, args.sample_interval)
    
    # Lưu sampled_data.csv
    sampled_path = output_dir / "sampled_data.csv"
    resampled.to_csv(sampled_path, index=True, index_label="timestamp", encoding="utf-8-sig")
    print(f"   ✅ Đã xuất dữ liệu lấy mẫu: {sampled_path}")
    
    # 3. Tính toán các chỉ số tăng trưởng sinh học
    print("🧬 Tác vụ sinh trưởng: Đang tính toán chiều cao và tốc độ sinh trưởng...")
    
    # Tinh plant_height_cm
    resampled["plant_height_cm"] = compute_plant_height(
        resampled["days_after_planting"], 
        cycle_days=5.0, 
        max_height_cm=12.0
    )
    # Su dung cummax() de dam bao chieu cao luon tang len, khong bao gio giam
    resampled["plant_height_cm"] = resampled["plant_height_cm"].cummax()
    
    # Tinh growth_progress
    resampled["growth_progress"] = resampled["plant_height_cm"] / 12.0
    
    # Tinh growth_rate_cm_per_day
    delta_h = resampled["plant_height_cm"].diff()
    delta_d = resampled["days_after_planting"].diff()
    
    # Tránh chia cho 0 hoặc khoảng thời gian cực ngắn
    growth_rate = np.zeros(len(resampled))
    for i in range(1, len(resampled)):
        dh = delta_h.iloc[i]
        dd = delta_d.iloc[i]
        if dd > 1e-6:
            growth_rate[i] = dh / dd
        else:
            growth_rate[i] = 0.0
            
    resampled["growth_rate_cm_per_day"] = growth_rate
    
    # Tinh growth_rate_smooth
    resampled["growth_rate_smooth"] = (
        resampled["growth_rate_cm_per_day"]
        .rolling(window=5, center=True, min_periods=1)
        .mean()
    )
    
    # Phan loai growth_status
    rate = resampled["growth_rate_smooth"]
    q25 = rate.quantile(0.25)
    q75 = rate.quantile(0.75)
    if q25 == q75:
        q25 = 0.48  # Default value neu khong co bien dong
        q75 = 1.92
        
    def get_status(r):
        if pd.isna(r) or r <= 0.01:
            return "stopped"
        elif r < q25:
            return "slow_growth"
        elif r >= q75:
            return "fast_growth"
        else:
            return "normal_growth"
            
    resampled["growth_status"] = resampled["growth_rate_smooth"].apply(get_status)
    
    # Phan loai growth_stage
    resampled["growth_stage"] = resampled["plant_height_cm"].apply(classify_growth_stage)
    
    # Export growth_analysis.csv
    analysis_cols = [
        "days_after_planting", "plant_height_cm", "growth_progress",
        "growth_rate_cm_per_day", "growth_rate_smooth", "growth_status", "growth_stage",
        "temperature", "air_humidity", "lux", "soil_moisture", "soil_moisture_fused",
        "phase", "pump_state", "light_state"
    ]
    df_analysis = resampled.copy()
    for col in analysis_cols:
        if col not in df_analysis.columns:
            df_analysis[col] = np.nan
            
    df_analysis = df_analysis[analysis_cols]
    analysis_path = output_dir / "growth_analysis.csv"
    df_analysis.to_csv(analysis_path, index=True, index_label="timestamp", encoding="utf-8-sig")
    print(f"   ✅ Đã xuất dữ liệu phân tích: {analysis_path}")
    
    # 4. Vẽ các biểu đồ
    print("🎨 Đang tiến hành vẽ các đồ thị...")
    p1 = plot_plant_height(resampled, output_dir)
    print(f"   ✓ Đồ thị 1: {p1.name}")
    p2 = plot_growth_rate(resampled, output_dir)
    print(f"   ✓ Đồ thị 2: {p2.name}")
    p3 = plot_growth_status_timeline(resampled, output_dir)
    print(f"   ✓ Đồ thị 3: {p3.name}")
    p4 = plot_correlation_heatmap(resampled, output_dir)
    if p4:
        print(f"   ✓ Đồ thị 4: {p4.name}")
    p5 = plot_growth_stage_summary(resampled, output_dir)
    print(f"   ✓ Đồ thị 5: {p5.name}")
    
    # 5. Xuất báo cáo nhận xét
    report_path = generate_text_report(
        resampled, 
        num_csv_files, 
        initial_rows, 
        args.sample_interval, 
        output_dir, 
        args.duration_days
    )
    
    # In báo cáo ra màn hình
    with open(report_path, "r", encoding="utf-8") as f:
        print(f.read())
        
    print(f"🎉 Hoàn thành phân tích! Kết quả lưu tại thư mục: {output_dir.resolve()}")

if __name__ == "__main__":
    main()
