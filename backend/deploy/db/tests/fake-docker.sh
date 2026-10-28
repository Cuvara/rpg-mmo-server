#!/usr/bin/env bash
# Minimal stateful fake of the docker CLI for tests/rehearsal_test.sh.
# State lives in $FAKE_DOCKER_DIR:
#   objects/{c,v,n}_<name>   one file per created object: line 1 = run label,
#                            line 2 = space-joined networks (containers only)
#   normal-names.txt         normal rpg-* container names (one per line)
#   normal-volumes.txt       normal rpg-* volume names
#   normal-networks.txt      "<id> <name>" per line
#   inspect.txt              output of `docker inspect --format container|...`
#   calls.log                every invocation, one per line
set -u
D="${FAKE_DOCKER_DIR:?}"
mkdir -p "$D/objects"
echo "$*" >> "$D/calls.log"
cmd="${1:-}"; shift || true

# opt VALUE-FLAG args...: print the value following flag in the remaining args
flag_value() {
	local flag="$1"; shift
	while [ "$#" -gt 0 ]; do
		if [ "$1" = "$flag" ]; then printf '%s' "${2:-}"; return 0; fi
		shift
	done
}
label_of() { sed -n 1p "$1"; }
created() { local k="$1" f; for f in "$D"/objects/"$k"_*; do [ -e "$f" ] && basename "$f" | cut -c3-; done; }
record() { # kind name label networks
	printf '%s\n%s\n' "$3" "$4" > "$D/objects/$1_$2"
}
label_arg() { # extract run label from --label org.rpg.rehearsal.run=X args
	local a prev=""
	for a in "$@"; do
		if [ "$prev" = "--label" ]; then case "$a" in org.rpg.rehearsal.run=*) printf '%s' "${a#*=}";; esac; fi
		prev="$a"
	done
}
last_arg() { local a; for a in "$@"; do :; done; printf '%s' "$a"; }

inspect_obj() { # kind args...
	local kind="$1"; shift
	local fmt="" names=() a
	while [ "$#" -gt 0 ]; do
		case "$1" in
			-f|--format) fmt="$2"; shift 2 ;;
			*) names+=("$1"); shift ;;
		esac
	done
	if [ -z "$fmt" ]; then
		[ -e "$D/objects/${kind}_${names[0]}" ]; return
	fi
	case "$fmt" in
		container\|*) cat "$D/inspect.txt"; return 0 ;;
	esac
	local f="$D/objects/${kind}_${names[0]}"
	[ -e "$f" ] || return 1
	case "$fmt" in
		*org.rpg.rehearsal.run*) label_of "$f" ;;
		*NetworkSettings.Networks*) sed -n 2p "$f" ;;
	esac
}

case "$cmd" in
	info|pull|image) exit 0 ;;
	context) echo default ;;
	ps)
		if printf '%s' "$*" | grep -q '{{.ID}}'; then
			[ -s "$D/normal-names.txt" ] && awk '{print "id-" $0 "|img|" $0 "|running|Up 3 hours"}' "$D/normal-names.txt"
		else
			cat "$D/normal-names.txt" 2>/dev/null
			created c
		fi
		;;
	inspect) inspect_obj c "$@"; exit $? ;;
	run)
		name="$(flag_value --name "$@")"
		record c "$name" "$(label_arg "$@")" "$(flag_value --network "$@") "
		echo "id-$name"
		;;
	rm)
		for a in "$@"; do case "$a" in -*) ;; *) rm -f "$D/objects/c_$a" ;; esac; done
		;;
	exec)
		all="$*"
		case "$all" in
			*pg_isready*) exit 0 ;;
			*"redis-cli PING"*) echo PONG ;;
			*"redis-cli DBSIZE"*) echo 4 ;;
			*pg_database*) : ;;
			*"FROM rehearsal_synthetic_accounts"*) echo 2 ;;
			*"tablename IN"*) echo 4 ;;
			*"FROM player_states; SELECT"*) printf '1\n1\n1\n1\n' ;;
			*pg_indexes*) printf 'table|a\ntable|b\ntable|c\ntable|d\n' ;;
		esac
		;;
	volume)
		sub="$1"; shift
		case "$sub" in
			ls)
				if printf '%s' "$*" | grep -q 'volume|'; then
					{ cat "$D/normal-volumes.txt" 2>/dev/null; created v; } | awk 'NF {print "volume|" $0}'
				else
					{ cat "$D/normal-volumes.txt" 2>/dev/null; created v; }
				fi
				;;
			create) record v "$(last_arg "$@")" "$(label_arg "$@")" "" ;;
			inspect) inspect_obj v "$@"; exit $? ;;
			rm) for a in "$@"; do rm -f "$D/objects/v_$a"; done ;;
		esac
		;;
	network)
		sub="$1"; shift
		case "$sub" in
			ls) awk 'NF {print "network|" $1 "|" $2}' "$D/normal-networks.txt" 2>/dev/null ;;
			create) record n "$(last_arg "$@")" "$(label_arg "$@")" "" ;;
			inspect) inspect_obj n "$@"; exit $? ;;
			rm) for a in "$@"; do rm -f "$D/objects/n_$a"; done ;;
		esac
		;;
esac
exit 0
