# Plant-Monitoring-CPS

## Giới thiệu

**Plant-Monitoring-CPS** là mô hình hệ thống Cyber-Physical System (CPS) ứng dụng trong giám sát và chăm sóc cây trồng. Hệ thống tích hợp các công nghệ IoT, MQTT, Edge AI, cơ sở dữ liệu thời gian thực và mô hình Digital Twin trên Unity nhằm thu thập dữ liệu môi trường, xử lý tại biên, điều khiển thiết bị và trực quan hóa trạng thái cây trồng theo thời gian thực.

Đề tài được xây dựng cho mô hình chăm sóc rau cải mầm (*Brassica juncea*) trong môi trường thử nghiệm. Hệ thống có khả năng đo các thông số như nhiệt độ, độ ẩm không khí, độ ẩm đất và cường độ ánh sáng. Dữ liệu được gửi từ ESP32 về Raspberry Pi thông qua MQTT, sau đó được xử lý, lưu trữ, hiển thị trên Web Dashboard và đồng bộ với mô hình Digital Twin trong Unity.

## Mục tiêu dự án

Dự án hướng đến việc xây dựng một mô hình CPS hoàn chỉnh cho chăm sóc cây trồng thông minh với các mục tiêu chính:

- Thu thập dữ liệu môi trường từ các cảm biến.
- Truyền dữ liệu giữa ESP32, Raspberry Pi, Web Dashboard và Unity thông qua MQTT.
- Lưu trữ dữ liệu cảm biến theo thời gian thực bằng InfluxDB.
- Xử lý dữ liệu tại biên bằng Raspberry Pi.
- Ứng dụng mô hình Machine Learning để hỗ trợ quyết định bật/tắt bơm tưới.
- Điều khiển thiết bị chấp hành như relay, máy bơm và đèn.
- Xây dựng mô hình Digital Twin 3D trên Unity để mô phỏng trạng thái hệ thống.
- Hỗ trợ điều khiển hai chiều từ Web Dashboard và Unity xuống phần cứng thực tế.

## Kiến trúc hệ thống

Luồng hoạt động tổng quát của hệ thống:

```text
Cảm biến môi trường
        ↓
      ESP32
        ↓ MQTT
Raspberry Pi / MQTT Broker
        ↓
Edge Processing + AI Model
        ↓
InfluxDB + FastAPI Backend
        ↓
Web Dashboard / Unity Digital Twin
        ↓
Lệnh điều khiển thiết bị
        ↓
ESP32 → Relay → Bơm / Đèn
```

Trong đó:

- **ESP32** đọc dữ liệu cảm biến và điều khiển thiết bị chấp hành.
- **MQTT Broker Mosquitto** làm trung gian truyền nhận dữ liệu.
- **Raspberry Pi 4B** đóng vai trò Edge Gateway để xử lý dữ liệu, chạy AI và lưu trữ.
- **InfluxDB** lưu dữ liệu cảm biến dạng chuỗi thời gian.
- **FastAPI Backend** cung cấp API và WebSocket cho giao diện.
- **Web Dashboard** hiển thị dữ liệu và điều khiển thiết bị.
- **Unity Digital Twin** mô phỏng nhà kính, cây trồng và trạng thái thiết bị trong môi trường 3D.

## Chức năng chính

### 1. Giám sát dữ liệu môi trường

Hệ thống thu thập các thông số chính:

- Nhiệt độ không khí
- Độ ẩm không khí
- Độ ẩm đất
- Cường độ ánh sáng
- Trạng thái bơm
- Trạng thái đèn
- Trạng thái hệ thống

### 2. Truyền nhận dữ liệu MQTT

ESP32 publish dữ liệu cảm biến lên MQTT Broker. Raspberry Pi, Web Dashboard và Unity có thể subscribe các topic tương ứng để nhận dữ liệu realtime.

MQTT được sử dụng để:

- Gửi dữ liệu cảm biến từ ESP32 lên Raspberry Pi.
- Gửi lệnh điều khiển từ Raspberry Pi/Web/Unity xuống ESP32.
- Đồng bộ trạng thái thiết bị giữa phần cứng và giao diện.
- Kiểm tra khả năng truyền nhận dữ liệu hai chiều.

### 3. Edge AI trên Raspberry Pi

Raspberry Pi xử lý dữ liệu tại biên và chạy mô hình Machine Learning để hỗ trợ quyết định tưới nước. Mô hình AI sử dụng dữ liệu cảm biến để dự đoán trạng thái cần tưới hoặc không cần tưới.

Quy trình AI:

```text
Dữ liệu cảm biến → Tiền xử lý → Random Forest Model → Dự đoán trạng thái bơm → Gửi lệnh điều khiển
```

Mô hình chính được sử dụng:

- Random Forest

Các tiêu chí đánh giá:

- Accuracy
- Precision
- Recall
- F1-score
- Confusion Matrix
- Feature Importance

### 4. Điều khiển thiết bị

Hệ thống có thể điều khiển:

