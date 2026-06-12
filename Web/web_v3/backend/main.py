"""
main.py — Plant Monitoring CPS Web API v1.1.0-rpi-topic-v2
===========================================================

Backend role:
- Read dashboard/history data from InfluxDB Cloud.
- Write Web/Unity commands into InfluxDB measurement `dt` with status=PENDING.
- Gateway polls `dt`, then publishes MQTT topic v2 to ESP32.

MQTT topic v2 reference:
- cps/greenhouse/brassica_01/telemetry/sensors
- cps/greenhouse/brassica_01/state/actuator
- cps/greenhouse/brassica_01/status/esp32
- cps/greenhouse/gateway/status
- cps/greenhouse/brassica_01/cmd/auto/pump
- cps/greenhouse/brassica_01/cmd/direct/pump
- cps/greenhouse/brassica_01/cmd/direct/light
- cps/greenhouse/brassica_01/cmd/config/planting_start

Run:
    uvicorn main:app --host 0.0.0.0 --port 8000 --reload
"""

from __future__ import annotations

import json
import os
import uuid
from datetime import datetime, timedelta, timezone
from io import StringIO
from typing import Any, Literal

import numpy as np
import pandas as pd
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException, Query
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from influxdb_client import InfluxDBClient, Point, WritePrecision
from influxdb_client.client.write_api import SYNCHRONOUS
from pydantic import BaseModel, Field

# =============================================================================
# CONFIG
# =============================================================================

load_dotenv()

API_VERSION = "1.1.0-rpi-topic-v2"
NODE_ID = os.getenv("NODE_ID", "BRASSICA_JUNCEA_01")
NODE_TOPIC_ID = os.getenv("NODE_TOPIC_ID", "brassica_01")
PLANT_NAME = os.getenv("PLANT_NAME", "Rau Cải Mầm (Brassica juncea)")

TOPIC_ROOT = "cps/greenhouse"
TOPICS = {
    "telemetry_sensors": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/telemetry/sensors",
    "state_actuator": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/state/actuator",
    "status_esp32": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/status/esp32",
    "status_gateway": f"{TOPIC_ROOT}/gateway/status",
    "cmd_auto_pump": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/cmd/auto/pump",
    "cmd_direct_pump": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/cmd/direct/pump",
    "cmd_direct_light": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/cmd/direct/light",
    "cmd_config_planting_start": f"{TOPIC_ROOT}/{NODE_TOPIC_ID}/cmd/config/planting_start",
}

INFLUX_URL = os.getenv("INFLUX_URL") or os.getenv("INFLUXDB_URL") or "https://us-east-1-1.aws.cloud2.influxdata.com"
INFLUX_TOKEN = os.getenv("INFLUX_TOKEN") or os.getenv("INFLUXDB_TOKEN") or ""
INFLUX_ORG = os.getenv("INFLUX_ORG") or os.getenv("INFLUXDB_ORG") or "DEV_TEAM"
INFLUX_BUCKET = os.getenv("INFLUX_BUCKET") or os.getenv("INFLUXDB_BUCKET") or "digital_twin_data"

MEAS_SENSORS = "sensors"
MEAS_STATUS = "status"
MEAS_ACTUATOR = "actuator"
MEAS_CMD = "cmd"
MEAS_DT = "dt"

WRITE_PRECISION_SECONDS = getattr(WritePrecision, "S", None) or getattr(WritePrecision, "SECONDS")

# =============================================================================
# FASTAPI
# =============================================================================

app = FastAPI(
    title="Plant Monitoring CPS Web API",
    description="FastAPI backend for Plant Monitoring CPS. Reads InfluxDB and queues Web commands into measurement dt.",
    version=API_VERSION,
)

cors_origins_raw = os.getenv(
    "CORS_ORIGINS",
    "http://localhost:5173,http://127.0.0.1:5173,http://192.168.4.1:5173",
)
CORS_ORIGINS = [x.strip() for x in cors_origins_raw.split(",") if x.strip()]

