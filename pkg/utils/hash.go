// pkg/utils/hash.go

package utils

import (
    "crypto/md5"
    "crypto/sha256"
    "encoding/hex"
    "io"
    "os"
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