idf_component_register(SRCS "main.c"
                    INCLUDE_DIRS "."
                    REQUIRES dht11 bh1750 i2cdev ads111x esp_driver_gpio esp_wifi esp_event nvs_flash esp_timer espressif__mqtt)