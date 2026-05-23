/**
 * @file main.c
 * @brief ESP32 Firmware v2.1 — Rau Cai Mam Brassica juncea Monitor
 *
 * Sensors : DHT11 (Moving Average 5), BH1750, ADS1115 (4ch single-ended), DS3231 RTC
 * Actuator: Pump relay (active HIGH, GPIO 26), Light relay (GPIO 27)
 * Features: Ring buffer 64 packets — tự động gửi lại khi WiFi phục hồi
 * Protocol: MQTT → BBB (topic: cps/greenhouse/sensors)
 * IDF     : v6.0
 */

#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <time.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "freertos/semphr.h"
#include "freertos/queue.h"

#include "esp_system.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "nvs_flash.h"
#include "driver/i2c_master.h"   /* IDF v6 new driver — i2cdev.c tự dùng API này */
#include "driver/gpio.h"
#include "mqtt_client.h"
#include "i2cdev.h"

#include <ads111x.h>
#include "bh1750.h"
#include "esp32-dht11.h"
#include "ds3231.h"

/* ================================================================
   CẤU HÌNH HỆ THỐNG
   ================================================================ */

#define NODE_ID              "BRASSICA_JUNCEA_01"
#define PLANT_NAME           "Rau Cải Mầm (Brassica juncea)"
#define FW_VERSION           "2.1.0"

/* WiFi */
#define WIFI_SSID            "Phòng toàn trai đẹp"
#define WIFI_PASSWORD        "aicungdeptrai<3"
#define WIFI_MAX_RETRY       10

/* MQTT */
#define MQTT_BROKER_URI      "mqtt://192.168.2.15"
#define MQTT_PORT            1883
#define MQTT_KEEPALIVE_S     60
#define MQTT_QOS             1

#define TOPIC_SENSOR         "cps/greenhouse/sensors"
#define TOPIC_STATUS         "cps/greenhouse/status"
#define TOPIC_CMD_PUMP       "cps/greenhouse/cmd/pump"
#define TOPIC_CMD_LIGHT      "cps/greenhouse/cmd/light"

/* GPIO */
#define I2C_MASTER_SDA       21
#define I2C_MASTER_SCL       22
#define I2C_MASTER_PORT      I2C_NUM_0

#define DHT11_GPIO           16
#define RELAY_PUMP_GPIO      26      /* Active HIGH */
#define RELAY_LIGHT_GPIO     27      /* Active HIGH */

/* ADS1115 — 4 kênh single-ended */
#define ADS1115_I2C_ADDR     ADS111X_ADDR_GND   /* 0x48 */
#define SOIL_CH_COUNT        4
static const ads111x_mux_t SOIL_MUX[SOIL_CH_COUNT] = {
    ADS111X_MUX_0_GND,   /* AIN0 */
    ADS111X_MUX_1_GND,   /* AIN1 */
    ADS111X_MUX_2_GND,   /* AIN2 */
    ADS111X_MUX_3_GND,   /* AIN3 */
};

/* BH1750 */
#define BH1750_I2C_ADDR      BH1750_ADDR_LO     /* 0x23 */
/* 25002500 Th00f4ng s1ed1 sinh h1ecdc Brassica juncea microgreens 25002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500
 * Phase 1 (N1ea3y m1ea7m, ng00e0y 1-3): T1ed1i ho00e0n to00e0n, T=20-2400b0C, RH=70-85%%, soil=60-80%%
 * Phase 2 (Sinh tr01b01edfng, ng00e0y 4-7): 011000e8n LED 12-16h, T=18-2400b0C, RH=50-65%%, soil=55-75%%
 * Ngu1ed3n: Johnny's Seeds microgreens guide + NCBI PMC8073284
 * 250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500250025002500 */
#define TEMP_IDEAL_MIN       18.0f
#define TEMP_IDEAL_MAX       24.0f
#define TEMP_GERM_IDEAL      22.0f
#define HUM_PHASE1_MIN       70.0f
#define HUM_PHASE1_MAX       85.0f
#define HUM_PHASE2_MIN       50.0f
#define HUM_PHASE2_MAX       65.0f
#define SOIL_IDEAL_MIN       55.0f
#define SOIL_IDEAL_MAX       80.0f
#define LIGHT_LEAK_THRESHOLD  5.0f   /* lux -- Phase 1 hop kin, >5 lux = lot sang */
#define LIGHT_PHASE2_MIN     150.0f  /* lux -- den LED toi thieu Phase 2 */
#define LIGHT_PHASE2_IDEAL   220.0f  /* lux -- LED do/xanh toi uu */

