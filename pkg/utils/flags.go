//go:build windows
// +build windows

package utils

import (
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

// PatchWindowsArgs re-parses the raw Windows command line so that
// os.Args exactly matches what the user typed, including paths with spaces.
//
// Must be called before flag.Parse() or any other os.Args usage in main().
func PatchWindowsArgs() {
	cmdLinePtr := windows.GetCommandLine()
	if cmdLinePtr == nil {
		return
	}
	var argc int32
	argvPtr, err := windows.CommandLineToArgv(cmdLinePtr, &argc)
	if err != nil || argvPtr == nil || argc < 1 {
		return
	}
	defer windows.LocalFree(windows.Handle(uintptr(unsafe.Pointer(argvPtr))))

	// Convert the argv pointer into a slice of *uint16
	argvSlice := unsafe.Slice((**uint16)(unsafe.Pointer(argvPtr)), argc)

	newArgs := make([]string, 0, argc)
	for _, p := range argvSlice {
		if p != nil {
			newArgs = append(newArgs, windows.UTF16PtrToString(p))
		}
	}
	os.Args = newArgs
}
