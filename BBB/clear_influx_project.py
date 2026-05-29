from datetime import datetime, timezone
from influxdb_client import InfluxDBClient

from gateway import (
    INFLUX_URL_DEFAULT,
    INFLUX_TOKEN_DEFAULT,
    INFLUX_ORG_DEFAULT,
    INFLUX_BUCKET_DEFAULT,
)

client = InfluxDBClient(
    url=INFLUX_URL_DEFAULT,
    token=INFLUX_TOKEN_DEFAULT,
    org=INFLUX_ORG_DEFAULT,
)

delete_api = client.delete_api()

start = "1970-01-01T00:00:00Z"
stop = datetime.now(timezone.utc).isoformat()

measurements = [
    "sensors",
    "status",
    "actuator",
    "cmd",
    "dt",
]

for m in measurements:
    print("Deleting:", m)
    delete_api.delete(
        start=start,
        stop=stop,
        predicate=f'_measurement="{m}"',
        bucket=INFLUX_BUCKET_DEFAULT,
        org=INFLUX_ORG_DEFAULT,
    )

client.close()
print("DONE")