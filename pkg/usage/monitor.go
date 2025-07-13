// Package usage collects launch→quit sessions for *all* application processes
// on Windows clients. It runs in‑process inside managedsoftwareupdate.exe.
//
// Behaviour summary
// -----------------
// • Every <interval> (default 60 s) we enumerate *all* running processes.
// • For any process we haven’t seen before we record a start timestamp.
// • When a process disappears we record an end timestamp and duration.
// • Completed sessions are kept in‑memory until Drain() and mirrored to
//   app_usage_YYYY‑MM‑DD.jsonl in the Cimian log directory so external
//   shippers (ELK, Log Analytics) pick them up immediately.
// • An optional **ignore list** lets you skip system services like
//   System, Idle, wininit.exe, etc.
//
// NOTE: There is *no* allow‑list anymore; by default everything is tracked.
// If Admins set cfg.UsageMonitor.Ignore these names are skipped (case‑insensitive).

package usage

import (
	"bufio"
	"context"
	"encoding/json"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/shirou/gopsutil/v3/process"
	"golang.org/x/sys/windows"
)

// Session represents one contiguous run of an application by a user.
// Example JSONL record:
// {"exe":"chrome.exe","path":"C:/Program Files/Google/Chrome/Application/chrome.exe","user":"ECUAD\\jdoe","started":"2025‑04‑25T13:01:07‑07:00","ended":"2025‑04‑25T14:38:55‑07:00","duration_seconds":5868}

type Session struct {
	Exe             string    `json:"exe"`
	Path            string    `json:"path"`
	User            string    `json:"user"`
	Started         time.Time `json:"started"`
	Ended           time.Time `json:"ended"`
	DurationSeconds int64     `json:"duration_seconds"`
}

type tracker struct {
	mu       sync.Mutex
	active   map[int32]*Session // pid → open session
	finished []*Session
	ignore   map[string]struct{} // exe names to skip (lower‑case)
	outDir   string
}

func newTracker(outDir string, ignore []string) *tracker {
	t := &tracker{
		active: make(map[int32]*Session),
		ignore: make(map[string]struct{}),
		outDir: outDir,
	}
	for _, ex := range ignore {
		if ex = strings.ToLower(strings.TrimSpace(ex)); ex != "" {
			t.ignore[ex] = struct{}{}
		}
	}
	return t
}

// ---------------- Public API ----------------

var (
	global *tracker
	once   sync.Once
)

// Start launches the background collector. Calling it multiple times is safe; only
// the first call activates the goroutine.
//
//	interval   – sampling frequency (e.g. 1 min)
//	outDir     – directory for jsonl mirror ("" = ProgramData\ManagedInstalls\logs)
//	ignoreList – exe names to *exclude* (case‑insensitive)
func Start(ctx context.Context, interval time.Duration, outDir string, ignoreList []string) {
	once.Do(func() {
		if outDir == "" {
			outDir = filepath.Join(os.Getenv("ProgramData"), "ManagedInstalls", "Logs")
		}
		global = newTracker(outDir, ignoreList)
		go global.run(ctx, interval)
		log.Printf("usage‑monitor: tracking ALL apps (interval=%s, outDir=%s)", interval, outDir)
	})
}

// Drain returns any finished sessions since the last call and clears them.
func Drain() []Session {
	if global == nil {
		return nil
	}
	global.mu.Lock()
	defer global.mu.Unlock()
	out := global.finished
	global.finished = nil
	result := make([]Session, len(out))
	for i, s := range out {
		result[i] = *s
	}
	return result
}

// ---------------- Internal logic ----------------

func (t *tracker) run(ctx context.Context, interval time.Duration) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		if err := t.sample(); err != nil {
			log.Printf("usage‑monitor: %v", err)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (t *tracker) sample() error {
	now := time.Now()
	procs, err := process.Processes()
	if err != nil {
		return err
	}
	seen := make(map[int32]struct{})
	for _, p := range procs {
		exe, err := p.Name()
		if err != nil || exe == "" {
			continue
		}
		exeLower := strings.ToLower(exe)
		if _, skip := t.ignore[exeLower]; skip {
			continue
		}
		seen[p.Pid] = struct{}{}
		if _, tracked := t.active[p.Pid]; !tracked {
			path, _ := p.Exe()
			user := resolveUsername(p)
			t.begin(p.Pid, user, exe, path, now)
		}
	}
	// detect quits
	for pid := range t.active {
		if _, still := seen[pid]; !still {
			t.end(pid, now)
		}
	}
	return t.flushToFile()
}

func (t *tracker) begin(pid int32, user, exe, path string, ts time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.active[pid] = &Session{Exe: exe, Path: path, User: user, Started: ts}
}

func (t *tracker) end(pid int32, ts time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if s, ok := t.active[pid]; ok {
		s.Ended = ts
		s.DurationSeconds = int64(s.Ended.Sub(s.Started).Seconds())
		t.finished = append(t.finished, s)
		delete(t.active, pid)
	}
}

func (t *tracker) flushToFile() error {
	t.mu.Lock()
	finished := append([]*Session(nil), t.finished...) // copy
	t.mu.Unlock()
	if len(finished) == 0 {
		return nil
	}
	if err := os.MkdirAll(t.outDir, 0o755); err != nil {
		return err
	}
	fname := filepath.Join(t.outDir, "app_usage_"+time.Now().Format("2006-01-02")+".jsonl")
	f, err := os.OpenFile(fname, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	w := bufio.NewWriter(f)
	enc := json.NewEncoder(w)
	for _, s := range finished {
		if err := enc.Encode(s); err != nil {
			log.Printf("usage‑monitor encode: %v", err)
		}
	}
	return w.Flush()
}

// resolveUsername converts the process token to DOMAIN\user (best effort).
func resolveUsername(p *process.Process) string {
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(p.Pid))
	if err != nil {
		return ""
	}
	defer windows.CloseHandle(h)
	var token windows.Token
	if err := windows.OpenProcessToken(h, windows.TOKEN_QUERY, &token); err != nil {
		return ""
	}
	defer token.Close()
	user, err := token.GetTokenUser()
	if err != nil {
		return ""
	}
	acc, dom, _, err := user.User.Sid.LookupAccount("")
	if err != nil {
		return ""
	}
	return dom + "\\" + acc
}