/* Chu kỳ */
#define SENSOR_READ_MS       5000
#define MQTT_PUBLISH_MS      5000

/* Calibration ADS1115 (đo thực tế khi khô/ướt) */
#define SOIL_V_DRY           3.0f
#define SOIL_V_WET           1.1f

/* ================================================================
   RING BUFFER — tránh mất gói khi mất mạng
   ================================================================ */

#define RING_BUF_SIZE        64     /* số packet tối đa lưu khi offline */
#define JSON_MAX_LEN         400

typedef struct {
    char  json[JSON_MAX_LEN];
    bool  valid;
} ring_entry_t;

typedef struct {
    ring_entry_t buf[RING_BUF_SIZE];
    int          head;    /* vị trí ghi tiếp theo */
    int          tail;    /* vị trí đọc tiếp theo */
    int          count;   /* số packet đang chờ   */
    SemaphoreHandle_t mutex;
} ring_buf_t;

static ring_buf_t s_ring = {0};

static void ring_init(void) {
    s_ring.head  = 0;
    s_ring.tail  = 0;
    s_ring.count = 0;
    s_ring.mutex = xSemaphoreCreateMutex();
}

/* Trả về true nếu push thành công, false nếu buffer đầy (ghi đè packet cũ nhất) */
static bool ring_push(const char *json) {
    if (!xSemaphoreTake(s_ring.mutex, pdMS_TO_TICKS(50))) return false;

    /* Nếu đầy → ghi đè packet cũ nhất (overwrite oldest) */
    if (s_ring.count == RING_BUF_SIZE) {
        s_ring.tail = (s_ring.tail + 1) % RING_BUF_SIZE;
        s_ring.count--;
        ESP_LOGW("RING", "Buffer đầy — ghi đè packet cũ nhất");
    }

    strlcpy(s_ring.buf[s_ring.head].json, json, JSON_MAX_LEN);
    s_ring.buf[s_ring.head].valid = true;
    s_ring.head  = (s_ring.head + 1) % RING_BUF_SIZE;
    s_ring.count++;

    xSemaphoreGive(s_ring.mutex);
    return true;
}

/* Lấy 1 packet ra (không xóa, gọi ring_pop sau khi gửi thành công) */
static bool ring_peek(char *out_json) {
    if (!xSemaphoreTake(s_ring.mutex, pdMS_TO_TICKS(50))) return false;
    bool has = (s_ring.count > 0);
    if (has) strlcpy(out_json, s_ring.buf[s_ring.tail].json, JSON_MAX_LEN);
    xSemaphoreGive(s_ring.mutex);
    return has;
}

static void ring_pop(void) {
    if (!xSemaphoreTake(s_ring.mutex, pdMS_TO_TICKS(50))) return;
    if (s_ring.count > 0) {
        s_ring.buf[s_ring.tail].valid = false;
        s_ring.tail  = (s_ring.tail + 1) % RING_BUF_SIZE;
        s_ring.count--;
    }
    xSemaphoreGive(s_ring.mutex);
}

static int ring_count(void) {
    if (!xSemaphoreTake(s_ring.mutex, pdMS_TO_TICKS(50))) return 0;
    int c = s_ring.count;
    xSemaphoreGive(s_ring.mutex);
    return c;
}

/* ================================================================
   BỘ LỌC MOVING AVERAGE DHT11
   ================================================================ */

#define DHT_FILTER_SIZE 5

typedef struct {
    float temp_buf[DHT_FILTER_SIZE];
    float hum_buf[DHT_FILTER_SIZE];
    uint8_t index;
    uint8_t count;
} dht_filter_t;

static dht_filter_t s_dht_ma = {0};

