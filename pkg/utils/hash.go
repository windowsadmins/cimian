// pkg/utils/hash.go - utility functions for hashing files.

package utils

import (
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"os"
	"strings"

	"github.com/windowsadmins/cimian/pkg/logging"
)

// FileMD5 returns the MD5 sum of a file.
func FileMD5(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()

	h := md5.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// FileSHA256 returns the SHA256 sum of a file.
func FileSHA256(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()

	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// Verify checks if a file's hash matches the expected hash
func Verify(file string, expectedHash string) bool {
	actualHash := CalculateHash(file)
	return strings.EqualFold(actualHash, expectedHash)
}

// CalculateHash returns SHA256 hash of file contents
func CalculateHash(path string) string {
	file, err := os.Open(path)
	if err != nil {
		logging.Error("Failed to open file for hash calculation", "path", path, "error", err)
		return ""
	}
	defer file.Close()

	// Try SHA256 first
	sha256Hash := sha256.New()
	if _, err := io.Copy(sha256Hash, file); err != nil {
		logging.Error("Failed to calculate SHA256 hash", "path", path, "error", err)
		return ""
	}

	sha256Sum := hex.EncodeToString(sha256Hash.Sum(nil))
	logging.Debug("Calculated SHA256 hash", "path", path, "hash", sha256Sum)

	// Return SHA256 by default
	return sha256Sum
}