- Máy bơm nước
- Đèn chiếu sáng
- Relay
- Chức năng START hệ thống

Thiết bị có thể được điều khiển từ:

- Web Dashboard
- Unity Digital Twin
- Logic tự động từ AI
- Lệnh MQTT

### 5. Web Dashboard

Web Dashboard dùng để:

- Hiển thị dữ liệu cảm biến realtime.
- Theo dõi trạng thái bơm và đèn.
- Gửi lệnh điều khiển thiết bị.
- Quan sát lịch sử dữ liệu.
- Kiểm tra trạng thái hoạt động của hệ thống.

### 6. Unity Digital Twin

Unity Digital Twin mô phỏng hệ thống chăm sóc cây trồng trong môi trường 3D.

Các chức năng chính:

- Hiển thị mô hình nhà kính.
- Hiển thị dữ liệu cảm biến realtime.
- Hiển thị trạng thái bơm, đèn và cây trồng.
- Mô phỏng các giai đoạn phát triển của cây.
- Điều khiển thiết bị từ giao diện Unity.
- Đồng bộ trạng thái giữa mô hình số và hệ thống vật lý.

## Phần cứng sử dụng

| Thiết bị | Vai trò |
|---|---|
| ESP32 | Đọc cảm biến, gửi dữ liệu MQTT, điều khiển relay |
| Raspberry Pi 4B | Gateway xử lý biên, MQTT Broker, AI, lưu trữ dữ liệu |
| DHT11 | Đo nhiệt độ và độ ẩm không khí |
| Cảm biến độ ẩm đất | Đo độ ẩm đất |
| BH1750 | Đo cường độ ánh sáng |
| ADS1115 | Chuyển đổi tín hiệu analog sang digital |
| DS3231 | Cung cấp thời gian thực |
| Relay | Đóng/ngắt thiết bị chấp hành |
| Máy bơm mini | Tưới nước tự động |
| Đèn chiếu sáng | Bổ sung ánh sáng cho cây |
| Nguồn DC | Cấp nguồn cho hệ thống |

## Công nghệ sử dụng

| Thành phần | Công nghệ |
|---|---|
| Vi điều khiển | ESP32 |
| Gateway | Raspberry Pi 4B |
| Giao thức truyền thông | MQTT |
| MQTT Broker | Mosquitto |
| Backend | FastAPI |
| Database | InfluxDB |
| AI / Machine Learning | Python, Random Forest |
| Dashboard | Web Dashboard |
| Digital Twin | Unity |
| Dữ liệu huấn luyện | CSV |
| Ngôn ngữ lập trình | C/C++, Python, C#, JavaScript |

## Cấu trúc thư mục

```text
Plant-Monitoring/
│
├── BBB/
│   └── Xử lý biên, gateway hoặc các file liên quan đến Raspberry Pi
│
├── Hardware/
│   └── node_sensor/
│       └── Mã nguồn và thiết kế phần cảm biến
│
├── Web/
│   └── Giao diện Web Dashboard
│
├── esp32/
│   └── Firmware ESP32 đọc cảm biến và điều khiển thiết bị
│
├── Plant_DigitalTwin/
│   ├── Assets/
│   ├── Packages/
│   ├── ProjectSettings/
│   ├── file_csv/
│   ├── analyze_growth_from_old_csv.py
│   ├── analyze_plant_growth.py
│   └── generate_sample_csv.py
│
├── README.md
└── .gitignore
```

## Luồng dữ liệu hệ thống

1. ESP32 đọc dữ liệu từ cảm biến.
2. ESP32 đóng gói dữ liệu thành JSON.
3. ESP32 publish dữ liệu lên MQTT Broker.
4. Raspberry Pi subscribe dữ liệu từ MQTT Broker.
5. Raspberry Pi xử lý dữ liệu và lưu vào InfluxDB.
6. Mô hình AI dự đoán trạng thái cần tưới hoặc không cần tưới.
7. Raspberry Pi gửi lệnh điều khiển xuống ESP32 qua MQTT.
8. ESP32 điều khiển relay để bật/tắt bơm hoặc đèn.
9. Web Dashboard và Unity Digital Twin cập nhật trạng thái realtime.
10. Người dùng có thể điều khiển ngược lại từ Web hoặc Unity.

## Cài đặt và chạy hệ thống

### 1. Clone repository

```bash
git clone https://github.com/minhnhatng47/Plant-Monitoring.git
cd Plant-Monitoring
```

### 2. Cài đặt MQTT Broker trên Raspberry Pi

```bash
sudo apt update
sudo apt install mosquitto mosquitto-clients
sudo systemctl enable mosquitto
sudo systemctl start mosquitto
```

Kiểm tra trạng thái Mosquitto:

```bash
sudo systemctl status mosquitto
```

### 3. Chạy Backend / Gateway

Di chuyển vào thư mục chứa chương trình xử lý trên Raspberry Pi, sau đó cài các thư viện cần thiết:

```bash
pip install -r requirements.txt
```