static void dht11_moving_avg(float t, float h, float *out_t, float *out_h) {
    s_dht_ma.temp_buf[s_dht_ma.index] = t;
    s_dht_ma.hum_buf[s_dht_ma.index]  = h;
    s_dht_ma.index = (s_dht_ma.index + 1) % DHT_FILTER_SIZE;
    if (s_dht_ma.count < DHT_FILTER_SIZE) s_dht_ma.count++;

    float st = 0, sh = 0;
    for (int i = 0; i < s_dht_ma.count; i++) {
        st += s_dht_ma.temp_buf[i];
        sh += s_dht_ma.hum_buf[i];
    }
    *out_t = st / s_dht_ma.count;
    *out_h = sh / s_dht_ma.count;
}

/* ================================================================
   BIẾN TOÀN CỤC
   ================================================================ */

static const char *TAG = "SPROUT";

static EventGroupHandle_t s_wifi_eg;
#define WIFI_CONNECTED_BIT  BIT0
#define WIFI_FAIL_BIT       BIT1

static esp_mqtt_client_handle_t s_mqtt = NULL;
static bool s_mqtt_connected = false;
static int  s_wifi_retry     = 0;
static int  s_step           = 0;

static SemaphoreHandle_t s_sensor_mutex;
static i2c_dev_t         s_ads_dev = {0};
static i2c_dev_t         s_rtc_dev = {0};

typedef struct {
    float temperature;
    float air_humidity;
    float soil_pct[SOIL_CH_COUNT];
    float lux;
    bool  pump_state;
    bool  light_state;
    bool  dht11_ok;
    bool  bh1750_ok;
    bool  ads1115_ok;
    bool  rtc_ok;
    char  iso_time[32];     /* ISO8601 từ DS3231 */
    int   wifi_rssi;
    long  uptime_s;
} sensor_data_t;

static sensor_data_t g_sensor = {0};

/* ================================================================
   HELPER — RTC timestamp
   ================================================================ */

/* Auto-set DS3231 từ compile time nếu RTC chưa được set (năm <= 2000) */
static void rtc_sync_compile_time(void) {
    struct tm t = {0};
    if (ds3231_get_time(&s_rtc_dev, &t) != ESP_OK) return;

    /* Nếu năm <= 2000 → DS3231 chưa được set, tự set từ __DATE__ __TIME__ */
    if (t.tm_year <= 100) {   /* tm_year = years since 1900, 100 = năm 2000 */
        struct tm ct = {0};
        /* Parse compile-time strings: __DATE__ = "May 23 2026", __TIME__ = "09:45:00" */
        strptime(__DATE__ " " __TIME__, "%b %d %Y %H:%M:%S", &ct);
        if (ds3231_set_time(&s_rtc_dev, &ct) == ESP_OK) {
            ESP_LOGW("RTC", "DS3231 chưa set — tự đồng bộ từ compile time: %s %s", __DATE__, __TIME__);
        } else {
            ESP_LOGE("RTC", "Set RTC thất bại");
        }
    }
}

static void rtc_get_iso(char *buf, size_t len) {
    struct tm t = {0};
    if (ds3231_get_time(&s_rtc_dev, &t) == ESP_OK && t.tm_year > 100) {
        strftime(buf, len, "%Y-%m-%dT%H:%M:%S+07:00", &t);
    } else {
        /* fallback: uptime nếu RTC vẫn lỗi */
        long up = (long)(esp_timer_get_time() / 1000000);
        snprintf(buf, len, "uptime:%lds", up);
    }
}

/* ================================================================
   HELPER — soil ADC → %
   ================================================================ */

static float ads_voltage_to_pct(double v) {
    if (v >= SOIL_V_DRY) return 0.0f;
    if (v <= SOIL_V_WET) return 100.0f;
    return (SOIL_V_DRY - (float)v) / (SOIL_V_DRY - SOIL_V_WET) * 100.0f;
}

/* ================================================================
   HELPER — build JSON payload
   ================================================================ */

