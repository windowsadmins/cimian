// pkg/extract/exe.go

package extract

import (
	"fmt"
	"runtime"
	"strings"
	"syscall"
	"unsafe"
)

// #include <windows.h>
// #include <stdio.h>
import "C"

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

func ExeMetadata(exePath string) (prodName, productVer, company, fileDesc string) {
	// For non-Windows, just return empty:
	if runtime.GOOS != "windows" {
		return "", "", "", ""
	}
	// 1) get size
	size, err := getFileVersionInfoSize(exePath)
	if err != nil || size == 0 {
		return "", "", "", ""
	}

	// 2) get version info
	info, err := getFileVersionInfo(exePath, size)
	if err != nil {
		return "", "", "", ""
	}

	// 3) query the root \ for VS_FIXEDFILEINFO
	fixedInfoPtr, fixedInfoLen, err := verQueryValue(info, `\`)
	if err != nil || fixedInfoLen == 0 {
		return "", "", "", ""
	}
	fixedInfo := (*VSFixedFileInfo)(fixedInfoPtr)

	// parse out version
	major := fixedInfo.FileVersionMS >> 16
	minor := fixedInfo.FileVersionMS & 0xffff
	build := fixedInfo.FileVersionLS >> 16
	revision := fixedInfo.FileVersionLS & 0xffff
	productVer = fmt.Sprintf("%d.%d.%d.%d", major, minor, build, revision)

	// 4) fetch the language code \VarFileInfo\Translation
	langPtr, langLen, err := verQueryValue(info, `\VarFileInfo\Translation`)
	if err != nil || langLen == 0 {
		return "", productVer, "", ""
	}

	type langAndCodePage struct {
		Language uint16
		CodePage uint16
	}

	langData := (*langAndCodePage)(langPtr)
	language := fmt.Sprintf("%04x", langData.Language)
	codepage := fmt.Sprintf("%04x", langData.CodePage)

	// 5) helper
	queryString := func(name string) string {
		subBlock := fmt.Sprintf(`\StringFileInfo\%s%s\%s`, language, codepage, name)
		valPtr, valLen, err := verQueryValue(info, subBlock)
		if err != nil || valLen == 0 {
			return ""
		}
		return syscall.UTF16ToString((*[1 << 20]uint16)(valPtr)[:valLen])
	}

	company = strings.TrimSpace(queryString("CompanyName"))
	prodName = strings.TrimSpace(queryString("ProductName"))
	fileDesc = strings.TrimSpace(queryString("FileDescription"))

	return prodName, productVer, company, fileDesc
}

// helper for size
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

// helper for info
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

// helper for querying values
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
