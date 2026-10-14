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


def require_frameworks(text, aspnet=False, host=False):
    if len(text) > 8192:
        raise ValueError(INVALID)
    lines = text.splitlines()
    if not 1 <= len(lines) <= 16:
        raise ValueError(INVALID)
    found = set()
    selected_patches = {}
    identities = set()
    pattern = r"(Microsoft\.(?:NETCore|AspNetCore|WindowsDesktop)\.App) ([0-9]+)\.([0-9]+)\.([0-9]+) \[[^\r\n\[\]]+\]"
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
    if not required <= found:
        raise ValueError(INVALID)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sdk", action="store_true")
    parser.add_argument("--aspnet", action="store_true")
    parser.add_argument("--host", action="store_true")
    args = parser.parse_args()
    try:
        text = sys.stdin.read(8193)
        if args.sdk:
            if args.aspnet or args.host:
                raise ValueError(INVALID)
            require_sdk(text)
        else:
            require_frameworks(text, args.aspnet, args.host)
    except (ValueError, OverflowError):
        print(INVALID, file=sys.stderr)
        return 1
    print("PASS required October .NET SDK servicing floor" if args.sdk else
          "PASS required October .NET shared-framework servicing floor")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
