#!/usr/bin/env python3
"""Read-only usb2snes/SNI probe for FXPAK/SD2SNES.

This script intentionally sends only Name, DeviceList, Attach, Info and
GetAddress requests. It never sends PutAddress or binary write payloads.
"""

import argparse
import json
import sys

import websocket


DEFAULT_URL = "ws://127.0.0.1:23074"
DEFAULT_ADDRESS = 0xF50000
DEFAULT_SIZE = 0x100


def send_json(ws, opcode, operands=None, space="SNES"):
    request = {"Opcode": opcode, "Space": space}
    if operands is not None:
        request["Operands"] = operands
    ws.send(json.dumps(request))


def recv_json(ws):
    while True:
        message = ws.recv()
        if isinstance(message, str):
            return json.loads(message)


def read_exact(ws, address, size):
    send_json(ws, "GetAddress", [f"{address:X}", f"{size:X}"])
    data = bytearray()

    while len(data) < size:
        message = ws.recv()
        if isinstance(message, str):
            # Ignore unexpected text responses while waiting for the binary block.
            continue
        data.extend(message)

    return bytes(data[:size])


def dump_hex(data):
    for offset in range(0, len(data), 16):
        chunk = data[offset : offset + 16]
        print(f"{offset:04X}: " + " ".join(f"{byte:02X}" for byte in chunk))


def main():
    parser = argparse.ArgumentParser(description="Read-only SNI/usb2snes probe")
    parser.add_argument("--url", default=DEFAULT_URL, help="usb2snes websocket URL")
    parser.add_argument(
        "--address",
        type=lambda value: int(value, 0),
        default=DEFAULT_ADDRESS,
        help="address to read, e.g. 0xF50000",
    )
    parser.add_argument(
        "--size",
        type=lambda value: int(value, 0),
        default=DEFAULT_SIZE,
        help="number of bytes to read, e.g. 0x100",
    )
    args = parser.parse_args()

    if args.size <= 0:
        parser.error("--size must be greater than zero")

    ws = websocket.create_connection(args.url, timeout=10)
    try:
        send_json(ws, "Name", ["usb2snes_probe read-only"])

        send_json(ws, "DeviceList")
        devices_reply = recv_json(ws)
        devices = devices_reply.get("Results", [])
        print("Devices:", devices_reply)

        if not devices:
            print("No SNI/usb2snes device found", file=sys.stderr)
            return 2

        device = devices[0]
        print("Attaching to:", device)
        send_json(ws, "Attach", [device])

        send_json(ws, "Info")
        info = recv_json(ws)
        print("Info:", info)

        data = read_exact(ws, args.address, args.size)
        print(f"\nREAD ONLY ${args.address:06X} + ${args.size:X}:")
        dump_hex(data)
        return 0
    finally:
        ws.close()


if __name__ == "__main__":
    raise SystemExit(main())
