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
// It sanitizes the value by removing null characters and does not log the raw credential.
// Even at high verbosity or debug, we do not print the plaintext Basic token.
func GetAuthHeader() (string, error) {
	cred, err := wincred.GetGenericCredential(additionalHeadersTarget)
	if err != nil {
		return "", err
	}
	if cred == nil || len(cred.CredentialBlob) == 0 {
		return "", errors.New("no Authorization header credential found")
	}

	// Convert CredentialBlob to string
	val := string(cred.CredentialBlob)

	// Remove any embedded null characters (sometimes appear as wide-char encoding)
	val = strings.ReplaceAll(val, "\x00", "")

	// Trim whitespace
	val = strings.TrimSpace(val)

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

	// Remove any \r or \n just to be safe
	val = strings.ReplaceAll(val, "\r", "")
	val = strings.ReplaceAll(val, "\n", "")
	val = strings.TrimSpace(val)

	if val == "" {
		return "", errors.New("authorization header is empty")
	}

	// Return the sanitized header value without logging its content.
	return val, nil
}
