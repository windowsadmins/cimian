//go:build windows
// +build windows

package main

import (
    "flag"
    "fmt"
    "golang.org/x/sys/windows"
    "github.com/rodchristiansen/gorilla/pkg/logging"
)

// adminCheck checks for admin privileges (used in main.go).
func adminCheck() (bool, error) {
    // Skip the check during tests.
    if flag.Lookup("test.v") != nil {
        return false, nil
    }

    var adminSid *windows.SID

    // Create a SID for the administrator group.
    adminSid, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid, nil)
    if err != nil {
        return false, err
    }

    // Check if the current user belongs to the administrator group.
    token := windows.Token(0)
    isAdmin, err := token.IsMember(adminSid)
    if err != nil {
        return false, err
    }

    return isAdmin, nil
}
