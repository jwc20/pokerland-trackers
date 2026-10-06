// Package api is the HTTP client for pokerland-api's tracker endpoints.
// See protocol/PROTOCOL.md.
package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Config is what GET /api/tracker/config returns.
type Config struct {
	MinVersion           string `json:"min_version"`
	PollIntervalSeconds  int    `json:"poll_interval_seconds"`
	FlushIntervalSeconds int    `json:"flush_interval_seconds"`
	FlushBytes           int64  `json:"flush_bytes"`
	MaxReadBytes         int64  `json:"max_read_bytes"`
	MaxChunkBytes        int64  `json:"max_chunk_bytes"`
}

// DefaultConfig is used until the first successful config fetch.
var DefaultConfig = Config{
	MinVersion:           "0.0.0",
	PollIntervalSeconds:  2,
	FlushIntervalSeconds: 10,
	FlushBytes:           256 * 1024,
	MaxReadBytes:         1024 * 1024,
	MaxChunkBytes:        4 * 1024 * 1024,
}

// Registration is the body of PUT /api/tracker/streams/{id}.
type Registration struct {
	Source        string `json:"source"`
	Platform      string `json:"platform"`
	ClientVersion string `json:"client_version"`
	PathHint      string `json:"path_hint"`
	Fingerprint   string `json:"fingerprint"`
}

// StatusError is a non-2xx reply. AckedOffset is set when the server included
// one (a 409), MinVersion with a 426.
type StatusError struct {
	Status      int
	Detail      string
	AckedOffset *int64
	MinVersion  string
}

func (e *StatusError) Error() string {
	if e.Detail != "" {
		return fmt.Sprintf("server replied %d: %s", e.Status, e.Detail)
	}
	return fmt.Sprintf("server replied %d", e.Status)
}

// Retryable reports whether the failure is the server's or the network's and
// worth retrying with back-off, as opposed to something the tracker must fix.
func Retryable(err error) bool {
	var status *StatusError
	if errors.As(err, &status) {
		return status.Status == http.StatusTooManyRequests || status.Status >= 500
	}
	return err != nil // a network error
}

type Client struct {
	BaseURL   string
	Token     string
	UserAgent string
	HTTP      *http.Client
}

func New(baseURL, token, userAgent string) *Client {
	return &Client{
		BaseURL:   strings.TrimRight(baseURL, "/"),
		Token:     token,
		UserAgent: userAgent,
		HTTP:      &http.Client{Timeout: 60 * time.Second},
	}
}

func (c *Client) Me(ctx context.Context) (string, error) {
	var reply struct {
		Username string `json:"username"`
	}
	err := c.do(ctx, http.MethodGet, "/api/tracker/me/", nil, "", nil, &reply)
	return reply.Username, err
}

func (c *Client) Config(ctx context.Context) (Config, error) {
	config := DefaultConfig
	err := c.do(ctx, http.MethodGet, "/api/tracker/config/", nil, "", nil, &config)
	return config, err
}

// RegisterStream creates or looks up a stream and returns the server's acked offset.
func (c *Client) RegisterStream(ctx context.Context, streamID string, reg Registration) (int64, error) {
	body, err := json.Marshal(reg)
	if err != nil {
		return 0, err
	}
	var reply struct {
		AckedOffset int64 `json:"acked_offset"`
	}
	err = c.do(ctx, http.MethodPut, "/api/tracker/streams/"+streamID+"/", body, "application/json", nil, &reply)
	return reply.AckedOffset, err
}

// UploadChunk sends the gzipped bytes [start, end) and returns the new acked offset.
func (c *Client) UploadChunk(ctx context.Context, streamID string, start, end int64, sha256Hex string, gz []byte) (int64, error) {
	var reply struct {
		AckedOffset int64 `json:"acked_offset"`
	}
	headers := map[string]string{
		"X-Chunk-End":    strconv.FormatInt(end, 10),
		"X-Chunk-Sha256": sha256Hex,
	}
	path := fmt.Sprintf("/api/tracker/streams/%s/chunks/%d/", streamID, start)
	err := c.do(ctx, http.MethodPut, path, gz, "application/gzip", headers, &reply)
	return reply.AckedOffset, err
}

func (c *Client) do(ctx context.Context, method, path string, body []byte, contentType string, headers map[string]string, reply any) error {
	var reader io.Reader
	if body != nil {
		reader = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, c.BaseURL+path, reader)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Token "+c.Token)
	req.Header.Set("User-Agent", c.UserAgent)
	req.Header.Set("Accept", "application/json")
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	for name, value := range headers {
		req.Header.Set(name, value)
	}

	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		status := &StatusError{Status: resp.StatusCode}
		var detail struct {
			Detail      string `json:"detail"`
			AckedOffset *int64 `json:"acked_offset"`
			MinVersion  string `json:"min_version"`
		}
		if json.Unmarshal(data, &detail) == nil {
			status.Detail = detail.Detail
			status.AckedOffset = detail.AckedOffset
			status.MinVersion = detail.MinVersion
		}
		return status
	}
	if reply != nil && len(data) > 0 {
		return json.Unmarshal(data, reply)
	}
	return nil
}
