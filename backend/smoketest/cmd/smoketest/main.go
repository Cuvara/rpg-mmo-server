// Command smoketest runs the post-deploy smoke test: Nakama health -> device
// auth -> gateway_token RPC -> gateway MsgAuth/MsgEnterWorld -> game server
// join -> input/snapshot loop -> clean disconnect.
//
// Exit code 0 and a final "SMOKE=PASS" line mean the deployed stack serves the
// full flow; any failure prints "SMOKE=FAIL" and exits 1.
package main

import (
	"fmt"
	"os"

	"github.com/duycuong/rpg-mmo/smoketest/smoke"
)

func main() {
	cfg, err := smoke.LoadConfig(os.Getenv, os.Args[1:])
	if err != nil {
		fmt.Fprintf(os.Stderr, "config: %v\n", err)
		fmt.Println(smoke.FinalLine(false))
		os.Exit(1)
	}
	if !smoke.NewRunner(cfg, os.Stdout).Run() {
		os.Exit(1)
	}
}
