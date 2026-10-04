// pokerland-tracker uploads PokerStars hand histories to pokerland-api.
//
//	pokerland-tracker login            save the client token from your Settings page
//	pokerland-tracker run              watch the hand-history folders and upload (launchd runs this)
//	pokerland-tracker once             upload what is there now, then exit
//	pokerland-tracker status           what is tracked and when it last uploaded
//	pokerland-tracker doctor           check the setup
//	pokerland-tracker logout           forget the token
//	pokerland-tracker version
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"regexp"
	"runtime"
	"syscall"
	"time"

	"github.com/jwc20/pokerland-trackers/mac/internal/api"
	"github.com/jwc20/pokerland-trackers/mac/internal/config"
	"github.com/jwc20/pokerland-trackers/mac/internal/follower"
	"github.com/jwc20/pokerland-trackers/mac/internal/platform"
	"github.com/jwc20/pokerland-trackers/mac/internal/state"
)

// Set by the build: -ldflags "-X main.version=1.2.3".
var version = "0.0.0-dev"

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	dataDir := platform.DataDir()
	var err error
	switch os.Args[1] {
	case "login":
		err = login(dataDir, os.Args[2:])
	case "run":
		err = run(dataDir, os.Args[2:], false)
	case "once":
		err = run(dataDir, os.Args[2:], true)
	case "status":
		err = status(dataDir)
	case "doctor":
		err = doctor(dataDir)
	case "logout":
		err = logout(dataDir)
	case "version", "--version", "-v":
		fmt.Println(version)
	case "help", "--help", "-h":
		usage()
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "pokerland-tracker:", err)
		os.Exit(1)
	}
}

func usage() {
	fmt.Fprintln(os.Stderr, `Usage: pokerland-tracker <command>

Commands:
  login     save the client token from your Settings page (--api to use another server)
  run       watch the hand-history folders and upload new hands
  once      upload what is there now, then exit
  status    what is tracked and when it last uploaded
  doctor    check the setup
  logout    forget the token
  version`)
}

func userAgent() string {
	return follower.UserAgent(version, platform.Name(), runtime.GOARCH)
}