Chạy chương trình:

```bash
python main.py
```

Lưu ý: tên file chạy thực tế có thể thay đổi tùy theo cấu trúc code trong thư mục gateway/backend.

### 4. Nạp code ESP32

Mở thư mục `esp32/` bằng Arduino IDE hoặc PlatformIO.

Cấu hình các thông tin cần thiết trong code:

```cpp
WiFi SSID
WiFi Password
MQTT Broker IP
MQTT Port
MQTT Topics
```

Sau đó nạp chương trình vào ESP32.

### 5. Chạy Web Dashboard

Mở thư mục `Web/`.

Nếu là web tĩnh, có thể mở trực tiếp:

```text
index.html
```

Nếu có server riêng, chạy theo file hướng dẫn hoặc file cấu hình trong thư mục `Web`.

### 6. Mở Unity Digital Twin

Mở Unity Hub, chọn thư mục:

```text
Plant_DigitalTwin/
```

Sau đó mở project bằng Unity.

Các thư mục quan trọng cần giữ nguyên:

```text
Assets/
Packages/
ProjectSettings/
```

Không nên xóa các file `.meta` trong project Unity vì Unity cần các file này để nhận diện asset.

## Dữ liệu AI

Dữ liệu huấn luyện AI được lưu dưới dạng CSV trong thư mục:

```text
Plant_DigitalTwin/file_csv/
```

Các script Python dùng để phân tích dữ liệu sinh trưởng:

```text
analyze_growth_from_old_csv.py
analyze_plant_growth.py
generate_sample_csv.py
```

Dữ liệu được sử dụng để:

- Kiểm tra dữ liệu thiếu.
- Phân tích đặc trưng đầu vào.
- Thống kê trạng thái bơm.
- Huấn luyện mô hình Random Forest.
- Đánh giá mô hình AI.
- Mô phỏng tăng trưởng cây trong Unity.

## Kết quả đạt được

Dự án đã xây dựng được mô hình CPS chăm sóc cây trồng gồm đầy đủ các thành phần:

- Phần cứng cảm biến và thiết bị chấp hành.
- Truyền nhận dữ liệu hai chiều qua MQTT.
- Lưu trữ dữ liệu cảm biến bằng InfluxDB.
- Điều khiển thiết bị từ Web Dashboard.
- Điều khiển thiết bị từ Unity Digital Twin.
- Huấn luyện mô hình Random Forest để hỗ trợ quyết định tưới.
- Mô phỏng trạng thái cây trồng và thiết bị trên Unity.
- Hiển thị dữ liệu realtime và trạng thái sinh trưởng của cây.

## Hạn chế

Một số hạn chế hiện tại của hệ thống:

- Mô hình mới triển khai ở quy mô thử nghiệm.
- Cảm biến sử dụng có độ chính xác phù hợp cho mô hình học tập, chưa phải thiết bị công nghiệp.
- AI chủ yếu tập trung vào quyết định bật/tắt bơm, chưa mở rộng sang nhận diện bệnh cây hoặc tối ưu đa mục tiêu.
- Digital Twin chủ yếu mô phỏng trạng thái và tăng trưởng cơ bản, chưa mô phỏng đầy đủ các quá trình sinh học phức tạp.
- Hệ thống chưa triển khai bảo mật nâng cao cho MQTT, API và dashboard.

## Hướng phát triển

Trong tương lai, hệ thống có thể được mở rộng theo các hướng:

- Bổ sung thêm cảm biến pH, EC, CO2 hoặc camera.
- Nâng cấp mô hình AI để dự đoán sinh trưởng cây trồng.
- Ứng dụng Computer Vision để nhận diện bệnh cây.
- Triển khai MQTT bảo mật bằng username/password hoặc TLS.
- Xây dựng dashboard hoàn chỉnh hơn với phân quyền người dùng.
- Đồng bộ dữ liệu lên cloud để theo dõi từ xa.
- Tối ưu mô hình Digital Twin để mô phỏng chính xác hơn.
- Triển khai hệ thống trên mô hình nhà kính quy mô lớn hơn.

## Thành viên nhóm

| STT | Họ và tên | MSSV |
|---|---|---|
| 1 | Lê Lữ Nhật An | 23139001 |
| 2 | Nguyễn Trần Minh Đức | 23139012 |
| 3 | Nguyễn Minh Nhật | 23139032 |
| 4 | Trà Hồng Phượng | 23139035 |
| 5 | Lê Đức Trí | 23139049 |

## Thông tin môn học

- Môn học: Hệ thống CPS
- Đề tài: Thiết kế và thi công mô hình hệ thống CPS tích hợp IoT, AI và Unity
- Giảng viên hướng dẫn: ThS. Trịnh Quốc Thanh
- Trường: Đại học Công nghệ Kỹ thuật TP. Hồ Chí Minh
- Khoa: Điện - Điện tử

## License

Dự án được thực hiện phục vụ mục đích học tập, nghiên cứu và thực nghiệm trong môn học Hệ thống CPS.
