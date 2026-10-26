#!/bin/bash
set -euo pipefail

# The hardened upstream image has bash but no curl. Keep management private.
exec 3<>/dev/tcp/127.0.0.1/9000
printf 'HEAD /health/ready HTTP/1.0\r\n\r\n' >&3
read -r status <&3
[[ "$status" == *" 200 "* ]]