func login(dataDir string, args []string) error {
	flags := flag.NewFlagSet("login", flag.ContinueOnError)
	apiURL := flags.String("api", "", "API base URL (default: the saved one, or "+config.DefaultAPIBaseURL+")")
	tokenFlag := flags.String("token", "", "client token (default: prompt without echo)")
	if err := flags.Parse(args); err != nil {
		return err
	}
	cfg, err := config.Load(dataDir)
	if err != nil {
		return err
	}
	if *apiURL != "" {
		cfg.APIBaseURL = *apiURL
	}
	token := *tokenFlag
	if token == "" {
		token, err = platform.ReadSecret("Client token (from your Pokerland Settings page): ")
		if err != nil {
			return err
		}
	}
	if !regexp.MustCompile(`^[0-9a-f]{32}$`).MatchString(token) {
		return errors.New("that is not a client token: expected 32 hex characters")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	username, err := api.New(cfg.APIBaseURL, token, userAgent()).Me(ctx)
	if err != nil {
		var status *api.StatusError
		if errors.As(err, &status) && status.Status == 401 {
			return errors.New("the server rejected that token")
		}
		return fmt.Errorf("could not reach %s: %w", cfg.APIBaseURL, err)
	}
	cfg.Token = token
	if err := cfg.Save(dataDir); err != nil {
		return err
	}
	fmt.Printf("Connected as %s. Start the tracker with: brew services start pokerland-tracker\n", username)
	return nil
}

func logout(dataDir string) error {
	cfg, err := config.Load(dataDir)
	if err != nil {
		return err
	}
	cfg.Token = ""
	return cfg.Save(dataDir)
}

func roots(cfg *config.Config, extra []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, root := range append(append(platform.DefaultRoots(), cfg.ExtraRoots...), extra...) {
		if abs, err := filepath.Abs(root); err == nil && !seen[abs] {
			seen[abs] = true
			out = append(out, abs)
		}
	}
	return out
}

type rootList []string

func (r *rootList) String() string     { return fmt.Sprint(*r) }
func (r *rootList) Set(v string) error { *r = append(*r, v); return nil }

func run(dataDir string, args []string, once bool) error {
	flags := flag.NewFlagSet("run", flag.ContinueOnError)
	var extra rootList
	flags.Var(&extra, "root", "extra hand-history directory to watch (repeatable)")
	if err := flags.Parse(args); err != nil {
		return err
	}
	cfg, err := config.Load(dataDir)
	if err != nil {
		return err
	}
	if cfg.Token == "" {
		return errors.New("no client token saved; run `pokerland-tracker login` first")
	}
	watched := roots(cfg, extra)
	if len(watched) == 0 {
		return errors.New("no PokerStars HandHistory folder found; pass one with --root or add it to config.json")
	}
	store, err := state.Load(filepath.Join(dataDir, "state.json"))
	if err != nil {
		return fmt.Errorf("state.json is unreadable (%w); delete it to start over", err)
	}
	logger := slog.New(slog.NewTextHandler(os.Stderr, nil))
	f := follower.New(api.New(cfg.APIBaseURL, cfg.Token, userAgent()), store, follower.Options{
		Roots:         watched,
		Platform:      platform.Name(),
		ClientVersion: version,
		Log:           logger,
		Notify:        platform.Notify,
	})
	logger.Info("tracker started", "version", version, "api", cfg.APIBaseURL, "roots", watched)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	statusPath := filepath.Join(dataDir, "status.json")
	for {
		f.Poll(ctx)
		writeStatus(statusPath, f.Status())
		if once || ctx.Err() != nil {
			return nil
		}
		select {
		case <-ctx.Done():
			logger.Info("tracker stopping")
			return nil
		case <-time.After(f.PollInterval()):
		}
	}
}

// writeStatus is best effort: `status` reads it while the daemon runs.
func writeStatus(path string, s follower.Status) {
	data, err := json.MarshalIndent(s, "", "  ")
	if err == nil {
		_ = os.WriteFile(path, data, 0o600)
	}
}

func status(dataDir string) error {
	cfg, err := config.Load(dataDir)
	if err != nil {
		return err
	}
	fmt.Printf("API:      %s\n", cfg.APIBaseURL)
	if cfg.Token == "" {
		fmt.Println("Token:    not set (run `pokerland-tracker login`)")
	} else {
		fmt.Printf("Token:    %s…\n", cfg.Token[:6])
	}
	fmt.Printf("Roots:    %v\n", roots(cfg, nil))

	var s follower.Status
	if data, err := os.ReadFile(filepath.Join(dataDir, "status.json")); err == nil && json.Unmarshal(data, &s) == nil {
		fmt.Printf("Last poll:   %s\n", ago(s.LastPollAt))
		fmt.Printf("Last upload: %s (%d bytes this run)\n", ago(s.LastUploadAt), s.BytesUploaded)
		if s.PausedReason != "" {
			fmt.Printf("Paused:      %s until %s\n", s.PausedReason, s.PausedUntil.Local().Format(time.Kitchen))
		}
		if s.LastError != "" {
			fmt.Printf("Last error:  %s\n", s.LastError)
		}
	} else {
		fmt.Println("Daemon:   not running yet (brew services start pokerland-tracker)")
	}

	store, err := state.Load(filepath.Join(dataDir, "state.json"))
	if err != nil {
		return err
	}
	fmt.Printf("Files:    %d tracked\n", len(store.Files))
	for path, file := range store.Files {
		fmt.Printf("  %s  acked %d bytes\n", filepath.Base(path), file.AckedOffset)
	}
	return nil
}

func ago(t time.Time) string {
	if t.IsZero() {
		return "never"
	}
	return fmt.Sprintf("%s ago", time.Since(t).Round(time.Second))
}

func doctor(dataDir string) error {
	ok := true
	check := func(good bool, label, fix string) {
		if good {
			fmt.Printf("ok    %s\n", label)
		} else {
			ok = false
			fmt.Printf("FAIL  %s\n      %s\n", label, fix)
		}
	}
	cfg, err := config.Load(dataDir)
	check(err == nil, "config readable at "+config.Path(dataDir), fmt.Sprint(err))
	if err != nil {
		return errors.New("fix the config first")
	}
	check(cfg.Token != "", "client token saved", "run `pokerland-tracker login`")
	watched := roots(cfg, nil)
	check(len(watched) > 0, fmt.Sprintf("hand-history folders found: %v", watched),
		"PokerStars has not written any hands yet, or saves them elsewhere; add the folder to extra_roots in config.json")
	for _, root := range watched {
		entries, err := os.ReadDir(root)
		check(err == nil, "can read "+root, fmt.Sprint(err))
		if err == nil && len(entries) == 0 {
			fmt.Printf("note  %s is empty\n", root)
		}
	}
	if cfg.Token != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		username, err := api.New(cfg.APIBaseURL, cfg.Token, userAgent()).Me(ctx)
		check(err == nil, fmt.Sprintf("server %s accepts the token (user %s)", cfg.APIBaseURL, username), fmt.Sprint(err))
	}
	_, err = os.Stat(filepath.Join(dataDir, "status.json"))
	check(err == nil, "daemon has run", "start it with `brew services start pokerland-tracker`")
	if !ok {
		return errors.New("some checks failed")
	}
	return nil
}
