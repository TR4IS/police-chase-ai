#!/bin/bash
# Install the built CopBot plugin and audio, then restart the game server. Puts the old plugin back if it won't start.
cd ~/docker/assettocorsa/assetto || exit 1

clients=$(curl -s -m 4 http://localhost:8081/INFO | grep -o '"clients":[0-9]*' | cut -d: -f2)
echo "players: $clients"
if [ "$clients" != "0" ]; then
  echo "PLAYERS ONLINE - installed nothing"
  exit 0
fi

rm -rf ~/build/plugin_before_nfs
cp -r plugins/CopBotPlugin ~/build/plugin_before_nfs

rm -rf plugins/CopBotPlugin/audio
mkdir -p plugins/CopBotPlugin/audio
cp ~/build/voices/*.mp3 ~/build/voices/CREDITS.txt plugins/CopBotPlugin/audio/
cp ~/build/out/CopBotPlugin/* plugins/CopBotPlugin/

kill -TERM "$(systemctl show assettocorsa -p MainPID --value)"
sleep 22

log=$(ls -t logs/log-*.txt | head -1)
if ! curl -s -m 4 http://localhost:8081/INFO | grep -q '"clients"'; then
  echo "SERVER NOT UP - putting the old plugin back"
  tail -30 "$log" | grep -E 'FTL|Exception' | cut -c1-200
  rm -rf plugins/CopBotPlugin
  cp -r ~/build/plugin_before_nfs plugins/CopBotPlugin
  sleep 15
fi

systemctl is-active assettocorsa
systemctl show assettocorsa -p NRestarts
tail -120 "$log" | grep -E 'FTL|Loaded plugin|\[CopBot\]|Starting TCP|update loop' | tail -7 | cut -c1-140
curl -s -m 4 http://localhost:8081/INFO | grep -oE '"(clients|track)":[^,]*'
echo "hud has pursuit meter: $(curl -s -m 4 http://localhost:8081/api/scripts/1 | grep -c drawPursuitMeter)"
for n in this_is_the_police radio_on_the_road radio_on_that_car radio_beep rotor nope; do
  printf '%s: ' "$n"
  curl -s -m 5 -o /dev/null -w '%{http_code} %{content_type} %{size_download}\n' "http://localhost:8081/copbot/audio/$n.mp3"
done
ls plugins/CopBotPlugin/audio | wc -l
grep -c "\[CopBot\] Error" "$log"