static int build_json(char *buf, size_t buf_len, const sensor_data_t *s, int step) {
    float soil_avg = (s->soil_pct[0] + s->soil_pct[1] +
                      s->soil_pct[2] + s->soil_pct[3]) / 4.0f;
    return snprintf(buf, buf_len,
        "{"
        "\"node_id\":\"%s\","
        "\"fw\":\"%s\","
        "\"timestamp\":\"%s\","
        "\"step\":%d,"
        "\"uptime_s\":%ld,"
        "\"sensor\":{"
            "\"temperature\":%.1f,"
            "\"air_humidity\":%.1f,"
            "\"lux\":%.2f,"
            "\"soil_moisture_avg\":%.1f,"
            "\"soil_moisture_raw\":{"
                "\"s1\":%.1f,\"s2\":%.1f,\"s3\":%.1f,\"s4\":%.1f"
            "}"
        "},"
        "\"status\":{"
            "\"wifi_rssi\":%d,"
            "\"dht11_ok\":%s,"
            "\"bh1750_ok\":%s,"
            "\"ads1115_ok\":%s,"
            "\"rtc_ok\":%s,"
            "\"pump_on\":%s,"
            "\"light_on\":%s"
        "}"
        "}",
        NODE_ID, FW_VERSION, s->iso_time, step,
        s->uptime_s,
        s->temperature, s->air_humidity, s->lux, soil_avg,
        s->soil_pct[0], s->soil_pct[1], s->soil_pct[2], s->soil_pct[3],
        s->wifi_rssi,
        s->dht11_ok   ? "true" : "false",
        s->bh1750_ok  ? "true" : "false",
        s->ads1115_ok ? "true" : "false",
        s->rtc_ok     ? "true" : "false",
        s->pump_state ? "true" : "false",
        s->light_state ? "true" : "false"
    );
}

/* ================================================================
   WIFI
   ================================================================ */

static void wifi_event_handler(void *arg, esp_event_base_t base,
                                int32_t id, void *data) {
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        s_mqtt_connected = false;
        if (s_wifi_retry < WIFI_MAX_RETRY) {
            esp_wifi_connect();
            s_wifi_retry++;
            ESP_LOGW(TAG, "WiFi retry %d/%d", s_wifi_retry, WIFI_MAX_RETRY);
        } else {
            xEventGroupSetBits(s_wifi_eg, WIFI_FAIL_BIT);
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        s_wifi_retry = 0;
        xEventGroupSetBits(s_wifi_eg, WIFI_CONNECTED_BIT);
        ESP_LOGI(TAG, "WiFi kết nối thành công");
    }
}

static void wifi_init(void) {
    s_wifi_eg = xEventGroupCreate();
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));

    esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID,
                                        wifi_event_handler, NULL, NULL);
    esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP,
                                        wifi_event_handler, NULL, NULL);

    wifi_config_t wcfg = {
        .sta = {
            .ssid     = WIFI_SSID,
            .password = WIFI_PASSWORD,
            .threshold.authmode = WIFI_AUTH_WPA2_PSK,
        },
    };
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wcfg));
    ESP_ERROR_CHECK(esp_wifi_start());

    EventBits_t bits = xEventGroupWaitBits(s_wifi_eg,
        WIFI_CONNECTED_BIT | WIFI_FAIL_BIT, pdFALSE, pdFALSE,
        pdMS_TO_TICKS(15000));

    if (bits & WIFI_CONNECTED_BIT) {
        ESP_LOGI(TAG, "WiFi OK");
    } else {
        ESP_LOGE(TAG, "WiFi thất bại — chạy offline, ring buffer active");
    }
}

/* ================================================================
   MQTT
   ================================================================ */

