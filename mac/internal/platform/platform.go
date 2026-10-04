// Package platform knows where things live on each OS and how to talk to the user.
package platform

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// Name is the platform string sent to the server.
func Name() string {
	switch runtime.GOOS {
	case "darwin":
		return "macos"
	default:
		return runtime.GOOS
	}
}

// DataDir holds config.json, state.json and status.json.
func DataDir() string {
	if dir := os.Getenv("POKERLAND_TRACKER_HOME"); dir != "" {
		return dir
	}
	home, _ := os.UserHomeDir()
	switch runtime.GOOS {
	case "darwin":
		return filepath.Join(home, "Library", "Application Support", "Pokerland")
	default:
		if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" {
			return filepath.Join(xdg, "pokerland")
		}
		return filepath.Join(home, ".config", "pokerland")
	}
}

// LogDir is where the launchd service's output goes.
func LogDir() string {
	home, _ := os.UserHomeDir()
	if runtime.GOOS == "darwin" {
		return filepath.Join(home, "Library", "Logs", "Pokerland")
	}
	return filepath.Join(DataDir(), "logs")
}

// DefaultRoots are the PokerStars hand-history directories that exist on this
// machine, including regional builds such as PokerStars.EU.
func DefaultRoots() []string {
	home, _ := os.UserHomeDir()
	var patterns []string
	switch runtime.GOOS {
	case "darwin":
		patterns = []string{filepath.Join(home, "Library", "Application Support", "PokerStars*", "HandHistory")}
	case "windows":
		patterns = []string{
			filepath.Join(os.Getenv("LOCALAPPDATA"), "PokerStars*", "HandHistory"),
			filepath.Join(os.Getenv("APPDATA"), "PokerStars*", "HandHistory"),
		}
	default:
		// Wine / Proton installs.
		patterns = []string{filepath.Join(home, ".wine", "drive_c", "users", "*", "AppData", "Local", "PokerStars*", "HandHistory")}
	}
	var roots []string
	for _, pattern := range patterns {
		matches, _ := filepath.Glob(pattern)
		for _, match := range matches {
			if info, err := os.Stat(match); err == nil && info.IsDir() {
				roots = append(roots, match)
			}
		}
	}
	return roots
}

// Notify shows a desktop notification where the OS supports it, else logs to stderr.
func Notify(title, message string) {
	if runtime.GOOS == "darwin" {
		script := fmt.Sprintf("display notification %q with title %q", message, title)
		if exec.Command("osascript", "-e", script).Run() == nil {
			return
		}
	}
	fmt.Fprintf(os.Stderr, "%s: %s\n", title, message)
}

// ReadSecret reads a line from the terminal without echoing it.
func ReadSecret(prompt string) (string, error) {
	fmt.Fprint(os.Stderr, prompt)
	echoOff := exec.Command("stty", "-echo")
	echoOff.Stdin = os.Stdin
	hidden := echoOff.Run() == nil
	defer func() {
		if hidden {
			echoOn := exec.Command("stty", "echo")
			echoOn.Stdin = os.Stdin
			_ = echoOn.Run()
			fmt.Fprintln(os.Stderr)
		}
	}()
	var line string
	if _, err := fmt.Fscanln(os.Stdin, &line); err != nil {
		return "", err
	}
	return strings.TrimSpace(line), nil
}
