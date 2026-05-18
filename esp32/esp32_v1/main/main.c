/**
 * @file main.c
 * @brief ESP32 Firmware - Hệ thống CPS chăm sóc cây trồng
 *        Sensors: DHT11, BH1750, ADS1115 (4x cảm biến độ ẩm đất)
 *        Actuator: Relay (máy bơm nước)
 *        Protocol: MQTT over WiFi → Raspberry Pi (Mosquitto Broker)
 *
 * @project CPS Plant Care - HCMUTE 2026
 */

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "freertos/semphr.h"

#include "esp_system.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_log.h"
#include "nvs_flash.h"
#include "esp_timer.h"

#include "driver/i2c.h"
#include "driver/gpio.h"

#include "mqtt_client.h"
#include "i2cdev.h"  // ESP-IDF component from esp-idf-lib

/* ---------- Component headers (thư viện nội bộ) ---------- */
#include <ads111x.h> 
#include "bh1750.h"
#include "esp32-dht11.h"

/* ===================== CẤU HÌNH HỆ THỐNG ===================== */

/* --- WiFi --- */
#define WIFI_SSID           "anhDucdeptraivcloz"
#define WIFI_PASSWORD       "ducnguyen281025"
#define WIFI_MAX_RETRY      5

/* --- MQTT Broker (Raspberry Pi) --- */
#define MQTT_BROKER_URI     "mqtt://192.168.1.100"   // IP của Raspberry Pi
#define MQTT_PORT           1883
#define MQTT_USERNAME       ""                        // để trống nếu không có auth
#define MQTT_PASSWORD       ""

/* --- MQTT Topics --- */
#define TOPIC_SENSOR_DATA   "greenhouse/sensors"     // ESP32 publish dữ liệu cảm biến
#define TOPIC_STATUS        "greenhous                                              e/status"      // ESP32 publish trạng thái
#define TOPIC_CMD_PUMP      "greenhouse/cmd/pump"    // Raspberry Pi publish lệnh bơm
#define TOPIC_CMD_LIGHT     "greenhouse/cmd/light"   // Raspberry Pi publish lệnh đèn

/* --- Chân GPIO --- */
#define I2C_MASTER_SDA      21
#define I2C_MASTER_SCL      22
#define I2C_MASTER_PORT     I2C_NUM_0
#define I2C_MASTER_FREQ_HZ  100000

#define DHT11_GPIO          4
#define RELAY_PUMP_GPIO     26    // Relay máy bơm nước
#define RELAY_LIGHT_GPIO    27    // Relay đèn LED bổ sung

/* --- ADS1115 (ADC mở rộng) --- */
#define ADS1115_I2C_ADDR    0x48  // ADDR pin → GND
#define SOIL_SENSOR_COUNT   4     // Số lượng cảm biến độ ẩm đất

// 4 kênh độ ẩm đất: AIN0..AIN3 (single-ended vs GND)




// Đổi kiểu dữ liệu mảng MUX sang cấu trúc mới của ads111x
static const ads111x_mux_t SOIL_MUX[SOIL_SENSOR_COUNT] = {
    ADS111X_MUX_0_GND,   // AIN0: Vị trí 1
    ADS111X_MUX_1_GND,   // AIN1: Vị trí 2
    ADS111X_MUX_2_GND,   // AIN2: Vị trí 3
    ADS111X_MUX_3_GND,   // AIN3: Vị trí 4
};


/* --- BH1750 (Ánh sáng) --- */
#define BH1750_I2C_ADDR     BH1750_ADDR_LO      // ADDR pin → GND

/* --- Ngưỡng cảnh báo cây giá đỗ --- */
#define SOIL_DRY_THRESHOLD      30.0f   // % độ ẩm đất, dưới ngưỡng → bật bơm
#define SOIL_WET_THRESHOLD      70.0f   // % độ ẩm đất, trên ngưỡng → tắt bơm
#define TEMP_HIGH_THRESHOLD     35.0f   // °C nhiệt độ cao
#define LIGHT_LOW_THRESHOLD     200     // lux ánh sáng thấp

/* --- Chu kỳ thu thập --- */
#define SENSOR_READ_PERIOD_MS   5000    // Đọc cảm biến mỗi 5 giây
#define MQTT_PUBLISH_PERIOD_MS  10000   // Gửi dữ liệu mỗi 10 giây

/* ===================== BIẾN TOÀN CỤC ===================== */

static const char *TAG = "CPS_MAIN";

