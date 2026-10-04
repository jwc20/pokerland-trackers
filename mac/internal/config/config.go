// Package config is the user's settings: API URL, client token and extra roots.
package config

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
)

const DefaultAPIBaseURL = "https://api.pokerland.app"

type Config struct {
	APIBaseURL string `json:"api_base_url"`
	// The client token from the web app's Settings page. It can only upload hand
	// histories, so it is kept in a 0600 file rather than the Keychain, which
	// would prompt after every Homebrew upgrade (a new binary each time).
	Token string `json:"token"`
	// Hand-history directories to watch in addition to the detected defaults.
	ExtraRoots []string `json:"extra_roots,omitempty"`
}

func Path(dataDir string) string { return filepath.Join(dataDir, "config.json") }

func Load(dataDir string) (*Config, error) {
	cfg := &Config{APIBaseURL: DefaultAPIBaseURL}
	data, err := os.ReadFile(Path(dataDir))
	if errors.Is(err, os.ErrNotExist) {
		return cfg, nil
	}
	if err != nil {
		return nil, err
	}
	if err := json.Unmarshal(data, cfg); err != nil {
		return nil, err
	}
	if cfg.APIBaseURL == "" {
		cfg.APIBaseURL = DefaultAPIBaseURL
	}
	return cfg, nil
}

func (c *Config) Save(dataDir string) error {
	if err := os.MkdirAll(dataDir, 0o700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	tmp := Path(dataDir) + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, Path(dataDir))
}
