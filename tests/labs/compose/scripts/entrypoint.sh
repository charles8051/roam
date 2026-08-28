#!/usr/bin/env bash

set -euo pipefail

mkdir -p /var/run/sshd /home/roam/.ssh /lab-state/source
chmod 700 /home/roam/.ssh

if [[ ! -f /lab-state/source/id_ed25519 ]]; then
  ssh-keygen -q -t ed25519 -N "" -f /lab-state/source/id_ed25519
fi

cp /lab-state/source/id_ed25519.pub /home/roam/.ssh/authorized_keys
chmod 600 /home/roam/.ssh/authorized_keys

if [[ "${ROAM_ROLE:-}" == "source" ]]; then
  cp /lab-state/source/id_ed25519 /home/roam/.ssh/id_ed25519
  cp /lab-state/source/id_ed25519.pub /home/roam/.ssh/id_ed25519.pub
  chmod 600 /home/roam/.ssh/id_ed25519

  if [[ -f /lab-state/source/ssh_config ]]; then
    cp /lab-state/source/ssh_config /home/roam/.ssh/config
    chmod 600 /home/roam/.ssh/config
  fi

  if [[ -d /work/source/fixture ]]; then
    mkdir -p /work/source/repo
    cp -a /work/source/fixture/. /work/source/repo/
    chown -R roam:roam /work/source/repo
    if [[ ! -d /work/source/repo/.git ]]; then
      sudo -u roam git -C /work/source/repo init -q
      sudo -u roam git -C /work/source/repo config user.email roam-lab@example.invalid
      sudo -u roam git -C /work/source/repo config user.name "Roam Lab"
      sudo -u roam git -C /work/source/repo add .
      sudo -u roam git -C /work/source/repo commit -q -m "initial fixture"
    fi
  fi
fi

if [[ -n "${ROAM_CREATE_WORKSPACE:-}" ]]; then
  mkdir -p "${ROAM_CREATE_WORKSPACE}"
  chown -R roam:roam "${ROAM_CREATE_WORKSPACE}"
fi

chown -R roam:roam /home/roam /lab-state || true
for path in /work/source /work/build; do
  if [[ -e "${path}" ]]; then
    chown -R roam:roam "${path}" || true
  fi
done

exec /usr/sbin/sshd -D -e