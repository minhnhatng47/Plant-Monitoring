"""
generate_sample_csv.py
======================
Script tạo file CSV mẫu mô phỏng dữ liệu cây sinh trưởng
theo mô hình Logistic (giống PlantGrowthSimulator.cs).

Dùng để test analyze_plant_growth.py.

Cách chạy:
    python generate_sample_csv.py
"""

import numpy as np
import pandas as pd
from datetime import datetime, timezone, timedelta
from pathlib import Path

# ── Tham số mô phỏng ──────────────────────────────────────────────────────────
TOTAL_DAYS       = 7        # Tổng số ngày sinh trưởng (realFullCycleDays = 7)
SAMPLES_PER_DAY  = 8        # Số mẫu mỗi ngày (mỗi 3 giờ)
MIN_HEIGHT_CM    = 0.0
MAX_HEIGHT_CM    = 12.0
INITIAL_BIOMASS  = 0.02
TARGET_BIOMASS   = 0.98

# Ngày gieo hạt (planting start)
PLANTING_START   = datetime(2026, 6, 20, 8, 0, 0, tzinfo=timezone.utc)
PLANTING_START_EPOCH_S = int(PLANTING_START.timestamp())

rng = np.random.default_rng(42)

# ── Logistic growth model ─────────────────────────────────────────────────────
def logistic_biomass(t_seconds, total_seconds, m0, target, env=1.0):
    mMax = 1.0
    a = (mMax - m0) / m0
    b = (mMax / target) - 1.0
    r = np.log(a / b) / total_seconds
    r_eff = r * env
    return mMax / (1.0 + a * np.exp(-r_eff * t_seconds))

total_seconds = TOTAL_DAYS * 86400
n_samples     = TOTAL_DAYS * SAMPLES_PER_DAY

# Tạo timestamps
timestamps = [
    PLANTING_START + timedelta(seconds=total_seconds * i / (n_samples - 1))
    for i in range(n_samples)
]

# Tính t_seconds từ planting start
t_seconds_arr = np.array([
    (ts - PLANTING_START).total_seconds() for ts in timestamps
])

# Tính biomass → progress → height
biomass_arr = logistic_biomass(t_seconds_arr, total_seconds, INITIAL_BIOMASS, TARGET_BIOMASS)
progress_arr = (biomass_arr - INITIAL_BIOMASS) / (TARGET_BIOMASS - INITIAL_BIOMASS)
progress_arr = np.clip(progress_arr, 0, 1)
height_arr = MIN_HEIGHT_CM + progress_arr * (MAX_HEIGHT_CM - MIN_HEIGHT_CM)

# Thêm noise nhỏ (cảm biến thực tế)
noise_height = rng.normal(0, 0.05, n_samples)
height_arr = np.clip(height_arr + noise_height, 0, MAX_HEIGHT_CM)

# Mô phỏng dữ liệu môi trường
day_cycle_t = t_seconds_arr / 86400
temperature  = 25 + 5 * np.sin(2 * np.pi * day_cycle_t - np.pi / 2) + rng.normal(0, 0.5, n_samples)
air_humidity = 65 + 10 * np.cos(2 * np.pi * day_cycle_t) + rng.normal(0, 2, n_samples)
soil_moisture = 70 - 0.5 * day_cycle_t + rng.normal(0, 3, n_samples)
lux           = np.abs(5000 * np.sin(np.pi * day_cycle_t % 1) + rng.normal(0, 100, n_samples))

# Làm tròn
temperature   = np.round(temperature, 1)
air_humidity  = np.round(np.clip(air_humidity, 40, 95), 1)
soil_moisture = np.round(np.clip(soil_moisture, 30, 90), 1)
lux           = np.round(np.clip(lux, 0, 6000), 0)

# ── Tạo DataFrame ──────────────────────────────────────────────────────────────
df = pd.DataFrame({
    "_time":                 [ts.strftime("%Y-%m-%dT%H:%M:%SZ") for ts in timestamps],
    "planting_start_epoch":  [PLANTING_START_EPOCH_S] * n_samples,
    "plant_height_cm":       np.round(height_arr, 3),
    "temperature":           temperature,
    "air_humidity":          air_humidity,
    "soil_moisture":         soil_moisture,
    "lux":                   lux,
})

# ── Lưu file ───────────────────────────────────────────────────────────────────
out_dir = Path("sample_data")
out_dir.mkdir(exist_ok=True)
out_path = out_dir / "plant_growth_sample.csv"

df.to_csv(out_path, index=False)
print(f"✅  Đã tạo file CSV mẫu: {out_path}")
print(f"    → {len(df)} dòng | {len(df.columns)} cột")
print(f"    → Chiều cao: {df['plant_height_cm'].min():.2f} → {df['plant_height_cm'].max():.2f} cm")
print(f"    → Thời gian: {df['_time'].iloc[0]}  →  {df['_time'].iloc[-1]}")
print()
print("Để chạy phân tích:")
print(f"    python analyze_plant_growth.py --csv {out_path}")
