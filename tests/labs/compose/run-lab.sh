#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
COMPOSE=(docker compose -f "${SCRIPT_DIR}/docker-compose.yml" --project-directory "${SCRIPT_DIR}")
ROAM_DLL="/work/roam/src/Roam/bin/Release/net10.0/Roam.dll"
PROFILE="${ROAM_LAB_PROFILE:-kiosk}"
KEEP_LAB="${ROAM_KEEP_COMPOSE_LAB:-0}"

log() {
  printf '[roam-lab] %s\n' "$*"
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    printf 'roam compose lab requires %s, but it was not found in PATH.\n' "$1" >&2
    return 127
  fi
}

compose_exec() {
  "${COMPOSE[@]}" exec -T --user roam source bash -lc "$1"
}

cleanup() {
  if [[ "${KEEP_LAB}" != "1" ]]; then
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  else
    log "leaving Compose lab running because ROAM_KEEP_COMPOSE_LAB=1"
  fi
}

wait_for_source_ssh() {
  local attempt
  for attempt in $(seq 1 60); do
    if "${COMPOSE[@]}" exec -T source bash -lc 'test -S /run/sshd.pid 2>/dev/null || pgrep -x sshd >/dev/null' >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  printf 'source sshd did not become ready in time\n' >&2
  return 1
}

wait_for_lab_ssh() {
  local host
  for host in build target bastion; do
    log "waiting for SSH host ${host}"
    local attempt
    for attempt in $(seq 1 60); do
      if compose_exec "ssh -F /home/roam/.ssh/config ${host} hostname" >/dev/null 2>&1; then
        break
      fi
      if [[ "${attempt}" == "60" ]]; then
        printf 'SSH host %s did not become reachable in time\n' "${host}" >&2
        return 1
      fi
      sleep 1
    done
  done
}

verify_toolchain_shape() {
  log "verifying lab toolchain shape"
  compose_exec 'dotnet --info >/dev/null'
  compose_exec 'ssh build dotnet --info >/dev/null'
  compose_exec 'ssh target "if command -v dotnet >/dev/null 2>&1; then echo target unexpectedly has dotnet >&2; exit 9; fi"'
}

run_roam_once() {
  local label="$1"
  log "running roam profile ${PROFILE} (${label})"
  compose_exec "cd /work/source/repo && dotnet ${ROAM_DLL} run ${PROFILE}"
}

verify_deploy_state() {
  log "verifying target deploy state"
  compose_exec 'ssh target "test -x /opt/roam-fixture/Roam.SampleApp"'
  compose_exec 'ssh target "pgrep -f Roam.SampleApp >/dev/null"'
  compose_exec 'ssh target "test -f /opt/roam-fixture/assets/nested/probe.txt"'
  compose_exec 'ssh target "grep -qx nested-publish-probe /opt/roam-fixture/assets/nested/probe.txt"'
  compose_exec "test -f /work/source/repo/.roam/manifests/${PROFILE}/source.json"
  compose_exec "test -f /work/source/repo/.roam/manifests/${PROFILE}/artifacts.json"
}

verify_remote_artifact_materialization() {
  log "verifying remote publish materialization preserves nested files, mtimes, and temp cleanup"
  compose_exec 'ssh build "test -f /work/build/repo/bin/Release/net10.0/linux-x64/publish/assets/nested/probe.txt"'
  compose_exec 'bash -lc '\''build_mtime=$(ssh build "stat -c %Y /work/build/repo/bin/Release/net10.0/linux-x64/publish/assets/nested/probe.txt"); target_mtime=$(ssh target "stat -c %Y /opt/roam-fixture/assets/nested/probe.txt"); test "$build_mtime" = "$target_mtime"'\'''
  compose_exec 'bash -lc '\''if compgen -G "/tmp/roam-publish-*" >/dev/null; then ls -ld /tmp/roam-publish-* >&2; exit 11; fi'\'''
}

verify_deploy_ownership_boundary() {
  log "verifying manifest-owned stale delete and unmanaged deploy file preservation"
  compose_exec 'ssh target "mkdir -p /opt/roam-fixture && printf delete-me > /opt/roam-fixture/stale-owned.txt && printf keep-me > /opt/roam-fixture/unmanaged-sentinel.txt"'
  compose_exec "cd /work/source/repo && sed -i '/\"Entries\": \[/a\\    {\\n      \"Path\": \"stale-owned.txt\",\\n      \"Size\": 9,\\n      \"Mtime\": 0,\\n      \"ContentHash\": null\\n    },' .roam/manifests/${PROFILE}/artifacts.json && grep -q stale-owned.txt .roam/manifests/${PROFILE}/artifacts.json"
  run_roam_once "with stale owned file and unmanaged sentinel"
  compose_exec "ssh target 'test ! -e /opt/roam-fixture/stale-owned.txt'"
  compose_exec "ssh target 'grep -qx keep-me /opt/roam-fixture/unmanaged-sentinel.txt'"
  compose_exec "cd /work/source/repo && ! grep -q stale-owned.txt .roam/manifests/${PROFILE}/artifacts.json"
}

main() {
  require_command docker

  log "building Roam release binary on host"
  dotnet build -c Release "${REPO_ROOT}/src/Roam/Roam.csproj" >/dev/null

  trap cleanup EXIT

  log "starting Compose lab"
  "${COMPOSE[@]}" up -d --build --remove-orphans
  wait_for_source_ssh
  wait_for_lab_ssh
  verify_toolchain_shape

  run_roam_once "cold"
  verify_deploy_state
  run_roam_once "warm"
  verify_deploy_state
  verify_remote_artifact_materialization
  verify_deploy_ownership_boundary

  log "compose lab passed"
}

main "$@"
