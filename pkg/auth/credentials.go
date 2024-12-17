// pkg/auth/credentials.go

package auth

import (
	"encoding/base64"
	"errors"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows/registry"
)

const (
	registryPath   = `SOFTWARE\Gorilla`
	authHeaderName = "AuthHeader"
)

type dataBlob struct {
	cbData uint32
	pbData *byte
}

var (
	crypt32                = syscall.NewLazyDLL("crypt32.dll")
	procCryptUnprotectData = crypt32.NewProc("CryptUnprotectData")
	kernel32               = syscall.NewLazyDLL("kernel32.dll")
	procLocalFree          = kernel32.NewProc("LocalFree")
)

func localFree(h syscall.Handle) error {
	r0, _, e1 := syscall.Syscall(procLocalFree.Addr(), 1, uintptr(h), 0, 0)
	if r0 != 0 {
		return e1
	}
	return nil
}

func decryptDPAPI(data []byte) ([]byte, error) {
	inBlob := dataBlob{
		cbData: uint32(len(data)),
		pbData: &data[0],
	}
	var outBlob dataBlob

	r, _, err := procCryptUnprotectData.Call(
		uintptr(unsafe.Pointer(&inBlob)),
		0, 0, 0, 0, 0,
		uintptr(unsafe.Pointer(&outBlob)),
	)
	if r == 0 {
		return nil, err
	}
	defer localFree(syscall.Handle(uintptr(unsafe.Pointer(outBlob.pbData))))

	decrypted := make([]byte, outBlob.cbData)
	copy(decrypted, (*[1 << 30]byte)(unsafe.Pointer(outBlob.pbData))[:outBlob.cbData])
	return decrypted, nil
}

// GetAuthHeader retrieves and decrypts the AuthHeader from the registry.
func GetAuthHeader() (string, error) {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, registryPath, registry.QUERY_VALUE)
	if err != nil {
		return "", err
	}
	defer k.Close()

	val, _, err := k.GetStringValue(authHeaderName)
	if err != nil {
		return "", errors.New("no Authorization header found in registry")
	}

	encryptedData, err := base64.StdEncoding.DecodeString(val)
	if err != nil {
		return "", errors.New("failed to decode AuthHeader from base64")
	}

	decryptedData, err := decryptDPAPI(encryptedData)
	if err != nil {
		return "", errors.New("failed to decrypt AuthHeader with DPAPI")
	}

	header := trimHeader(string(decryptedData))
	if header == "" {
		return "", errors.New("authorization header is empty after decrypt")
	}

	return header, nil
}

func trimHeader(val string) string {
	val = removeNulls(val)
	val = strings.TrimSpace(val)
	val = removeAuthorizationPrefix(val)
	val = removeCRLF(val)
	val = strings.TrimSpace(val)
	return val
}

func removeNulls(s string) string {
	// Just in case, but typically strings.ReplaceAll(s, "\x00", "") is enough.
	return strings.ReplaceAll(s, "\x00", "")
}

func removeCRLF(s string) string {
	s = strings.ReplaceAll(s, "\r", "")
	s = strings.ReplaceAll(s, "\n", "")
	return s
}

func removeAuthorizationPrefix(val string) string {
	lowerVal := strings.ToLower(val)
	if strings.HasPrefix(lowerVal, "authorization:") {
		parts := strings.SplitN(val, ":", 2)
		if len(parts) == 2 {
			return strings.TrimSpace(parts[1])
		}
		return ""
	}
	return val
}
