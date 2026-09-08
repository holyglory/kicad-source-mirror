#!/bin/bash
set -euo pipefail

kicad_probe_root=/home/holyglory/kicad
kicad_probe_context="$kicad_probe_root/automation/artifacts/clean-debian-context"
kicad_probe_evidence="$kicad_probe_root/automation/artifacts/rootless-debian-evidence"
if [ -e "$kicad_probe_evidence" ]; then
    mkdir -p "$kicad_probe_root/automation/artifacts/rootless-debian-history"
    mv "$kicad_probe_evidence" "$kicad_probe_root/automation/artifacts/rootless-debian-history/$(date -u +%Y%m%dT%H%M%S)-$$"
fi
mkdir -p "$kicad_probe_evidence"
cd "$kicad_probe_context"
sha256sum --check subject.sha256

# Bootstrap success is only setup/evidence collection. The separate compiled
# verification check must accept result.json before the governed graph passes.
# mmdebstrap owns and removes the private mount/PID/user namespace and rootfs.
mmdebstrap --mode=unshare --variant=minbase --format=null \
    --include=xvfb,xauth,passwd \
    --customize-hook="copy-in $kicad_probe_context/subject.deb /tmp" \
    --customize-hook='chroot "$1" apt-get install -y --no-install-recommends /tmp/subject.deb' \
    --customize-hook="copy-in $kicad_probe_context/probe /" \
    --customize-hook='chroot "$1" useradd --uid 1000 --create-home probe' \
    --customize-hook='chroot "$1" install -d -m 1777 /tmp/.X11-unix' \
    --customize-hook='chroot "$1" install -d -o 1000 -g 1000 /evidence' \
    --customize-hook='chroot "$1" runuser -u probe -- /probe/kicad-package-probe /opt/kicad-codex/preview-20260908T093000Z-a5666e707777 /evidence || test -f "$1/evidence/result.json"' \
    --customize-hook="copy-out /evidence $kicad_probe_evidence" \
    trixie /dev/null https://deb.debian.org/debian