/* --- Event groups --- */
static EventGroupHandle_t s_wifi_event_group;
#define WIFI_CONNECTED_BIT  BIT0
#define WIFI_FAIL_BIT       BIT1

/* --- MQTT client --- */
static esp_mqtt_client_handle_t mqtt_client = NULL;
static bool mqtt_connected = false;

/* --- Mutex bảo vệ dữ liệu cảm biến --- */
static SemaphoreHandle_t sensor_mutex;

/* Khai báo biến cấu hình ADS1115 theo định dạng i2c_dev_t mới */
static i2c_dev_t ads_dev = {0};

/* --- Cấu trúc dữ liệu cảm biến --- */
typedef struct {
    float    temperature;               // DHT11 nhiệt độ (°C)
    float    humidity_air;              // DHT11 độ ẩm không khí (%)
    float    humidity_soil[SOIL_SENSOR_COUNT]; // ADS1115 độ ẩm đất 4 vị trí (%)
    uint16_t light_lux;                 // BH1750 ánh sáng (lux)
    bool     pump_state;                // Trạng thái máy bơm
    bool     light_relay_state;         // Trạng thái relay đèn
} sensor_data_t;

static sensor_data_t g_sensor_data = {0};

/* --- Biếm đếm retry WiFi --- */
static int s_retry_num = 0;

/* ===================== KHAI BÁO HÀM ===================== */

static void wifi_init(void);
static void mqtt_init(void);
static void i2c_master_init(void);
static void relay_init(void);
static void sensor_task(void *pvParameters);
static void mqtt_publish_task(void *pvParameters);
static void relay_auto_control(sensor_data_t *data);
static float ads1115_to_soil_percent(double voltage);
static void publish_sensor_json(void);

/* ===================== WIFI ===================== */

static void wifi_event_handler(void *arg, esp_event_base_t event_base,
                               int32_t event_id, void *event_data)
{
    if (event_base == WIFI_EVENT && event_id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (event_base == WIFI_EVENT && event_id == WIFI_EVENT_STA_DISCONNECTED) {
        if (s_retry_num < WIFI_MAX_RETRY) {
            esp_wifi_connect();
            s_retry_num++;
            ESP_LOGW(TAG, "WiFi mất kết nối, thử lại lần %d...", s_retry_num);
        } else {
            xEventGroupSetBits(s_wifi_event_group, WIFI_FAIL_BIT);
            ESP_LOGE(TAG, "Không thể kết nối WiFi sau %d lần thử", WIFI_MAX_RETRY);
        }
        mqtt_connected = false;
    } else if (event_base == IP_EVENT && event_id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)event_data;
        ESP_LOGI(TAG, "Đã kết nối WiFi, IP: " IPSTR, IP2STR(&event->ip_info.ip));
        s_retry_num = 0;
        xEventGroupSetBits(s_wifi_event_group, WIFI_CONNECTED_BIT);
    }
}

static void wifi_init(void)
{
    s_wifi_event_group = xEventGroupCreate();

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));

    esp_event_handler_instance_t instance_any_id;
    esp_event_handler_instance_t instance_got_ip;
    ESP_ERROR_CHECK(esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID,
                                                        &wifi_event_handler, NULL, &instance_any_id));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP,
                                                        &wifi_event_handler, NULL, &instance_got_ip));

    wifi_config_t wifi_config = {
        .sta = {
            .ssid     = WIFI_SSID,
            .password = WIFI_PASSWORD,
            .threshold.authmode = WIFI_AUTH_WPA2_PSK,
        },
    };
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi_config));
    ESP_ERROR_CHECK(esp_wifi_start());

    ESP_LOGI(TAG, "Đang kết nối WiFi...");
    EventBits_t bits = xEventGroupWaitBits(s_wifi_event_group,
                                           WIFI_CONNECTED_BIT | WIFI_FAIL_BIT,
                                           pdFALSE, pdFALSE, portMAX_DELAY);
    if (bits & WIFI_CONNECTED_BIT) {
        ESP_LOGI(TAG, "WiFi kết nối thành công");
    } else {
        ESP_LOGW(TAG, "WiFi không kết nối được - hệ thống chạy offline (Edge AI mode)");
    }
}

/* ===================== MQTT ===================== */

