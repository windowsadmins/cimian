package auth

import (
    "errors"
    "strings"

    "github.com/danieljoos/wincred"
)

const (
    additionalHeadersTarget = "Gorilla_AdditionalHttpHeaders"
)

// GetAuthHeader retrieves the Authorization header from Windows Credential Manager.
func GetAuthHeader() (string, error) {
    cred, err := wincred.GetGenericCredential(additionalHeadersTarget)
    if err != nil {
        return "", err
    }
    if cred == nil || len(cred.CredentialBlob) == 0 {
        return "", errors.New("no Authorization header credential found")
    }
    val := strings.TrimSpace(string(cred.CredentialBlob))
    // If the stored value includes "Authorization:" prefix, remove it.
    lowerVal := strings.ToLower(val)
    if strings.HasPrefix(lowerVal, "authorization:") {
        parts := strings.SplitN(val, ":", 2)
        if len(parts) == 2 {
            val = strings.TrimSpace(parts[1])
        } else {
            return "", errors.New("invalid authorization header format")
        }
    }
    if val == "" {
        return "", errors.New("authorization header is empty")
    }
    return val, nil
}