static void mqtt_event_handler(void *arg, esp_event_base_t base,
                                int32_t id, void *data) {
    esp_mqtt_event_handle_t ev = (esp_mqtt_event_handle_t)data;

    switch ((esp_mqtt_event_id_t)id) {

        case MQTT_EVENT_CONNECTED:
            s_mqtt_connected = true;
            esp_mqtt_client_subscribe(s_mqtt, TOPIC_CMD_PUMP,  MQTT_QOS);
            esp_mqtt_client_subscribe(s_mqtt, TOPIC_CMD_LIGHT, MQTT_QOS);
            ESP_LOGI(TAG, "MQTT kết nối — subscribed cmd/pump + cmd/light");
            break;

        case MQTT_EVENT_DISCONNECTED:
            s_mqtt_connected = false;
            ESP_LOGW(TAG, "MQTT mất kết nối — buffering ON");
            break;

        case MQTT_EVENT_DATA:
            if (xSemaphoreTake(s_sensor_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {

                /* Lệnh bơm */
                if (strncmp(ev->topic, TOPIC_CMD_PUMP, ev->topic_len) == 0) {
                    bool on = (strncmp(ev->data, "ON", ev->data_len) == 0);
                    gpio_set_level(RELAY_PUMP_GPIO, on ? 1 : 0);
                    g_sensor.pump_state = on;
                    ESP_LOGI(TAG, "Pump → %s", on ? "ON" : "OFF");
                }

                /* Lệnh đèn */
                if (strncmp(ev->topic, TOPIC_CMD_LIGHT, ev->topic_len) == 0) {
                    bool on = (strncmp(ev->data, "ON", ev->data_len) == 0);
                    gpio_set_level(RELAY_LIGHT_GPIO, on ? 1 : 0);
                    g_sensor.light_state = on;
                    ESP_LOGI(TAG, "Light → %s", on ? "ON" : "OFF");
                }

                xSemaphoreGive(s_sensor_mutex);
            }
            break;

        case MQTT_EVENT_PUBLISHED:
            ESP_LOGD(TAG, "MQTT publish ACK msg_id=%d", ev->msg_id);
            break;

        default:
            break;
    }
}

static void mqtt_init(void) {
    esp_mqtt_client_config_t cfg = {
        .broker.address.uri  = MQTT_BROKER_URI,
        .broker.address.port = MQTT_PORT,
        .session.keepalive   = MQTT_KEEPALIVE_S,
    };
    s_mqtt = esp_mqtt_client_init(&cfg);
    esp_mqtt_client_register_event(s_mqtt, ESP_EVENT_ANY_ID,
                                   mqtt_event_handler, NULL);
    esp_mqtt_client_start(s_mqtt);
}

/* ================================================================
   INIT PHẦN CỨNG
   ================================================================ */

static void hw_init(void) {
    /* I2C */
    ESP_ERROR_CHECK(i2cdev_init());

    /* DS3231 RTC */
    memset(&s_rtc_dev, 0, sizeof(s_rtc_dev));
    if (ds3231_init_desc(&s_rtc_dev, I2C_MASTER_PORT,
                         I2C_MASTER_SDA, I2C_MASTER_SCL) != ESP_OK) {
        ESP_LOGE(TAG, "DS3231 init thất bại");
    } else {
        /* Tự set time nếu RTC chưa được set (hiện thị năm 2000) */
        rtc_sync_compile_time();
    }

    /* Relay pump — active HIGH, mặc định OFF */
    gpio_config_t relay_cfg = {
        .pin_bit_mask = (1ULL << RELAY_PUMP_GPIO) | (1ULL << RELAY_LIGHT_GPIO),
        .mode         = GPIO_MODE_OUTPUT,
        .pull_down_en = GPIO_PULLDOWN_ENABLE,
        .pull_up_en   = GPIO_PULLUP_DISABLE,
        .intr_type    = GPIO_INTR_DISABLE,
    };
    gpio_config(&relay_cfg);
    gpio_set_level(RELAY_PUMP_GPIO,  0);
    gpio_set_level(RELAY_LIGHT_GPIO, 0);
}

/* ================================================================
   TASK: ĐỌC CẢM BIẾN
   ================================================================ */

static void sensor_task(void *pv) {
    /* DHT11 */
    dht11_t dht = { .dht11_pin = DHT11_GPIO };

    /* BH1750 */
    i2c_dev_t bh_dev = {0};
    bool bh_ready = false;
    if (bh1750_init_desc(&bh_dev, BH1750_I2C_ADDR,
                         I2C_MASTER_PORT, I2C_MASTER_SDA, I2C_MASTER_SCL) == ESP_OK
        && bh1750_power_on(&bh_dev) == ESP_OK
        && bh1750_setup(&bh_dev, BH1750_MODE_CONTINUOUS,
                        BH1750_RES_HIGH2) == ESP_OK) {
        bh_ready = true;
        ESP_LOGI(TAG, "BH1750 OK");
    } else {
        ESP_LOGE(TAG, "BH1750 init thất bại");
    }

    /* ADS1115 */
    bool ads_ready = false;
    if (ads111x_init_desc(&s_ads_dev, ADS1115_I2C_ADDR,
                          I2C_MASTER_PORT, I2C_MASTER_SDA, I2C_MASTER_SCL) == ESP_OK
        && ads111x_set_mode(&s_ads_dev, ADS111X_MODE_SINGLE_SHOT) == ESP_OK
        && ads111x_set_data_rate(&s_ads_dev, ADS111X_DATA_RATE_8) == ESP_OK
        && ads111x_set_gain(&s_ads_dev, ADS111X_GAIN_4V096) == ESP_OK) {
        ads_ready = true;
        ESP_LOGI(TAG, "ADS1115 OK");
    } else {
        ESP_LOGE(TAG, "ADS1115 init thất bại");
    }

    vTaskDelay(pdMS_TO_TICKS(2000));

    while (1) {
        sensor_data_t snap = {0};
        snap.uptime_s = (long)(esp_timer_get_time() / 1000000LL);

        /* --- RTC timestamp --- */
        rtc_get_iso(snap.iso_time, sizeof(snap.iso_time));
        snap.rtc_ok = (snap.iso_time[0] == '2');  /* bắt đầu bằng '2' = năm 20xx */

        /* --- DHT11 + moving average --- */
        if (dht11_read(&dht, 3) == 0) {
            float ft, fh;
            dht11_moving_avg(dht.temperature, dht.humidity, &ft, &fh);
            snap.temperature  = ft;
            snap.air_humidity = fh;
            snap.dht11_ok     = true;
        } else {
            /* giữ giá trị cũ */
            if (xSemaphoreTake(s_sensor_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
                snap.temperature  = g_sensor.temperature;
                snap.air_humidity = g_sensor.air_humidity;
                xSemaphoreGive(s_sensor_mutex);
            }
            snap.dht11_ok = false;
            ESP_LOGW(TAG, "DHT11 đọc lỗi");
        }

        /* --- BH1750 --- */
        if (bh_ready) {
            uint16_t raw = 0;
            if (bh1750_read(&bh_dev, &raw) == ESP_OK) {
                snap.lux      = (float)raw / 2.0f;
                snap.bh1750_ok = true;

                if (snap.lux > LIGHT_LEAK_THRESHOLD) {
                    ESP_LOGW(TAG, "[!!!] LỌT SÁNG: %.2f lux", snap.lux);
                }
            } else {
                snap.bh1750_ok = false;
            }
        }

        /* --- ADS1115 — 4 kênh single-ended --- */
        if (ads_ready) {
            bool all_ok = true;
            for (int i = 0; i < SOIL_CH_COUNT; i++) {
                /* Single-shot mode: set mux → start conversion → wait → read */
                if (ads111x_set_input_mux(&s_ads_dev, SOIL_MUX[i]) != ESP_OK) {
                    all_ok = false; continue;
                }
                /* Trigger single conversion */
                if (ads111x_start_conversion(&s_ads_dev) != ESP_OK) {
                    all_ok = false; continue;
                }
                /* Chờ conversion xong: 8 SPS = 125ms/mẫu → delay 150ms an toàn */
                vTaskDelay(pdMS_TO_TICKS(150));

                bool busy = true;
                /* Poll DRDY/OS bit tối đa 200ms */
                for (int w = 0; w < 8 && busy; w++) {
                    ads111x_is_busy(&s_ads_dev, &busy);
                    if (busy) vTaskDelay(pdMS_TO_TICKS(25));
                }

                int16_t raw_v = 0;
                if (ads111x_get_value(&s_ads_dev, &raw_v) == ESP_OK) {
                    double voltage = (double)raw_v * 4.096 / 32767.0;
                    snap.soil_pct[i] = ads_voltage_to_pct(voltage);
                } else {
                    all_ok = false;
                }
            }
            snap.ads1115_ok = all_ok;
        }

        /* --- WiFi RSSI --- */
        wifi_ap_record_t ap;
        if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) {
            snap.wifi_rssi = ap.rssi;
        }

        /* --- Giữ relay state từ g_sensor --- */
        if (xSemaphoreTake(s_sensor_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
            snap.pump_state  = g_sensor.pump_state;
            snap.light_state = g_sensor.light_state;
            g_sensor = snap;
            xSemaphoreGive(s_sensor_mutex);
        }

        /* --- Log serial với cảnh báo thông minh Brassica juncea --- */
        bool phase2 = (snap.lux >= 50.0f);
        float soil_avg = (snap.soil_pct[0]+snap.soil_pct[1]+
                          snap.soil_pct[2]+snap.soil_pct[3]) / 4.0f;

        ESP_LOGI(TAG, "══════════════════════════════════════");
        ESP_LOGI(TAG, "  %s | Phase %d | %s",
                 NODE_ID, phase2 ? 2 : 1, snap.iso_time);
        ESP_LOGI(TAG, "══════════════════════════════════════");

        /* Nhiệt độ */
        if (!snap.dht11_ok) {
            ESP_LOGE(TAG, "  Temp    : [ERR - DHT11 mất kết nối]");
        } else if (snap.temperature > TEMP_IDEAL_MAX) {
            ESP_LOGW(TAG, "  Temp    : %.1f°C [!] NÓNG QUÁ (tối ưu %.0f-%.0f°C)",
                     snap.temperature, TEMP_IDEAL_MIN, TEMP_IDEAL_MAX);
        } else if (snap.temperature < TEMP_IDEAL_MIN) {
            ESP_LOGW(TAG, "  Temp    : %.1f°C [!] LẠNH QUÁ (tối ưu %.0f-%.0f°C)",
                     snap.temperature, TEMP_IDEAL_MIN, TEMP_IDEAL_MAX);
        } else {
            ESP_LOGI(TAG, "  Temp    : %.1f°C [OK]", snap.temperature);
        }

        /* Độ ẩm không khí */
        float hum_min = phase2 ? HUM_PHASE2_MIN : HUM_PHASE1_MIN;
        float hum_max = phase2 ? HUM_PHASE2_MAX : HUM_PHASE1_MAX;
        if (!snap.dht11_ok) {
            ESP_LOGE(TAG, "  Hum KK  : [ERR]");
        } else if (snap.air_humidity < hum_min) {
            ESP_LOGW(TAG, "  Hum KK  : %.1f%% [!] KHÔ QUÁ (cần %.0f-%.0f%%)",
                     snap.air_humidity, hum_min, hum_max);
        } else if (snap.air_humidity > hum_max) {
            ESP_LOGW(TAG, "  Hum KK  : %.1f%% [!] ẨM QUÁ — nguy cơ nấm mốc (cần %.0f-%.0f%%)",
                     snap.air_humidity, hum_min, hum_max);
        } else {
            ESP_LOGI(TAG, "  Hum KK  : %.1f%% [OK]", snap.air_humidity);
        }

        /* Ánh sáng */
        if (!snap.bh1750_ok) {
            ESP_LOGE(TAG, "  Lux     : [ERR - BH1750 mất kết nối]");
        } else if (!phase2 && snap.lux > LIGHT_LEAK_THRESHOLD) {
            ESP_LOGW(TAG, "  Lux     : %.1f [!!!] LỌT SÁNG - Phase 1 cần tối hoàn toàn!", snap.lux);
        } else if (phase2 && snap.lux < LIGHT_PHASE2_MIN) {
            ESP_LOGW(TAG, "  Lux     : %.1f [!] ĐÈN YẾU QUÁ (cần >= %.0f lux)", snap.lux, LIGHT_PHASE2_MIN);
        } else {
            ESP_LOGI(TAG, "  Lux     : %.1f [%s]", snap.lux, phase2 ? "Phase2-OK" : "Tối OK");
        }

        /* Độ ẩm đất */
        ESP_LOGI(TAG, "  Soil    : S1=%.0f%% S2=%.0f%% S3=%.0f%% S4=%.0f%% avg=%.0f%%",
                 snap.soil_pct[0], snap.soil_pct[1],
                 snap.soil_pct[2], snap.soil_pct[3], soil_avg);
        if (soil_avg < SOIL_IDEAL_MIN) {
            ESP_LOGW(TAG, "  Soil    : [!] QUÁ KHÔ (%.0f%% < %.0f%%) — cần tưới!", soil_avg, SOIL_IDEAL_MIN);
        } else if (soil_avg > SOIL_IDEAL_MAX) {
            ESP_LOGW(TAG, "  Soil    : [!] QUÁ ẨM (%.0f%% > %.0f%%) — nguy cơ úng rễ!", soil_avg, SOIL_IDEAL_MAX);
        }

        ESP_LOGI(TAG, "  Pump    : %s  Light: %s  RingBuf: %d pending",
                 snap.pump_state  ? "ON" : "OFF",
                 snap.light_state ? "ON" : "OFF",
                 ring_count());

        vTaskDelay(pdMS_TO_TICKS(SENSOR_READ_MS));
    }
}

/* ================================================================
   TASK: PUBLISH MQTT + DRAIN RING BUFFER
   ================================================================ */

static void publish_task(void *pv) {
    static char json_buf[JSON_MAX_LEN];
    static char drain_buf[JSON_MAX_LEN];

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(MQTT_PUBLISH_MS));

        /* --- Build JSON từ snapshot hiện tại --- */
        sensor_data_t snap;
        if (xSemaphoreTake(s_sensor_mutex, pdMS_TO_TICKS(200)) == pdTRUE) {
            snap = g_sensor;
            xSemaphoreGive(s_sensor_mutex);
        } else {
            continue;
        }

        s_step++;
        int len = build_json(json_buf, sizeof(json_buf), &snap, s_step);
        if (len <= 0) continue;

        if (s_mqtt_connected) {
            /* ── ONLINE: gửi packet hiện tại ── */
            int ret = esp_mqtt_client_publish(s_mqtt, TOPIC_SENSOR,
                                              json_buf, len, MQTT_QOS, 0);
            if (ret >= 0) {
                ESP_LOGI(TAG, "[MQTT] Publish step=%d OK", s_step);
            } else {
                /* Publish thất bại dù connected → buffer */
                ring_push(json_buf);
                ESP_LOGW(TAG, "[MQTT] Publish thất bại → buffer (%d pending)",
                         ring_count());
            }

            /* ── DRAIN ring buffer — gửi lại các packet cũ ── */
            int drained = 0;
            while (s_mqtt_connected && ring_count() > 0 && drained < 5) {
                if (!ring_peek(drain_buf)) break;

                int dret = esp_mqtt_client_publish(s_mqtt, TOPIC_SENSOR,
                                                   drain_buf, strlen(drain_buf),
                                                   MQTT_QOS, 0);
                if (dret >= 0) {
                    ring_pop();
                    drained++;
                    ESP_LOGI(TAG, "[RING] Gửi lại thành công (%d còn lại)",
                             ring_count());
                } else {
                    /* Vẫn lỗi → dừng drain, thử lại sau */
                    break;
                }
                vTaskDelay(pdMS_TO_TICKS(200));   /* throttle drain */
            }

        } else {
            /* ── OFFLINE: đẩy vào ring buffer ── */
            ring_push(json_buf);
            ESP_LOGW(TAG, "[OFFLINE] Buffer step=%d (%d pending)",
                     s_step, ring_count());
        }
    }
}

/* ================================================================
   APP MAIN
   ================================================================ */

void app_main(void) {
    ESP_LOGI(TAG, "=== %s | %s v%s ===", PLANT_NAME, NODE_ID, FW_VERSION);

    /* NVS */
    esp_err_t nvs_ret = nvs_flash_init();
    if (nvs_ret == ESP_ERR_NVS_NO_FREE_PAGES ||
        nvs_ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        nvs_flash_init();
    }

    /* Khởi tạo */
    s_sensor_mutex = xSemaphoreCreateMutex();
    ring_init();
    hw_init();
    wifi_init();
    mqtt_init();

    /* Tasks */
    xTaskCreate(sensor_task,  "sensor",  4096, NULL, 5, NULL);
    xTaskCreate(publish_task, "publish", 4096, NULL, 3, NULL);

    ESP_LOGI(TAG, "Tất cả tasks đã khởi động");
}
