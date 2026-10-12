#!/bin/sh
# Double-click to convert. Runs convert.sh in Terminal and keeps the window open at the end.
cd "$(dirname "$0")" || exit 1
./convert.sh "$@"
status=$?
echo
if [ $status = 0 ]; then echo "Finished."; else echo "Conversion failed (exit code $status). The messages above say why."; fi
printf 'Press Return to close this window. '
read -r _
exit $status
