// pkg/utils/reporter.go - Status reporting interface moved to utils to break circular dependency

package utils

import (
	"context"
)

// Reporter interface abstracts the status reporting functionality
// Moved to utils package to break circular dependency between download <-> status
type Reporter interface {
	Start(ctx context.Context) error
	Message(txt string)
	Detail(txt string)
	Percent(pct int) // -1 = indeterminate
	ShowLog(path string)
	Error(err error)
	Stop()
}

// NoOpReporter implements Reporter but does nothing (for headless operation)
type NoOpReporter struct{}

func NewNoOpReporter() Reporter {
	return &NoOpReporter{}
}

func (r *NoOpReporter) Start(ctx context.Context) error { return nil }
func (r *NoOpReporter) Message(txt string)              {}
func (r *NoOpReporter) Detail(txt string)               {}
func (r *NoOpReporter) Percent(pct int)                 {}
func (r *NoOpReporter) ShowLog(path string)             {}
func (r *NoOpReporter) Error(err error)                 {}
func (r *NoOpReporter) Stop()                           {}