app.add_middleware(
    CORSMiddleware,
    allow_origins=CORS_ORIGINS or ["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# =============================================================================
# MODELS
# =============================================================================

class PumpCommand(BaseModel):
    state: Literal["ON", "OFF"]
    duration_s: int = Field(default=10, ge=0, le=15)
    reason: str = "web_manual"
    source: str = "web"


class LightCommand(BaseModel):
    state: Literal["ON", "OFF"]
    duration_s: int = Field(default=300, ge=0, le=1800)
    reason: str = "web_manual"
    source: str = "web"


class PlantingStartCommand(BaseModel):
    action: Literal["SET_NOW", "SET_EPOCH", "CLEAR", "GET"] = "SET_NOW"
    planting_start_epoch: int | None = Field(default=None, ge=1)
    reason: str = "web_planting_start"
    source: str = "web"

# =============================================================================
# HELPERS
# =============================================================================

def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def check_env() -> None:
    if not INFLUX_TOKEN:
        raise HTTPException(
            status_code=500,
            detail="Missing INFLUX_TOKEN. Create backend/.env or export INFLUX_TOKEN before running service.",
        )


def get_client() -> InfluxDBClient:
    check_env()
    return InfluxDBClient(url=INFLUX_URL, token=INFLUX_TOKEN, org=INFLUX_ORG)


def clean_value(value: Any) -> Any:
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, (np.floating,)):
        if np.isnan(value) or np.isinf(value):
            return None
        return float(value)
    if isinstance(value, float):
        if np.isnan(value) or np.isinf(value):
            return None
        return value
    if isinstance(value, pd.Timestamp):
        return value.isoformat()
    if isinstance(value, datetime):
        return value.isoformat()
    if pd.isna(value):
        return None
    return value


def normalize_dataframe(df: pd.DataFrame) -> pd.DataFrame:
    if df.empty:
        return df

    for col in ("result", "table", "_start", "_stop"):
        if col in df.columns:
            df = df.drop(columns=[col])

    if "_time" in df.columns:
        df["_time"] = pd.to_datetime(df["_time"], errors="coerce").dt.strftime("%Y-%m-%dT%H:%M:%SZ")

    return df.replace({np.nan: None})


def dataframe_to_records(df: pd.DataFrame) -> list[dict[str, Any]]:
    df = normalize_dataframe(df)
    records: list[dict[str, Any]] = []
    for row in df.to_dict(orient="records"):
        records.append({str(k): clean_value(v) for k, v in row.items()})
    return records


def build_range_part(minutes: int | None = None, date: str | None = None) -> str:
    if date:
        try:
            start = datetime.fromisoformat(date).replace(tzinfo=timezone.utc)
        except ValueError as exc:
            raise HTTPException(status_code=400, detail="date must be YYYY-MM-DD") from exc
        stop = start + timedelta(days=1)
        return f'|> range(start: {start.isoformat()}, stop: {stop.isoformat()})'

    safe_minutes = int(minutes or 720)
    safe_minutes = max(1, min(safe_minutes, 60 * 24 * 31))
    return f"|> range(start: -{safe_minutes}m)"


def query_measurement(
    measurement: str,
    minutes: int | None = 720,
    date: str | None = None,
    limit: int = 300,
) -> list[dict[str, Any]]:
    range_part = build_range_part(minutes=minutes, date=date)
    flux = f'''
from(bucket: "{INFLUX_BUCKET}")
  {range_part}
  |> filter(fn: (r) => r._measurement == "{measurement}")
  |> pivot(rowKey: ["_time"], columnKey: ["_field"], valueColumn: "_value")
  |> sort(columns: ["_time"], desc: false)
  |> limit(n: {int(limit)})
'''
    with get_client() as client:
        df = client.query_api().query_data_frame(flux, org=INFLUX_ORG)

    if isinstance(df, list):
        df = pd.concat(df, ignore_index=True) if df else pd.DataFrame()
    if df.empty:
        return []
    return dataframe_to_records(df)


def get_latest_record(measurement: str, minutes: int | None = 1440) -> dict[str, Any] | None:
    rows = query_measurement(measurement, minutes=minutes, limit=500)
    if not rows:
        return None
    return rows[-1]


def parse_status_record(record: dict[str, Any] | None) -> dict[str, Any] | None:
    if not record:
        return None
    value_json = record.get("value_json")
    if isinstance(value_json, str):
        try:
            parsed = json.loads(value_json)
            if isinstance(parsed, dict):
                parsed.setdefault("_time", record.get("_time"))
                parsed.setdefault("status_key", record.get("key"))
                return parsed
        except json.JSONDecodeError:
            pass
    return record


def write_dt_command(
    target: str,
    source: str,
    reason: str,
    state: str = "",
    duration_s: int = 0,
    action: str = "",
    planting_start_epoch: int | None = None,
) -> dict[str, Any]:
    command_id = f"web-{uuid.uuid4().hex[:12]}"
    target = target.strip().lower()

    point = (
        Point(MEAS_DT)
        .tag("node_id", NODE_ID)
        .tag("command_id", command_id)
        .tag("target", target)
        .tag("status", "PENDING")
        .field("source", source or "web")
        .field("reason", reason or "web_command")
        .time(datetime.now(timezone.utc), WRITE_PRECISION_SECONDS)
    )

    if target == "planting_start":
        action = (action or "SET_NOW").strip().upper()
        point = point.field("action", action)
        if action == "SET_EPOCH":
            if not planting_start_epoch:
                raise HTTPException(status_code=400, detail="SET_EPOCH requires planting_start_epoch")
            point = point.field("planting_start_epoch", int(planting_start_epoch))
    else:
        point = point.field("state", state.upper()).field("duration_s", int(duration_s))

    with get_client() as client:
        write_api = client.write_api(write_options=SYNCHRONOUS)
        write_api.write(bucket=INFLUX_BUCKET, org=INFLUX_ORG, record=point)

    return {
        "ok": True,
        "command_id": command_id,
        "target": target,
        "status": "PENDING",
        "source": source,
        "reason": reason,
        "state": state.upper() if state else None,
        "duration_s": duration_s if target != "planting_start" else None,
        "action": action if target == "planting_start" else None,
        "planting_start_epoch": planting_start_epoch,
        "created_at": utc_now_iso(),
        "message": "Command queued into InfluxDB measurement dt. Gateway will bridge it to MQTT topic v2.",
    }

# =============================================================================
# ROUTES
# =============================================================================

@app.get("/")
def root() -> dict[str, Any]:
    return {
        "name": "Plant Monitoring CPS Web API",
        "version": API_VERSION,
        "node_id": NODE_ID,
        "node_topic_id": NODE_TOPIC_ID,
        "plant": PLANT_NAME,
        "docs": "/docs",
    }


@app.get("/api/health")
def health() -> dict[str, Any]:
    check_env()
    try:
        with get_client() as client:
            ok = client.ping()
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"InfluxDB error: {exc}") from exc

    return {
        "status": "OK" if ok else "ERROR",
        "api_version": API_VERSION,
        "node_id": NODE_ID,
        "node_topic_id": NODE_TOPIC_ID,
        "plant": PLANT_NAME,
        "influx_url": INFLUX_URL,
        "org": INFLUX_ORG,
        "bucket": INFLUX_BUCKET,
        "measurements": [MEAS_SENSORS, MEAS_STATUS, MEAS_ACTUATOR, MEAS_CMD, MEAS_DT],
        "topics_v2": TOPICS,
        "timestamp": utc_now_iso(),
    }


