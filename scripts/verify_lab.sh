#!/usr/bin/env bash
set -euo pipefail

cd ~/roam/tests/labs/compose

docker compose up -d --force-recreate source build target bastion >/dev/null

source_cid=$(docker compose ps -q source)
build_cid=$(docker compose ps -q build)
target_cid=$(docker compose ps -q target)

# copy current roam repo into the source host so the CLI can run inside the lab
if docker exec "$source_cid" test -d /work/source/roam; then
  docker exec "$source_cid" rm -rf /work/source/roam
fi
docker cp ~/roam/. "$source_cid":/work/source/roam

docker exec "$source_cid" mkdir -p /work/source/repo /opt/roam-fixture || true
docker exec "$build_cid" mkdir -p /work/build || true
docker exec "$target_cid" mkdir -p /opt/roam-fixture || true

docker exec "$source_cid" chown -R roam:roam /work/source/repo /work/source/roam /opt/roam-fixture || true
docker exec "$build_cid" chown -R roam:roam /work/build || true
docker exec "$target_cid" chown -R roam:roam /opt/roam-fixture || true

# prepare the sample app source repo that roam will sync from
if docker compose exec -T --user roam source test -d /work/source/repo/.git; then
  docker compose exec -T --user roam source rm -rf /work/source/repo/.git
fi

docker compose exec -T --user roam source bash -lc '
  set -euo pipefail
  rm -rf /work/source/repo/* /work/source/repo/.[!.]* /work/source/repo/..?* || true
  cp -a /work/source/fixture/. /work/source/repo/
  rm -rf /work/source/repo/.git
  cd /work/source/repo
  git init -q
  git config user.email roam@example.test
  git config user.name RoamLab
  git add .
  git commit -qm initial
  ssh -G build >/dev/null
  ssh -G target >/dev/null
'

# verify init scaffolding in a clean temp directory

docker compose exec -T --user roam source bash -lc '
  set -euo pipefail
  rm -rf /tmp/init-sample
  mkdir -p /tmp/init-sample
  cp -a /work/source/repo/. /tmp/init-sample/
  rm -f /tmp/init-sample/roamfile.yaml
  cd /tmp/init-sample
  dotnet run --project /work/source/roam/src/Roam/Roam.csproj -- init --csproj SampleApp.csproj --force
  test -f roamfile.yaml
'

# verify attach and the three profile flows end-to-end

docker compose exec -T --user roam source bash -lc '
  set -euo pipefail
  cd /work/source/repo
  dotnet run --project /work/source/roam/src/Roam/Roam.csproj -- attach workstation-to-laptop --output /tmp/launch.json --regenerate
  test -f /tmp/launch.json
  dotnet run --project /work/source/roam/src/Roam/Roam.csproj -- run dev-local
  dotnet run --project /work/source/roam/src/Roam/Roam.csproj -- run workstation-to-laptop
  dotnet run --project /work/source/roam/src/Roam/Roam.csproj -- run kiosk
'

echo SMOKE_OK
