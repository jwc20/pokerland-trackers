package follower

import (
	"bytes"
	"crypto/sha1"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
)

// StreamNamespace is the UUID v5 namespace for stream ids (protocol/PROTOCOL.md).
var StreamNamespace = [16]byte{0x6f, 0x1e, 0x7c, 0x1e, 0x5a, 0x0b, 0x4d, 0x3e, 0x9b, 0x1a, 0x2f, 0x3c, 0x4d, 0x5e, 0x6f, 0x70}

// fingerprintWindow is how far into a file the first newline may be.
const fingerprintWindow = 4096

// Fingerprint hashes a file's first line, newline included. It returns "" when
// the file has no complete first line yet.
func Fingerprint(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	head := make([]byte, fingerprintWindow)
	n, err := io.ReadFull(f, head)
	if err != nil && err != io.ErrUnexpectedEOF && err != io.EOF {
		return "", err
	}
	end := bytes.IndexByte(head[:n], '\n')
	if end < 0 {
		return "", nil
	}
	sum := sha256.Sum256(head[:end+1])
	return hex.EncodeToString(sum[:]), nil
}

// StreamID derives the deterministic stream id (UUID v5) from a fingerprint.
func StreamID(fingerprint string) string {
	h := sha1.New()
	h.Write(StreamNamespace[:])
	h.Write([]byte(fingerprint))
	sum := h.Sum(nil)
	sum[6] = (sum[6] & 0x0f) | 0x50 // version 5
	sum[8] = (sum[8] & 0x3f) | 0x80 // RFC 4122 variant
	return fmt.Sprintf("%x-%x-%x-%x-%x", sum[0:4], sum[4:6], sum[6:8], sum[8:10], sum[10:16])
}