@app.get("/api/dashboard/latest")
def dashboard_latest(
    minutes: int = Query(default=1440, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
) -> dict[str, Any]:
    sensors_rows = query_measurement(MEAS_SENSORS, minutes=minutes, date=date, limit=500)
    actuator_rows = query_measurement(MEAS_ACTUATOR, minutes=minutes, date=date, limit=500)
    status_rows = query_measurement(MEAS_STATUS, minutes=minutes, date=date, limit=500)
    cmd_rows = query_measurement(MEAS_CMD, minutes=minutes, date=date, limit=200)
    dt_rows = query_measurement(MEAS_DT, minutes=minutes, date=date, limit=200)

    return {
        "node_id": NODE_ID,
        "plant": PLANT_NAME,
        "api_version": API_VERSION,
        "sensors": sensors_rows[-1] if sensors_rows else None,
        "actuator": actuator_rows[-1] if actuator_rows else None,
        "status": parse_status_record(status_rows[-1]) if status_rows else None,
        "command_event": cmd_rows[-1] if cmd_rows else None,
        "dt_command": dt_rows[-1] if dt_rows else None,
        "counts": {
            "sensors": len(sensors_rows),
            "actuator": len(actuator_rows),
            "status": len(status_rows),
            "cmd": len(cmd_rows),
            "dt": len(dt_rows),
        },
        "timestamp": utc_now_iso(),
    }


@app.get("/api/history/{measurement}")
def history(
    measurement: Literal["sensors", "actuator", "status", "cmd", "dt"],
    minutes: int = Query(default=720, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
    limit: int = Query(default=300, ge=1, le=5000),
) -> dict[str, Any]:
    rows = query_measurement(measurement, minutes=minutes, date=date, limit=limit)
    if measurement == MEAS_STATUS:
        rows = [parse_status_record(row) or row for row in rows]
    return {
        "measurement": measurement,
        "count": len(rows),
        "data": rows,
    }


@app.get("/api/history/sensors")
def history_sensors(
    minutes: int = Query(default=720, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
    limit: int = Query(default=300, ge=1, le=5000),
) -> dict[str, Any]:
    rows = query_measurement(MEAS_SENSORS, minutes=minutes, date=date, limit=limit)
    return {"measurement": MEAS_SENSORS, "count": len(rows), "data": rows}


@app.get("/api/history/actuator")
def history_actuator(
    minutes: int = Query(default=720, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
    limit: int = Query(default=300, ge=1, le=5000),
) -> dict[str, Any]:
    rows = query_measurement(MEAS_ACTUATOR, minutes=minutes, date=date, limit=limit)
    return {"measurement": MEAS_ACTUATOR, "count": len(rows), "data": rows}


@app.get("/api/history/status")
def history_status(
    minutes: int = Query(default=720, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
    limit: int = Query(default=300, ge=1, le=5000),
) -> dict[str, Any]:
    rows = query_measurement(MEAS_STATUS, minutes=minutes, date=date, limit=limit)
    rows = [parse_status_record(row) or row for row in rows]
    return {"measurement": MEAS_STATUS, "count": len(rows), "data": rows}


@app.post("/api/command/pump")
def command_pump(cmd: PumpCommand) -> dict[str, Any]:
    duration = 0 if cmd.state == "OFF" else cmd.duration_s
    return write_dt_command(
        target="pump",
        state=cmd.state,
        duration_s=duration,
        reason=cmd.reason,
        source=cmd.source,
    )


@app.post("/api/command/light")
def command_light(cmd: LightCommand) -> dict[str, Any]:
    duration = 0 if cmd.state == "OFF" else cmd.duration_s
    return write_dt_command(
        target="light",
        state=cmd.state,
        duration_s=duration,
        reason=cmd.reason,
        source=cmd.source,
    )


@app.post("/api/command/planting-start")
def command_planting_start(cmd: PlantingStartCommand) -> dict[str, Any]:
    return write_dt_command(
        target="planting_start",
        action=cmd.action,
        planting_start_epoch=cmd.planting_start_epoch,
        reason=cmd.reason,
        source=cmd.source,
    )


@app.get("/api/export/sensors.csv")
def export_sensors_csv(
    minutes: int = Query(default=720, ge=1, le=60 * 24 * 31),
    date: str | None = Query(default=None),
) -> StreamingResponse:
    rows = query_measurement(MEAS_SENSORS, minutes=minutes, date=date, limit=5000)
    df = pd.DataFrame(rows)
    buffer = StringIO()
    df.to_csv(buffer, index=False)
    buffer.seek(0)
    filename = f"plant_sensors_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"
    return StreamingResponse(
        iter([buffer.getvalue()]),
        media_type="text/csv",
        headers={"Content-Disposition": f"attachment; filename={filename}"},
    )


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=True)
