# HP ZBook Studio G8 keyboard lighting protocol

Reverse-engineered from OMEN Gaming Hub 1101.2609.11.0 and verified on the
hardware (BIOS T92 01.25.00, KBC 53.40.00). ✅ = seen working on the keyboard.

## How OMEN drives it

- `HP.Omen.Core.Model.Device.dll` maps board SSID **8873** to codename
  **Modena**, which has the feature `DraxLighting`.
- For the keyboard, DraxLightingBg's **`StarmadeKbLightingControl`**
  (`NbPerKeyRgbLightingControl`, identity `ModenaPerKeyRGB`, interface
  `mi_02`) calls **`McuSDK2.McuGeneralHelper`**. The HID I/O goes through
  OMEN's *HidMailroom* pipe.
- DraxLightingBg also has an older `DraxKBLightingControl` / McuSDK v1 path,
  which this keyboard doesn't use. Its packets are acknowledged but have no
  effect. Its "on" (`09 … FF`) is actually McuSDK2 `STOP`.

## Transport

| | |
|---|---|
| USB device | `1FC9:2238` (NXP MCU, internal). Only drives LEDs; typing goes through the EC (`ACPI\HPQ8002`, PS/2) |
| Interface | **MI_02**, vendor usage page `0x0081`/`0x82`, 64-byte in/out, no report IDs |
| Write | 65 bytes: `00` (report id) + 64-byte packet. No admin needed on Windows |
| Reply | 64 bytes: `cmd idx … … EC status`. Status `AC` = ok, `FA` = fail, `BD` = auto report |

## Commands (McuSDK2 "General")

| packet | meaning | |
|---|---|---|
| `09 00 01 00 01` | lighting on (`START`) | ✅ |
| `09 00 01 00 00` | lighting off | ✅ |
| `05/06/07 <page> 00 00 <60 bytes>` | R/G/B plane of the 144-key map, pages 0–2 (60/60/24 bytes). The length field is 0 | ✅ |
| `03 00 24 00 <36 bytes>` | lighting effect (below) | ✅ |
| `80 01 00 00` | device info (reply bytes 4–7 fw, 8 device id, 9 language, 11 effect, 12 brightness) | ✅ |
| `0C 00 01 00 <lvl>` | whole-keyboard brightness. **Rejected (`FA`) by this firmware** | ❌ |
| `0A 00 02 00 AC 53` | store lighting to flash. OMEN sends it after each change; we don't, to avoid flash wear | not used |

Static colour (OMEN's `ResetLighting`): `on`, then the 9 plane packets.

### Effect packet

```
[4] effect     [5] showMode   [6] colorNumber  [7] speed (0 slow..2 fast)
[8] brightness level (ignored by the animations)   [9] direction
[10] ripple size   [11] raindrop frequency (= speed)
[28..45] up to 6 colours, RGB
```
- Preset palette: `colorNumber = 4`, `showMode` = preset (2 volcano, 3 jungle, 4 ocean, 5 galaxy).
- Custom colours: `colorNumber = n-1`, `showMode = 0` for one colour or `1` for several.
- Direction: OMEN swaps the first two values (UI 0↔1) before sending.

| effect | id | OMEN default | |
|---|---|---|---|
| colorloop | 4 | volcano, medium | ✅ |
| sparkle | 7 | ocean | ✅ |
| breathing | 2 | custom `#EA002A`, slow. **A preset does nothing** | ✅ |
| ghosting | 8 | jungle. Lights keys as you type | ✅ |
| ripple | 9 | ocean. Reacts to typing | ✅ |
| wave | 10 | galaxy | ✅ |
| omenx | 13 | volcano | ✅ |
| raindrop | 12 | custom `#0FFA36`, fast | ✅ |
| confetti / sun / swipe | 15 / 16 / 17 | in OMEN's table, no Modena default | confetti ✅ |

## Hotkeys

The keyboard's Fn layer is in the EC, not the RGB MCU:

- **Fn+1…9** send the *same* scancodes as 1…9, so they can't be distinguished.
  (OMEN expects MCU auto-reports `EC BD` with `LED_ON_OFF=34`/
  `LIGHTING_CHANGE=35`/`BRIGHTNESS_CHANGE=36`, which this keyboard never sends,
  so OMEN's own hotkeys don't work here either.)
- **Backlight key (F9 action)** sends only a set-1 **break `0xC3`, with no make `0x43`**.
  A real F9 (Fn+F9) sends make + break.

hpkbd's scheme:
- **Backlight key:** static colour 100% → 50% → off; effects on/off.
- **Right Alt + 0–9:** pick a slot. The digit is swallowed.

On Windows the daemon detects the backlight key via Raw Input. On Linux the
input core drops a release with no press, so the module catches it with an
i8042 filter.

## Never sent

McuSDK2 also contains macro storage, `RESTORE_*_TO_DEFAULT`, colour
calibration, and `EnterUpdateMode` (firmware update). None of the tools send these.
