#!/bin/sh
# Cut the CC0 recordings into voice lines. Runs inside the Jellyfin image for its ffmpeg, with no network.
# Input: /in/<freesound id>/clip.mp3   Output: /out/<line>.mp3
FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
cut() {
  name=$1; id=$2; from=$3; to=$4
  length=$(awk "BEGIN{print $to-$from}")
  fade=$(awk "BEGIN{print $to-$from-0.04}")
  "$FF" -y -loglevel error -ss "$from" -to "$to" -i "/in/$id/clip.mp3" \
    -af "afade=t=in:d=0.02,afade=t=out:st=$fade:d=0.04,loudnorm=I=-14:TP=-1.5:LRA=11" \
    -ac 1 -ar 44100 -b:a 112k "/out/$name.mp3" || echo "FAILED $name"
}
cut this_is_the_police  165341 0.00 1.40
cut pull_over           159749 0.00 1.25
cut pull_over_side      159749 1.40 2.95
cut pull_over_now       159749 5.70 7.85
cut move_vehicle_over   159749 8.20 10.25
cut pull_it_over        159749 10.25 11.50
cut freeze              159755 14.60 17.40
cut under_arrest        823506 0.00 1.35
cut placed_under_arrest 144702 4.00 6.25
cut hands_on_head       144702 1.45 3.65
cut see_those_hands     217232 0.00 1.45
cut radio_pull_him_over 592684 0.00 3.15
cut radio_all_units     752406 1.40 5.25
cut radio_on_the_road   648463 0.00 1.30
cut radio_on_that_car   638265 5.65 7.40
cut radio_beep          732209 0.00 0.80

# A few seconds of rotor, overlapped into a longer loop so the join is hard to hear
R=/in/187678/clip.mp3
"$FF" -y -loglevel error -i "$R" -i "$R" -i "$R" -i "$R" -i "$R" -filter_complex \
  "[0][1]acrossfade=d=0.8[a];[a][2]acrossfade=d=0.8[b];[b][3]acrossfade=d=0.8[c];[c][4]acrossfade=d=0.8,loudnorm=I=-20:TP=-2:LRA=7[out]" \
  -map "[out]" -ac 1 -ar 44100 -b:a 96k /out/rotor.mp3 || echo "FAILED rotor"

ls -la /out | awk '{print $5, $9}' | grep mp3
