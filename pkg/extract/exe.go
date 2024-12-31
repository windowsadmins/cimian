// pkg/extract/exe.go

//go:build windows
// +build windows

package extract

import (
	"fmt"
	"runtime"
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

var (
	versionDLL                  = syscall.MustLoadDLL("version.dll")
	procGetFileVersionInfoSizeW = versionDLL.MustFindProc("GetFileVersionInfoSizeW")
	procGetFileVersionInfoW     = versionDLL.MustFindProc("GetFileVersionInfoW")
	procVerQueryValueW          = versionDLL.MustFindProc("VerQueryValueW")
)

type VSFixedFileInfo struct {
	Signature        uint32
	StrucVersion     uint32
	FileVersionMS    uint32
	FileVersionLS    uint32
	ProductVersionMS uint32
	ProductVersionLS uint32
	FileFlagsMask    uint32
	FileFlags        uint32
	FileOS           uint32
	FileType         uint32
	FileSubtype      uint32
	FileDateMS       uint32
	FileDateLS       uint32
}

type FileMetadata struct {
	ProductName   string
	VersionString string
	VersionMajor  int
	VersionMinor  int
	VersionPatch  int
	VersionBuild  int
}

func ExeMetadata(exePath string) (string, error) {
	if runtime.GOOS != "windows" {
		return "", nil
	}
	size, err := getFileVersionInfoSize(exePath)
	if err != nil || size == 0 {
		return "", nil
	}
	info, err := getFileVersionInfo(exePath, size)
	if err != nil {
		return "", nil
	}
	fixedInfoPtr, fixedInfoLen, err := verQueryValue(info, `\`)
	if err != nil || fixedInfoLen == 0 {
		return "", nil
	}
	fixedInfo := (*VSFixedFileInfo)(fixedInfoPtr)

	major := fixedInfo.FileVersionMS >> 16
	minor := fixedInfo.FileVersionMS & 0xffff
	build := fixedInfo.FileVersionLS >> 16
	revision := fixedInfo.FileVersionLS & 0xffff

	version := fmt.Sprintf("%d.%d.%d.%d", major, minor, build, revision)
	return version, nil
}

func GetFileMetadata(path string) FileMetadata {
	version, _ := ExeMetadata(path)
	return FileMetadata{
		VersionString: version,
	}
}

func getFileVersionInfoSize(filename string) (uint32, error) {
	p, err := syscall.UTF16PtrFromString(filename)
	if err != nil {
		return 0, err
	}
	r0, _, e1 := syscall.Syscall(procGetFileVersionInfoSizeW.Addr(), 2,
		uintptr(unsafe.Pointer(p)), 0, 0)
	size := uint32(r0)
	if size == 0 {
		if e1 != 0 {
			return 0, error(e1)
		}
		return 0, fmt.Errorf("GetFileVersionInfoSizeW failed for %s", filename)
	}
	return size, nil
}

func getFileVersionInfo(filename string, size uint32) ([]byte, error) {
	info := make([]byte, size)
	p, err := syscall.UTF16PtrFromString(filename)
	if err != nil {
		return nil, err
	}
	r0, _, e1 := syscall.Syscall6(procGetFileVersionInfoW.Addr(), 4,
		uintptr(unsafe.Pointer(p)),
		0,
		uintptr(size),
		uintptr(unsafe.Pointer(&info[0])),
		0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return nil, error(e1)
		}
		return nil, fmt.Errorf("GetFileVersionInfoW failed for %s", filename)
	}
	return info, nil
}

func verQueryValue(block []byte, subBlock string) (unsafe.Pointer, uint32, error) {
	pSubBlock, err := syscall.UTF16PtrFromString(subBlock)
	if err != nil {
		return nil, 0, err
	}
	var buf unsafe.Pointer
	var size uint32
	r0, _, e1 := syscall.Syscall6(procVerQueryValueW.Addr(), 4,
		uintptr(unsafe.Pointer(&block[0])),
		uintptr(unsafe.Pointer(pSubBlock)),
		uintptr(unsafe.Pointer(&buf)),
		uintptr(unsafe.Pointer(&size)),
		0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return nil, 0, error(e1)
		}
		return nil, 0, fmt.Errorf("VerQueryValueW failed for subBlock %s", subBlock)
	}
	return buf, size, nil
}

// ExtractExeDeveloper attempts to retrieve the CompanyName
// from StringFileInfo in the .exe version resource.
func ExtractExeDeveloper(exePath string) (string, error) {
	if runtime.GOOS != "windows" {
		return "", nil
	}

	size, err := getFileVersionInfoSize(exePath)
	if err != nil || size == 0 {
		return "", err
	}

	info, err := getFileVersionInfo(exePath, size)
	if err != nil {
		return "", err
	}

	// Grab the translation array
	type translation struct {
		Language uint16
		CodePage uint16
	}

	ptr, blockSize, err := verQueryValue(info, `\VarFileInfo\Translation`)
	if err != nil || blockSize == 0 {
		// fallback to default 0409/04B0 if no translation found
		devName, _ := queryCompanyName(info, 0x0409, 0x04B0)
		devName = strings.TrimSpace(devName)
		devName = strings.Trim(devName, "'")
		// Capitalize first letter, lowercase the rest
		if len(devName) > 1 {
			devName = strings.ToUpper(devName[:1]) + strings.ToLower(devName[1:])
		} else if len(devName) == 1 {
			devName = strings.ToUpper(devName)
		}
		return devName, nil
	}

	// parse the bytes into an array of translations
	numTranslations := blockSize / 4
	transSlice := (*[1 << 28]translation)(ptr)[:numTranslations:numTranslations]

	// Try each translation
	for _, t := range transSlice {
		if devName, _ := queryCompanyName(info, t.Language, t.CodePage); devName != "" {
			devName = strings.TrimSpace(devName)
			devName = strings.Trim(devName, "'")
			// Capitalize first letter, lowercase the rest
			if len(devName) > 1 {
				devName = strings.ToUpper(devName[:1]) + strings.ToLower(devName[1:])
			} else if len(devName) == 1 {
				devName = strings.ToUpper(devName)
			}
			return devName, nil
		}
	}

	// final fallback
	devName, _ := queryCompanyName(info, 0x0409, 0x04B0)
	devName = strings.TrimSpace(devName)
	devName = strings.Trim(devName, "'")
	if len(devName) > 1 {
		devName = strings.ToUpper(devName[:1]) + strings.ToLower(devName[1:])
	} else if len(devName) == 1 {
		devName = strings.ToUpper(devName)
	}
	return devName, nil
}

// queryCompanyName attempts to query CompanyName for a given Language/CodePage
func queryCompanyName(block []byte, lang, codepage uint16) (string, error) {
	subBlock := fmt.Sprintf(`\StringFileInfo\%04x%04x\CompanyName`, lang, codepage)
	ptr, size, err := verQueryValue(block, subBlock)
	if err != nil || size == 0 {
		return "", err
	}
	// ptr is a pointer to a UTF-16 string
	raw := unsafe.Slice((*uint16)(ptr), size)
	return utf16PtrToString(raw), nil
}

// utf16PtrToString converts a raw UTF-16 slice to Go string
func utf16PtrToString(u16 []uint16) string {
	// find null terminator
	n := 0
	for n < len(u16) && u16[n] != 0 {
		n++
	}
	return string(utf16.Decode(u16[:n]))
}
