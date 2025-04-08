#ifndef ARDUINO_USB_MODE
#error This ESP32 SoC has no Native USB interface
#elif ARDUINO_USB_MODE == 1
#warning This sketch should be used when USB is in OTG mode
void setup() {}
void loop() {}
#else

#include <Arduino.h>
#include <ArduinoJson.h>
#include <USB.h>
#include <USBHID.h>
#include <USBHIDMouse.h>
#include <USBHIDKeyboard.h>
#include <USBHIDSystemControl.h>

USBHID HID;
USBHIDAbsoluteMouse Mouse;
USBHIDKeyboard Keyboard;
USBCDC USBSerial;

HardwareSerial &host_serial = Serial;

JsonDocument mkb_input;
JsonDocument power_status;
JsonDocument heartbeat;

const int8_t pwr_button = 46;
const int8_t cmos_button = 3;

void setup()
{
    host_serial.begin(115200);
    HID.begin();
    Mouse.begin();
    Keyboard.begin();
    USB.begin();
    USBSerial.begin(115200);
    pinMode(pwr_button, OUTPUT);
    pinMode(cmos_button, OUTPUT);
    host_serial.write("HELLO\n");
}

void loop()
{
    static volatile int64_t cur_loop_timestamp = esp_timer_get_time();
    // Immediately check power status!
    static volatile int64_t check_power_status_timestamp = -200000;
    static volatile bool monitor_heartbeat = false;
    static volatile int64_t last_heartbeat_timestamp;
    static volatile bool waiting_reboot = false;
    static volatile int64_t begin_wait_reboot_timestamp;

    cur_loop_timestamp = esp_timer_get_time();
    
    if (cur_loop_timestamp - check_power_status_timestamp >= 100000)
    {
        if (HID.ready())
        {
            power_status["usb"] = true;
        }

        else
        {
            power_status["usb"] = false;
        }

        serializeJson(power_status, host_serial);
        host_serial.write('\n');
        check_power_status_timestamp = esp_timer_get_time();
    }

    if (host_serial.available())
    {
        DeserializationError error = deserializeJson(mkb_input, host_serial);

        if (error)
        {
            host_serial.print("deserializeJson() failed: ");
            host_serial.println(error.c_str());
            return;
        }

        else
        {
            if (mkb_input["key_down"].is<JsonVariant>())
            {
                Keyboard.pressRaw(mkb_input["key_down"].as<uint8_t>());
            }

            else if (mkb_input["key_up"].is<JsonVariant>())
            {
                Keyboard.releaseRaw(mkb_input["key_up"].as<uint8_t>());
            }

            else if (mkb_input["mouse_coord"].is<JsonVariant>())
            {
                Mouse.move(mkb_input["mouse_coord"]["x"].as<int16_t>(), mkb_input["mouse_coord"]["y"].as<int16_t>());
            }

            else if (mkb_input["mouse_down"].is<JsonVariant>())
            {
                Mouse.press(mkb_input["mouse_down"].as<uint8_t>());
            }

            else if (mkb_input["mouse_up"].is<JsonVariant>())
            {
                Mouse.release(mkb_input["mouse_up"].as<uint8_t>());
            }

            else if (mkb_input["pwr"].is<JsonVariant>())
            {
                digitalWrite(pwr_button, mkb_input["pwr"].as<uint8_t>());
            }

            else if (mkb_input["cmos"].is<JsonVariant>())
            {
                digitalWrite(cmos_button, mkb_input["cmos"].as<uint8_t>());
            }
        }
    }

    if (USBSerial.available())
    {
        DeserializationError error = deserializeJson(heartbeat, USBSerial);

        if (error)
        {
            host_serial.print("deserializeJson() failed: ");
            host_serial.println(error.c_str());
            return;
        }

        else
        {
            monitor_heartbeat = heartbeat["status"].as<bool>();
            last_heartbeat_timestamp = cur_loop_timestamp;
            waiting_reboot = false;
        }
    }
    
    // LARGE timeout because sometimes it recovers from a crash automatically & I don't want to interrupt that
    // and make it worse...
    if (monitor_heartbeat && cur_loop_timestamp - last_heartbeat_timestamp >= 300000000)
    {
        digitalWrite(pwr_button, 1);
        delay(5000);
        digitalWrite(pwr_button, 0);
        delay(5000);
        digitalWrite(pwr_button, 1);
        delay(200);
        digitalWrite(pwr_button, 0);
        monitor_heartbeat = false;
        waiting_reboot = true;
        begin_wait_reboot_timestamp = cur_loop_timestamp;
    }

    if (waiting_reboot && cur_loop_timestamp - begin_wait_reboot_timestamp >= 300000000)
    {
        Keyboard.pressRaw(HID_KEY_ARROW_LEFT);
        delay(50);
        Keyboard.releaseRaw(HID_KEY_ARROW_LEFT);
        delay(50);
        Keyboard.pressRaw(HID_KEY_ENTER);
        delay(50);
        Keyboard.releaseRaw(HID_KEY_ENTER);
        waiting_reboot = false;
    }
}

#endif /* ARDUINO_USB_MODE */