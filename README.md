# HID Battery Monitor

Reads the battery level of three 2.4 GHz wireless peripherals on Windows and
prints it as a single number, so a desktop status bar (Rainmeter) can show it.

Wireless dongles don't expose battery through Windows' Bluetooth battery
property, so each device needs its own path:

| Device | How it is read |
|---|---|
| Lofree Hyzen 67 keyboard | **Undocumented vendor HID protocol, reverse engineered** (see [docs/lofree-hyzen-protocol.md](docs/lofree-hyzen-protocol.md)) |
| Lamzu Maya X mouse | 65-byte HID feature report; battery and polling rate |
| Turtle Beach Atlas Air headset | Parses the vendor app's live log files; audio format from the Windows audio endpoint registry |

## Highlights

- **Protocol reverse engineering.** The keyboard's only documentation is its
  vendor web configurator, which talks to the device over WebHID and ships as
  string-obfuscated JavaScript. Deobfuscating it recovered the 63-byte frame
  format, the START/END handshake and the battery command (`0x1A`), verified on
  real hardware.
- **Raw Win32 HID through P/Invoke.** Device discovery with SetupAPI, then
  `CreateFile`, `HidD_SetFeature`/`HidD_GetFeature`, and overlapped
  `ReadFile`/`WriteFile` with event-based timeouts and `CancelIo`.
- **Safe under concurrency.** Two reads launched at once made the exchange fail
  3 times out of 3. A cross-process named mutex plus bounded retries fixed it.
- **Cheap.** No resident process: each call starts, reads and exits. Dropping a
  redundant status round trip cut a keyboard read from 365 ms to 216 ms.

## Two Win32 traps worth knowing

1. `hidapi` pads short writes, but Windows `WriteFile` on a HID handle does
   not. Every write must be exactly `OutputReportByteLength` bytes, or it fails.
2. If the handle is opened with `FILE_FLAG_OVERLAPPED`, `WriteFile` needs an
   `OVERLAPPED` too, not just `ReadFile`. Passing `NULL` returns
   `ERROR_INVALID_PARAMETER` (87).

## Build

Uses the C# compiler that ships with Windows (.NET Framework 4.x), no SDK needed:

```
build.bat
```

## Usage

```
BatteryRead.exe lofree        keyboard battery, 1-100 or -1
BatteryRead.exe lamzu         mouse battery, 0-100 or -1
BatteryRead.exe lamzu-rate    mouse polling rate, e.g. "8K"
BatteryRead.exe tbair         headset battery; value-1000 means last known but stale
BatteryRead.exe tbair-rate    headset audio bitrate, e.g. "4.6M"
```

Set `BR_DEBUG=1` to trace each step of the keyboard exchange on stderr.

## Credits

The Lamzu report layout follows [lamzu-ctl](https://github.com/anany1702/lamzu-ctl).
The 8 kHz polling-rate code (`0x40`) was confirmed on hardware; lamzu-ctl maps it
to 4 kHz.
