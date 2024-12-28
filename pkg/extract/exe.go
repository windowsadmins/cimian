package extract

import (
	"fmt"
	"runtime"
	"syscall"
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
