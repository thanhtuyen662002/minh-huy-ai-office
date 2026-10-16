"""Inspect public dotnet version output only; never print rejected input."""
import argparse
import re
import sys

SDK_FLOOR = 402
RUNTIME_FLOOR = 13
INVALID = "Required October .NET servicing floor is unavailable."


def require_sdk(text):
    if len(text) > 128 or not re.fullmatch(r"10\.0\.(?:4[0-9]{2})\s*", text):
        raise ValueError(INVALID)
    if int(text.strip().split(".")[2]) < SDK_FLOOR:
        raise ValueError(INVALID)
    return text.strip()


def require_frameworks(text, aspnet=False, host=False, windows_desktop=False):
    if len(text) > 8192:
        raise ValueError(INVALID)
    lines = text.splitlines()
    if not 1 <= len(lines) <= 16:
        raise ValueError(INVALID)
    found = set()
    selected_patches = {}
    identities = set()
    number = r"(0|[1-9][0-9]*)"
    pattern = r"(Microsoft\.(?:NETCore|AspNetCore|WindowsDesktop)\.App) " + r"\.".join([number] * 3) + r" \[[^\r\n\[\]]+\]"
    for line in lines:
        match = re.fullmatch(pattern, line)
        if match is None:
            raise ValueError(INVALID)
        name, major, minor, patch = match.groups()
        version = tuple(map(int, (major, minor, patch)))
        identity = (name, version)
        if identity in identities:
            raise ValueError(INVALID)
        identities.add(identity)
        if version[:2] == (10, 0):
            if version[2] < RUNTIME_FLOOR and not host:
                raise ValueError(INVALID)
            selected_patches[name] = max(selected_patches.get(name, -1), version[2])
    found.update(name for name, patch in selected_patches.items() if patch >= RUNTIME_FLOOR)
    required = {"Microsoft.NETCore.App"}
    if aspnet:
        required.add("Microsoft.AspNetCore.App")
    if windows_desktop:
        required.add("Microsoft.WindowsDesktop.App")
    if not required <= found:
        raise ValueError(INVALID)
    return {name: f"10.0.{selected_patches[name]}" for name in sorted(required)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk", action="store_true")
    parser.add_argument("--aspnet", action="store_true")
    parser.add_argument("--host", action="store_true")
    parser.add_argument("--windows-desktop", action="store_true")
    args = parser.parse_args()
    try:
        text = sys.stdin.read(8193)
        if args.sdk:
            if args.aspnet or args.host or args.windows_desktop:
                raise ValueError(INVALID)
            verified = require_sdk(text)
        else:
            verified = require_frameworks(text, args.aspnet, args.host, args.windows_desktop)
    except (ValueError, OverflowError):
        print(INVALID, file=sys.stderr)
        return 1
    if args.sdk:
        print(f"PASS required October .NET SDK servicing floor: {verified}")
    else:
        versions = ", ".join(f"{name} {version}" for name, version in verified.items())
        print(f"PASS required October .NET shared-framework servicing floor: {versions}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
