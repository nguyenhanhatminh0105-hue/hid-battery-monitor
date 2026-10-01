# Lofree Hyzen 67 vendor HID protocol

Decoded from the Lofree Hub web configurator (WebHID). Battery read verified
on real hardware.

## Interface

- VID `388D`, PID `0025`
- Usage page `0xFF1C`, usage `0x92` (device path contains `mi_01&col04`)
- OUTPUT report id 4 (63 bytes) and INPUT report id 4 (63 bytes)
- **No feature reports**, which is why a feature-report sweep finds nothing

## Frame

Indices as WebHID sees them (no report id byte). In a Win32 or hidapi buffer
the report id sits at byte 0, so add 1 to every index.

```
[0]=00  [1]=00  [2]=cmd  [3]=payloadLen  [4]=offsetLo  [5]=offsetHi  [6]=00  [7..]=payload
```

Responses use the same layout. Maximum payload is 56 bytes per frame.

## Exchange

1. START: `[00,00,01]` on report id 4. The device acks with byte[2] == 1.
2. Send the command frame.
3. Read input reports until byte[2] echoes the command.
4. END: `[00,00,02]`.

## Commands

| Command | Meaning |
|---|---|
| `0x1A` | Battery. Empty payload; percent is payload[0] |
| `0xAA` | Status. payload[0] == 0 means offline |

## How it was found

The configurator bundle is string-obfuscated: per-scope string arrays, a
base64/RC4 decoder, and a rotation IIFE that shuffles each array at load time.
Numeric literals are hidden as arithmetic (`9014*1+-2873+-6141` is 0), and the
decoder is re-aliased in each scope, so grepping for `sendReport` finds nothing.

Each array/decoder/rotation triple was extracted by brace matching and run in
Node to dump its strings. That exposed the HID read/write wrappers and the
battery call path. One bundle chunk turned out to be the firmware updater, a
decoy for this purpose; the battery path lives in the main chunk.