static void mqtt_event_handler(void *handler_args, esp_event_base_t base,
                               int32_t event_id, void *event_data)
{
    esp_mqtt_event_handle_t event = (esp_mqtt_event_handle_t)event_data;

    switch ((esp_mqtt_event_id_t)event_id) {
        case MQTT_EVENT_CONNECTED:
            ESP_LOGI(TAG, "MQTT kết nối thành công tới Broker");
            mqtt_connected = true;

            /* Subscribe các topic điều khiển từ Raspberry Pi */
            esp_mqtt_client_subscribe(mqtt_client, TOPIC_CMD_PUMP, 1);
            esp_mqtt_client_subscribe(mqtt_client, TOPIC_CMD_LIGHT, 1);
            ESP_LOGI(TAG, "Đã subscribe: %s, %s", TOPIC_CMD_PUMP, TOPIC_CMD_LIGHT);
            break;

        case MQTT_EVENT_DISCONNECTED:
            ESP_LOGW(TAG, "MQTT mất kết nối - chuyển sang chế độ tự chủ (Edge AI)");
            mqtt_connected = false;
            break;

        case MQTT_EVENT_DATA:
            /* Nhận lệnh điều khiển từ Raspberry Pi / Digital Twin */
            ESP_LOGI(TAG, "Nhận lệnh topic: %.*s | data: %.*s",
                     event->topic_len, event->topic,
                     event->data_len, event->data);

            char topic[64] = {0};
            char payload[32] = {0};
            strncpy(topic,   event->topic, event->topic_len);
            strncpy(payload, event->data,  event->data_len);

            if (xSemaphoreTake(sensor_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
                if (strcmp(topic, TOPIC_CMD_PUMP) == 0) {
                    if (strcmp(payload, "ON") == 0) {
                        gpio_set_level(RELAY_PUMP_GPIO, 1);
                        g_sensor_data.pump_state = true;
                        ESP_LOGI(TAG, ">>> Máy bơm: BẬT (lệnh từ Raspberry Pi)");
                    } else if (strcmp(payload, "OFF") == 0) {
                        gpio_set_level(RELAY_PUMP_GPIO, 0);
                        g_sensor_data.pump_state = false;
                        ESP_LOGI(TAG, ">>> Máy bơm: TẮT (lệnh từ Raspberry Pi)");
                    }
                } else if (strcmp(topic, TOPIC_CMD_LIGHT) == 0) {
                    if (strcmp(payload, "ON") == 0) {
                        gpio_set_level(RELAY_LIGHT_GPIO, 1);
                        g_sensor_data.light_relay_state = true;
                        ESP_LOGI(TAG, ">>> Đèn LED: BẬT (lệnh từ Raspberry Pi)");
                    } else if (strcmp(payload, "OFF") == 0) {
                        gpio_set_level(RELAY_LIGHT_GPIO, 0);
                        g_sensor_data.light_relay_state = false;
                        ESP_LOGI(TAG, ">>> Đèn LED: TẮT (lệnh từ Raspberry Pi)");
                    }
                }
                xSemaphoreGive(sensor_mutex);
            }
            break;

        case MQTT_EVENT_ERROR:
            ESP_LOGE(TAG, "Lỗi MQTT");
            break;

        default:
            break;
    }
}

static void mqtt_init(void)
{
    esp_mqtt_client_config_t mqtt_cfg = {
        .broker.address.uri  = MQTT_BROKER_URI,
        .broker.address.port = MQTT_PORT,
        .credentials = {
            .username = MQTT_USERNAME,
            .authentication.password = MQTT_PASSWORD,
        },
        .session = {
            .keepalive = 60,
            .last_will = {
                .topic = TOPIC_STATUS,
                .msg   = "{\"status\":\"offline\",\"device\":\"ESP32\"}",
                .qos   = 1,
                .retain = 1,
            },
        },
    };

    mqtt_client = esp_mqtt_client_init(&mqtt_cfg);
    esp_mqtt_client_register_event(mqtt_client, ESP_EVENT_ANY_ID, mqtt_event_handler, NULL);
    esp_mqtt_client_start(mqtt_client);
    ESP_LOGI(TAG, "MQTT client khởi động, kết nối tới: %s", MQTT_BROKER_URI);
}

/* ===================== I2C & RELAY INIT ===================== */

static void i2c_master_init(void)
{
    /* esp-idf-lib dùng i2cdev, cần gọi i2cdev_init() */
    ESP_ERROR_CHECK(i2cdev_init());
    ESP_LOGI(TAG, "I2C Master khởi tạo xong (SDA=%d, SCL=%d)", I2C_MASTER_SDA, I2C_MASTER_SCL);
}

static void relay_init(void)
{
    gpio_config_t io_conf = {
        .pin_bit_mask = (1ULL << RELAY_PUMP_GPIO) | (1ULL << RELAY_LIGHT_GPIO),
        .mode         = GPIO_MODE_OUTPUT,
        .pull_up_en   = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_ENABLE,
        .intr_type    = GPIO_INTR_DISABLE,
    };
    gpio_config(&io_conf);

    /* Tắt relay khi khởi động */
    gpio_set_level(RELAY_PUMP_GPIO,  0);
    gpio_set_level(RELAY_LIGHT_GPIO, 0);
    ESP_LOGI(TAG, "Relay khởi tạo xong (Pump=GPIO%d, Light=GPIO%d)", RELAY_PUMP_GPIO, RELAY_LIGHT_GPIO);
}

/* ===================== CHUYỂN ĐỔI GIÁ TRỊ ===================== */

/**
 * @brief Chuyển điện áp ADS1115 → phần trăm độ ẩm đất
 *        Cần hiệu chỉnh theo đặc tính cảm biến thực tế:
 *        - Khô hoàn toàn (trong không khí): ~3.3V → 0%
 *        - Ướt hoàn toàn (ngập nước):       ~1.2V → 100%
 */
static float ads1115_to_soil_percent(double voltage)
{
    const float V_DRY = 3.3f;
    const float V_WET = 1.2f;

    if (voltage >= V_DRY) return 0.0f;
    if (voltage <= V_WET) return 100.0f;

    return (V_DRY - (float)voltage) / (V_DRY - V_WET) * 100.0f;
}

/* ===================== ĐIỀU KHIỂN TỰ ĐỘNG (EDGE AI ĐƠN GIẢN) ===================== */

/**
 * @brief Logic điều khiển tự động dựa trên ngưỡng (Agentic AI tầng cơ bản)
 *        Khi mất kết nối MQTT, vẫn đảm bảo cây được chăm sóc.
 *        Khi có kết nối, Raspberry Pi sẽ override bằng lệnh MQTT từ mô hình ML.
 */
static void relay_auto_control(sensor_data_t *data)
{
    if (mqtt_connected) {
        /* Khi có kết nối: để Raspberry Pi (Edge AI - XGBoost/LightGBM) quyết định */
        return;
    }

    /* --- Chế độ OFFLINE: dùng ngưỡng cứng tại ESP32 --- */
    ESP_LOGW(TAG, "[OFFLINE MODE] Điều khiển tự động tại biên ESP32");

    /* Tính trung bình độ ẩm đất 4 vị trí */
    float avg_soil = 0.0f;
    for (int i = 0; i < SOIL_SENSOR_COUNT; i++) {
        avg_soil += data->humidity_soil[i];
    }
    avg_soil /= SOIL_SENSOR_COUNT;

    /* Điều khiển bơm theo độ ẩm đất trung bình */
    if (avg_soil < SOIL_DRY_THRESHOLD && !data->pump_state) {
        gpio_set_level(RELAY_PUMP_GPIO, 1);
        data->pump_state = true;
        ESP_LOGI(TAG, ">>> [AUTO] Đất khô (avg=%.1f%%) - BẬT máy bơm", avg_soil);
    } else if (avg_soil > SOIL_WET_THRESHOLD && data->pump_state) {
        gpio_set_level(RELAY_PUMP_GPIO, 0);
        data->pump_state = false;
        ESP_LOGI(TAG, ">>> [AUTO] Đất đủ ẩm (avg=%.1f%%) - TẮT máy bơm", avg_soil);
    }

    /* Điều khiển đèn theo ánh sáng */
    if (data->light_lux < LIGHT_LOW_THRESHOLD && !data->light_relay_state) {
        gpio_set_level(RELAY_LIGHT_GPIO, 1);
        data->light_relay_state = true;
        ESP_LOGI(TAG, ">>> [AUTO] Thiếu sáng (%d lux) - BẬT đèn LED", data->light_lux);
    } else if (data->light_lux >= LIGHT_LOW_THRESHOLD && data->light_relay_state) {
        gpio_set_level(RELAY_LIGHT_GPIO, 0);
        data->light_relay_state = false;
        ESP_LOGI(TAG, ">>> [AUTO] Đủ sáng (%d lux) - TẮT đèn LED", data->light_lux);
    }
}

/* ===================== TASK: ĐỌC CẢM BIẾN ===================== */

static void sensor_task(void *pvParameters)
{
    /* --- Khởi tạo DHT11 --- */
    dht11_t dht11 = { .dht11_pin = DHT11_GPIO, .temperature = 0.0f, .humidity = 0.0f };

    /* --- Khởi tạo BH1750 --- */
    i2c_dev_t bh1750_dev = {0};
    ESP_ERROR_CHECK(bh1750_init_desc(&bh1750_dev, BH1750_I2C_ADDR, I2C_MASTER_PORT, I2C_MASTER_SDA, I2C_MASTER_SCL));
    ESP_ERROR_CHECK(bh1750_power_on(&bh1750_dev));
    ESP_ERROR_CHECK(bh1750_setup(&bh1750_dev, BH1750_MODE_CONTINUOUS, BH1750_RES_HIGH));

    /* --- KHỞI TẠO ADS1115 (Sử dụng driver ads111x mới tương thích v6.0) --- */
    ESP_ERROR_CHECK(ads111x_init_desc(&ads_dev, ADS1115_I2C_ADDR, I2C_MASTER_PORT, I2C_MASTER_SDA, I2C_MASTER_SCL));
    // Cấu hình ADS1115 (sửa lại tên biến cho đúng chuẩn driver mới)
    ESP_ERROR_CHECK(ads111x_set_gain(&ads_dev, ADS111X_GAIN_4V096)); // Dải ±4.096V 
    ESP_ERROR_CHECK(ads111x_set_mode(&ads_dev, ADS111X_MODE_SINGLE_SHOT));
    ESP_ERROR_CHECK(ads111x_set_data_rate(&ads_dev, ADS111X_DATA_RATE_128)); // 128 samples per second

    uint16_t light_lux = 0;
    int16_t  raw_soil_val = 0;
    double   soil_v[SOIL_SENSOR_COUNT] = {0.0};
    float    soil_pct[SOIL_SENSOR_COUNT] = {0.0f};

    ESP_LOGI(TAG, "Sensor task bắt đầu...");
    vTaskDelay(pdMS_TO_TICKS(2000)); 

    while (1) {
        /* ---- 1. Đọc DHT11 (Giữ nguyên cấu trúc của bạn) ---- */
        int dht_ret = dht11_read(&dht11, 3);
        
        /* ---- 2. Đọc BH1750 (Giữ nguyên cấu trúc của bạn) ---- */
        esp_err_t bh_ret = bh1750_read(&bh1750_dev, &light_lux);

        /* ---- 3. Đọc ADS1115 - 4 kênh độ ẩm đất theo API mới ---- */
        for (int i = 0; i < SOIL_SENSOR_COUNT; i++) {
            // Chuyển kênh đọc Multiplexer
            ads111x_set_input_mux(&ads_dev, SOIL_MUX[i]);
            vTaskDelay(pdMS_TO_TICKS(20)); // Chờ một chút cho tầng ADC chuyển đổi xong
            
            // Đọc giá trị số nguyên Raw (16-bit) từ chip về
            if (ads111x_get_value(&ads_dev, &raw_soil_val) == ESP_OK) {
                // Công thức quy đổi từ giá trị Raw sang điện áp thực tế (với Gain hệ số 4.096V)
                soil_v[i] = (double)raw_soil_val * 4.096 / 32767.0;
                soil_pct[i] = ads1115_to_soil_percent(soil_v[i]);
                
                ESP_LOGI(TAG, "[ADS1115-CH%d] Đất vị trí %d: %.3fV → %.1f%%",
                         i, i + 1, soil_v[i], soil_pct[i]);
            } else {
                ESP_LOGW(TAG, "[ADS1115] Đọc kênh %d thất bại", i);
            }
        }

        /* ---- 4. Cập nhật dữ liệu dùng chung (Thread-safe) ---- */
        if (xSemaphoreTake(sensor_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
            if (dht_ret == 0) {
                g_sensor_data.temperature  = dht11.temperature;
                g_sensor_data.humidity_air = dht11.humidity;
            }
            if (bh_ret == ESP_OK) {
                g_sensor_data.light_lux = light_lux;
            }
            for (int i = 0; i < SOIL_SENSOR_COUNT; i++) {
                g_sensor_data.humidity_soil[i] = soil_pct[i];
            }

            /* ---- 5. Điều khiển tự động tại biên (Offline mode) ---- */
            relay_auto_control(&g_sensor_data);

            xSemaphoreGive(sensor_mutex);
        }

        vTaskDelay(pdMS_TO_TICKS(SENSOR_READ_PERIOD_MS));
    }
}

/* ===================== TASK: PUBLISH MQTT ===================== */

/**
 * @brief Tạo JSON payload và publish lên MQTT broker
 * Format: {
 *   "device": "ESP32-01",
 *   "temperature": 28.5,
 *   "humidity_air": 65.0,
 *   "soil_1": 42.3, "soil_2": 38.1, "soil_3": 51.0, "soil_4": 44.7,
 *   "light_lux": 850,
 *   "pump": false,
 *   "light_relay": false,
 *   "timestamp": 12345678
 * }
 */
static void publish_sensor_json(void)
{
    char json_buf[512];
    sensor_data_t snapshot;

    if (xSemaphoreTake(sensor_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
        snapshot = g_sensor_data;
        xSemaphoreGive(sensor_mutex);
    } else {
        ESP_LOGW(TAG, "Không lấy được mutex để publish");
        return;
    }

    int len = snprintf(json_buf, sizeof(json_buf),
        "{"
        "\"device\":\"ESP32-01\","
        "\"temperature\":%.1f,"
        "\"humidity_air\":%.1f,"
        "\"soil_1\":%.1f,"
        "\"soil_2\":%.1f,"
        "\"soil_3\":%.1f,"
        "\"soil_4\":%.1f,"
        "\"light_lux\":%d,"
        "\"pump\":%s,"
        "\"light_relay\":%s,"
        "\"timestamp\":%lld"
        "}",
        snapshot.temperature,
        snapshot.humidity_air,
        snapshot.humidity_soil[0],
        snapshot.humidity_soil[1],
        snapshot.humidity_soil[2],
        snapshot.humidity_soil[3],
        snapshot.light_lux,
        snapshot.pump_state        ? "true" : "false",
        snapshot.light_relay_state ? "true" : "false",
        (long long)esp_timer_get_time() / 1000000LL
    );

    if (len > 0 && mqtt_connected) {
        int msg_id = esp_mqtt_client_publish(mqtt_client, TOPIC_SENSOR_DATA,
                                              json_buf, len, 1, 0);
        if (msg_id >= 0) {
            ESP_LOGI(TAG, "Đã gửi MQTT msg_id=%d: %s", msg_id, json_buf);
        } else {
            ESP_LOGW(TAG, "Gửi MQTT thất bại");
        }
    } else if (!mqtt_connected) {
        ESP_LOGW(TAG, "[OFFLINE] Không gửi được MQTT, dữ liệu cục bộ: %s", json_buf);
    }
}

static void mqtt_publish_task(void *pvParameters)
{
    /* Đợi WiFi kết nối trước */
    xEventGroupWaitBits(s_wifi_event_group, WIFI_CONNECTED_BIT | WIFI_FAIL_BIT,
                        pdFALSE, pdFALSE, portMAX_DELAY);
    vTaskDelay(pdMS_TO_TICKS(3000)); // Đợi MQTT handshake

    /* Publish trạng thái online */
    if (mqtt_connected) {
        esp_mqtt_client_publish(mqtt_client, TOPIC_STATUS,
            "{\"status\":\"online\",\"device\":\"ESP32-01\",\"firmware\":\"v1.0\"}",
            0, 1, 1);
    }

    while (1) {
        publish_sensor_json();
        vTaskDelay(pdMS_TO_TICKS(MQTT_PUBLISH_PERIOD_MS));
    }
}

/* ===================== APP MAIN ===================== */

void app_main(void)
{
    ESP_LOGI(TAG, "=== CPS Plant Care System - HCMUTE 2026 ===");
    ESP_LOGI(TAG, "Khởi động firmware v1.0...");

    /* --- NVS (cần cho WiFi) --- */
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    /* --- Tạo mutex --- */
    sensor_mutex = xSemaphoreCreateMutex();
    if (!sensor_mutex) {
        ESP_LOGE(TAG, "Không tạo được mutex! Dừng chương trình.");
        return;
    }

    /* --- Khởi tạo ngoại vi --- */
    i2c_master_init();
    relay_init();

    /* --- Khởi tạo mạng --- */
    wifi_init();
    mqtt_init();

    /* --- Tạo các FreeRTOS Tasks --- */
    xTaskCreate(sensor_task,
                "sensor_task",
                4096,           // Stack 4KB
                NULL,
                5,              // Priority cao hơn để đảm bảo real-time
                NULL);

    xTaskCreate(mqtt_publish_task,
                "mqtt_pub_task",
                4096,
                NULL,
                3,
                NULL);

    ESP_LOGI(TAG, "Tất cả task đã khởi tạo. Hệ thống đang chạy...");
}