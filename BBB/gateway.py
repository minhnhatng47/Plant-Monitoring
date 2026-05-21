import os
import json
import paho.mqtt.client as mqtt
from influxdb_client import InfluxDBClient, Point, WriteOptions
from datetime import datetime

# ==========================================
# 1. CẤU HÌNH INFLUXDB CLOUD (Thay bằng thông tin của bạn)
# ==========================================
INFLUX_URL = "https://us-east-1-1.aws.cloud2.influxdata.com"
INFLUX_TOKEN = "6pSuWQaFLlWq6iRVfaRYEMwIO1DDEChBsG42HdDx5En6fuqpUx95j3xswbVNrcWxRrs_sizN6XXESjzNqcHzJA=="
INFLUX_ORG = "DEV_TEAM"
INFLUX_BUCKET = "digital_twin_data"

# ==========================================
# 2. CẤU HÌNH MQTT (Mosquitto nội bộ)
# ==========================================
MQTT_BROKER = "127.0.0.1" # Lắng nghe ngay trên chính BeagleBone
MQTT_PORT = 1883
MQTT_TOPIC = "cps/greenhouse/sensors"

# Khởi tạo InfluxDB Client (Ghi bất đồng bộ chống lag)
client_influx = InfluxDBClient(url=INFLUX_URL, token=INFLUX_TOKEN, org=INFLUX_ORG)
write_api = client_influx.write_api(write_options=WriteOptions(batch_size=5, flush_interval=1000))

def on_connect(client, userdata, flags, rc):
    if rc == 0:
        print("[MQTT] Đã kết nối thành công tới Mosquitto Broker Local!")
        client.subscribe(MQTT_TOPIC)
    else:
        print(f"[MQTT] Lỗi kết nối, mã lỗi: {rc}")

def on_message(client, userdata, msg):
    try:
        payload_str = msg.payload.decode('utf-8')
        payload = json.loads(payload_str)
        
        # Tự động quét các key trong JSON để tạo field cho InfluxDB
        point = Point("environment_sensor").tag("gateway", "BBB_Gateway").time(datetime.utcnow())
        
        for key, value in payload.items():
            if isinstance(value, (int, float)):
                point.field(key, float(value))
            elif isinstance(value, str):
                point.tag(key, value) # Nếu là chuỗi thì lưu làm tag phân loại

        # Đẩy lên Cloud
        write_api.write(bucket=INFLUX_BUCKET, org=INFLUX_ORG, record=point)
        print(f"[Cloud] Đã lưu: {payload}")
        
    except json.JSONDecodeError:
        print(f"[Lỗi] Dữ liệu MQTT không phải JSON chuẩn: {msg.payload}")
    except Exception as e:
        print(f"[Lỗi] Ngoại lệ khi xử lý: {e}")

# Khởi tạo và chạy MQTT Client
mqtt_client = mqtt.Client()
mqtt_client.on_connect = on_connect
mqtt_client.on_message = on_message

print("Đang khởi động Edge Gateway BBB -> InfluxDB Cloud...")
mqtt_client.connect(MQTT_BROKER, MQTT_PORT, 60)

try:
    mqtt_client.loop_forever()
except KeyboardInterrupt:
    print("\n[Hệ thống] Đang ngắt kết nối an toàn...")
    write_api.close()
    client_influx.close()
    mqtt_client.disconnect()
    print("Đã thoát.")
