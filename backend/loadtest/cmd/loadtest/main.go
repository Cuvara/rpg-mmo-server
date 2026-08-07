// Command loadtest drives N concurrent virtual players through the real wire
// protocol (Gateway MsgAuth/MsgEnterWorld -> GameServer join -> input/snapshot
// loop) and reports client-observed latency and throughput alongside the
// server's own Prometheus counters.
//
// It exists to replace the unbenchmarked CCU guesses called out in
// backend/docs/ARCHITECTURE-DECISIONS.md ADR-7 with measurements. The headline
// question it answers is: how many players does ONE game server hold before
// gameserver_tick_duration_seconds p99 breaks the 66.67ms budget at 15Hz.
//
// Single run:
//
//	loadtest -players 100 -duration 60s -json run-100.json
//
// Sweep until something degrades:
//
//	loadtest -sweep 1,10,50,100,200,400 -duration 60s -json sweep.json
//
// Exit code is 0 when the run completed, 1 when it could not be executed. A
// DEGRADED verdict is a finding, not a failure, so it does not change the exit
// code — use -fail-on-degraded in CI if you want the opposite.
package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/duycuong/rpg-mmo/loadtest/load"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintf(os.Stderr, "loadtest: %v\n", err)
		os.Exit(1)
	}
}

func run() error {
	// Sweep orchestration lives here rather than in load.Config: a sweep is a
	// list of runs, and each run must stay independently reproducible from its
	// own recorded config.
	args, sweep, cooldown, failOnDegraded, repeat, err := extractSweepFlags(os.Args[1:])
	if err != nil {
		return err
	}

	cfg, err := load.LoadConfig(os.Getenv, args)
	if err != nil {
		return err
	}
	if len(sweep) == 0 {
		sweep = []int{cfg.Players}
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	results := make([]*load.Result, 0, len(sweep)*repeat)
	// Levels are repeated in the OUTER loop, so run k of every level happens
	// before run k+1 of any of them. Repeating a level back to back would
	// correlate its runs with whatever the host was doing for that minute, which
	// is precisely the variance the repeat exists to measure.
	for pass := 0; pass < repeat; pass++ {
		for i, n := range sweep {
			if ctx.Err() != nil {
				break
			}
			if (i > 0 || pass > 0) && cooldown > 0 {
				// Let the previous level's entities age out of the world before the
				// next one starts, so levels do not inherit each other's load.
				fmt.Printf("[loadtest] cooldown %s before the next level\n", cooldown)
				select {
				case <-ctx.Done():
				case <-time.After(cooldown):
				}
			}
			lvl := cfg
			lvl.Players = n
			res, err := load.NewRunner(lvl, os.Stdout).Run(ctx)
			if err != nil {
				if ctx.Err() != nil {
					break
				}
				return fmt.Errorf("run with %d players: %w", n, err)
			}
			results = append(results, res)
			if res.Verdict.Degraded {
				fmt.Printf("[loadtest] players=%d DEGRADED: %s\n", n, res.Verdict.Reason)
			}
		}
	}

	if len(results) == 0 {
		return fmt.Errorf("no run completed")
	}
	load.WriteSummary(os.Stdout, results)
	load.WriteCeiling(os.Stdout, load.ComputeCeiling(results))

	if cfg.JSONOut != "" {
		if cfg.JSONOut == "-" {
			if err := load.WriteJSON(os.Stdout, results); err != nil {
				return err
			}
		} else {
			f, err := os.Create(cfg.JSONOut)
			if err != nil {
				return fmt.Errorf("create %s: %w", cfg.JSONOut, err)
			}
			defer f.Close()
			if err := load.WriteJSON(f, results); err != nil {
				return fmt.Errorf("write %s: %w", cfg.JSONOut, err)
			}
			fmt.Printf("\n[loadtest] JSON written to %s\n", cfg.JSONOut)
		}
	}

	if failOnDegraded {
		for _, r := range results {
			if r.Verdict.Degraded {
				os.Exit(1)
			}
		}
	}
	return nil
}

// extractSweepFlags pulls the orchestration-only flags out of the argument list
// before load.LoadConfig sees it, so the two flag sets never collide.
func extractSweepFlags(args []string) (rest []string, sweep []int, cooldown time.Duration, failOnDegraded bool, repeat int, err error) {
	cooldown = 20 * time.Second
	repeat = 1
	rest = make([]string, 0, len(args))
	for i := 0; i < len(args); i++ {
		a := args[i]
		name, value, hasValue := strings.Cut(strings.TrimLeft(a, "-"), "=")
		takeValue := func() (string, bool) {
			if hasValue {
				return value, true
			}
			if i+1 < len(args) {
				i++
				return args[i], true
			}
			return "", false
		}
		switch name {
		case "sweep":
			v, ok := takeValue()
			if !ok {
				return nil, nil, 0, false, 0, fmt.Errorf("-sweep needs a value, e.g. -sweep 1,10,50,100")
			}
			sweep, err = parseSweep(v)
			if err != nil {
				return nil, nil, 0, false, 0, err
			}
		case "cooldown":
			v, ok := takeValue()
			if !ok {
				return nil, nil, 0, false, 0, fmt.Errorf("-cooldown needs a value")
			}
			cooldown, err = time.ParseDuration(v)
			if err != nil {
				return nil, nil, 0, false, 0, fmt.Errorf("-cooldown: %w", err)
			}
		case "repeat":
			v, ok := takeValue()
			if !ok {
				return nil, nil, 0, false, 0, fmt.Errorf("-repeat needs a value, e.g. -repeat 3")
			}
			repeat, err = strconv.Atoi(v)
			if err != nil || repeat < 1 {
				return nil, nil, 0, false, 0, fmt.Errorf("-repeat must be a positive integer, got %q", v)
			}
		case "fail-on-degraded":
			if hasValue {
				failOnDegraded = value == "true" || value == "1"
			} else {
				failOnDegraded = true
			}
		default:
			rest = append(rest, a)
		}
	}
	return rest, sweep, cooldown, failOnDegraded, repeat, nil
}

func parseSweep(s string) ([]int, error) {
	parts := strings.Split(s, ",")
	out := make([]int, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p == "" {
			continue
		}
		n, err := strconv.Atoi(p)
		if err != nil {
			return nil, fmt.Errorf("-sweep %q: %w", s, err)
		}
		if n <= 0 {
			return nil, fmt.Errorf("-sweep %q: player counts must be > 0", s)
		}
		out = append(out, n)
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("-sweep %q: no player counts", s)
	}
	return out, nil
}
