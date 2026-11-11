package main

import (
	"context"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"time"
)

var validMarker = regexp.MustCompile(`^[A-Za-z0-9_-]{1,128}$`)

func main() {
	password := os.Getenv("DB_PASSWORD")
	if password == "" {
		log.Fatal("DB_PASSWORD is required")
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		value, err := query(r.Context(), password, "SELECT 1;")
		if err != nil || value != "1" {
			http.Error(w, "database unavailable", http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
	mux.HandleFunc("PUT /marker", func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 128))
		if err != nil || !validMarker.Match(body) {
			http.Error(w, "invalid marker", http.StatusBadRequest)
			return
		}
		_, err = query(r.Context(), password, `
BEGIN;
SELECT pg_advisory_xact_lock(40362);
CREATE TABLE IF NOT EXISTS acceptance_marker (id integer PRIMARY KEY, value text NOT NULL);
INSERT INTO acceptance_marker VALUES (1, :'marker')
    ON CONFLICT (id) DO UPDATE SET value = EXCLUDED.value;
COMMIT;
`, "--set=marker="+string(body))
		if err != nil {
			http.Error(w, "database write failed", http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
	mux.HandleFunc("GET /marker", func(w http.ResponseWriter, r *http.Request) {
		// Read the same relation written by PUT. A fresh database has no marker table yet.
		exists, err := query(r.Context(), password, "SELECT to_regclass('public.acceptance_marker') IS NOT NULL;")
		if err != nil {
			http.Error(w, "database read failed", http.StatusServiceUnavailable)
			return
		}
		if exists == "f" {
			http.NotFound(w, r)
			return
		}
		value, err := query(r.Context(), password, "SELECT value FROM acceptance_marker WHERE id = 1;")
		if err != nil {
			http.Error(w, "database read failed", http.StatusServiceUnavailable)
			return
		}
		if value == "" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = io.WriteString(w, value)
	})
	server := &http.Server{
		Addr:              ":8080",
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       10 * time.Second,
		WriteTimeout:      15 * time.Second,
	}
	log.Fatal(server.ListenAndServe())
}

func query(parent context.Context, password, sql string, extraArgs ...string) (string, error) {
	ctx, cancel := context.WithTimeout(parent, 3*time.Second)
	defer cancel()
	args := []string{"-X", "-qAt", "-w", "--set=ON_ERROR_STOP=1", "-h", "db", "-U", "postgres", "-d", "postgres"}
	command := exec.CommandContext(ctx, "/psql", append(args, extraArgs...)...)
	command.Env = []string{"PGPASSWORD=" + password, "PGCONNECT_TIMEOUT=2", "PGSSLMODE=disable"}
	command.Stdin = strings.NewReader(sql)
	output, err := command.Output()
	return strings.TrimSpace(string(output)), err
}
