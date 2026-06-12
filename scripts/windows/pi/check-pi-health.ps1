[CmdletBinding()]
param(
    [string]$HostOrIp = $env:PI_HOST_OR_IP,
    [string]$User = $(if ($env:PI_USER) { $env:PI_USER } else { "sebastian" }),
    [string]$KeyPath = $(if ($env:PI_SSH_KEY_PATH) { $env:PI_SSH_KEY_PATH } else { $codexKey = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_pi_ed25519"; if (Test-Path -LiteralPath $codexKey) { $codexKey } else { Join-Path $HOME ".ssh\id_ed25519" } }),
    [string]$KnownHostsPath = $(if ($env:PI_SSH_KNOWN_HOSTS_PATH) { $env:PI_SSH_KNOWN_HOSTS_PATH } else { $codexKnownHosts = Join-Path $HOME ".codex\keys\swedesclantracker-pi\.codex_known_hosts"; if (Test-Path -LiteralPath $codexKnownHosts) { $codexKnownHosts } else { Join-Path $HOME ".ssh\known_hosts" } }),
    [string]$Since = "",
    [int]$ErrorLines = 8,
    [switch]$NoColor,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pi-common.ps1")

function Resolve-HealthWindow {
    param([string]$RequestedSince)

    if (-not [string]::IsNullOrWhiteSpace($RequestedSince)) {
        return $RequestedSince.Trim()
    }

    Write-Host "Warning/error window"
    Write-Host "  1. 24 hours ago (default)"
    Write-Host "  2. 30 minutes ago"
    Write-Host "  3. 3 days ago"
    Write-Host "  4. 7 days ago"
    Write-Host "  5. Custom"
    $choice = Read-Host "Choose a window [1]"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        return "24 hours ago"
    }

    switch ($choice.Trim()) {
        "1" { return "24 hours ago" }
        "2" { return "30 minutes ago" }
        "3" { return "3 days ago" }
        "4" { return "7 days ago" }
        "5" {
            $custom = Read-Host "Enter journalctl --since value"
            if ([string]::IsNullOrWhiteSpace($custom)) {
                return "24 hours ago"
            }
            return $custom.Trim()
        }
        default {
            return $choice.Trim()
        }
    }
}

try {
    $HostOrIp = Resolve-PiHost -HostOrIp $HostOrIp
    $User = Resolve-PiUser -User $User
    $KeyPath = Resolve-PathWithPrompt -PathValue $KeyPath -PromptLabel "SSH private key path"
    $KnownHostsPath = Resolve-PathWithPrompt -PathValue $KnownHostsPath -PromptLabel "SSH known_hosts path"
    $Since = Resolve-HealthWindow -RequestedSince $Since

    $payload = @{
        since = $Since
        errorLines = [Math]::Max(1, $ErrorLines)
        color = -not [bool]$NoColor
    } | ConvertTo-Json -Compress

    $payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $python = @"
import base64
import datetime as dt
import json
import os
import re
import shutil
import socket
import subprocess
import sys

payload = json.loads(base64.b64decode("$payloadB64").decode("utf-8"))
since = payload.get("since") or "30 minutes ago"
error_lines = int(payload.get("errorLines") or 8)
use_color = bool(payload.get("color", True))
services = ["swedesclantracker-api", "swedesclantracker-worker", "nginx"]

class Color:
    reset = "\033[0m" if use_color else ""
    bold = "\033[1m" if use_color else ""
    dim = "\033[2m" if use_color else ""
    red = "\033[31m" if use_color else ""
    green = "\033[32m" if use_color else ""
    yellow = "\033[33m" if use_color else ""
    cyan = "\033[36m" if use_color else ""

def paint(text, color):
    return f"{color}{text}{Color.reset}" if color else text

def run(cmd):
    return subprocess.run(cmd, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)

def first_line(text, default="n/a"):
    for line in text.splitlines():
        line = line.strip()
        if line:
            return line
    return default

def status_mark(ok):
    return paint("OK", Color.green) if ok else paint("!!", Color.red)

def status_value(value, ok):
    return paint(value, Color.green if ok else Color.red)

def warn_value(value, ok):
    return paint(value, Color.green if ok else Color.yellow)

def http_value(code, ok):
    if ok:
        return paint(code, Color.green)
    if code and code.startswith(("4", "5")):
        return paint(code, Color.red)
    return paint(code, Color.yellow)

def read_file(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return handle.read().strip()
    except Exception:
        return ""

def format_seconds(value):
    try:
        seconds = int(float(value))
    except Exception:
        return "n/a"
    days, rem = divmod(seconds, 86400)
    hours, rem = divmod(rem, 3600)
    minutes, _ = divmod(rem, 60)
    if days:
        return f"{days}d {hours}h {minutes}m"
    if hours:
        return f"{hours}h {minutes}m"
    return f"{minutes}m"

def get_temperature():
    temp = read_file("/sys/class/thermal/thermal_zone0/temp")
    if temp:
        try:
            return float(temp) / 1000
        except Exception:
            pass
    vcgencmd = shutil.which("vcgencmd")
    if vcgencmd:
        output = run([vcgencmd, "measure_temp"]).stdout
        match = re.search(r"temp=([0-9.]+)", output)
        if match:
            return float(match.group(1))
    return None

uptime_raw = read_file("/proc/uptime")
uptime_seconds = float(uptime_raw.split()[0]) if uptime_raw else 0

def get_service_row(service):
    active = first_line(run(["systemctl", "is-active", service]).stdout, "unknown")
    enabled = first_line(run(["systemctl", "is-enabled", service]).stdout, "unknown")
    since_mono = first_line(run(["systemctl", "show", service, "--property=ActiveEnterTimestampMonotonic", "--value"]).stdout, "")
    runtime = "n/a"
    if since_mono and since_mono.isdigit() and int(since_mono) > 0 and uptime_seconds > 0:
        runtime = format_seconds(max(0, uptime_seconds - (int(since_mono) / 1000000)))
    ok = active == "active"
    active_text = status_value(f"{active:<8}", ok)
    enabled_text = warn_value(f"{enabled:<8}", enabled == "enabled")
    runtime_text = paint(runtime, Color.cyan)
    return ok, f"{status_mark(ok)} {service:<27} {active_text} enabled={enabled_text} runtime={runtime_text}"

def get_http_code(url):
    curl = shutil.which("curl")
    if not curl:
        return "curl-missing"
    proc = run([curl, "-s", "-o", "/dev/null", "-w", "%{http_code}", "--max-time", "5", url])
    code = first_line(proc.stdout, "000")
    return code

def get_recent_errors():
    cmd = [
        "sudo", "-n", "journalctl",
        "--since", since,
        "-p", "warning..alert",
        "-u", "swedesclantracker-api",
        "-u", "swedesclantracker-worker",
        "-u", "nginx",
        "--no-pager",
        "-n", str(error_lines),
    ]
    proc = run(cmd)
    lines = []
    for line in proc.stdout.splitlines():
        if "EntityFrameworkCore.Database.Command" in line:
            continue
        if "No journal files were found" in line:
            continue
        if "-- No entries --" in line:
            continue
        line = line.strip()
        if line:
            lines.append(line)
    return proc.returncode, summarize_journal_lines(lines, error_lines)

def parse_journal_line(line):
    match = re.match(r"^([A-Z][a-z]{2}\s+\d+\s+\d\d:\d\d:\d\d)\s+\S+\s+([^:\[]+)(?:\[[0-9]+\])?:\s*(.*)$", line)
    if not match:
        return {
            "time": "",
            "unit": "journal",
            "summary": compact_message(line),
            "frame": "",
            "raw": line,
        }

    timestamp, unit, message = match.groups()
    frame = ""
    frame_match = re.search(r"\s+at\s+(SwedesClanTracker\.[^(]+\(.*?\))\s+in\s+(.+?):line\s+([0-9]+)", message)
    if frame_match:
        member, path, line_no = frame_match.groups()
        file_name = re.split(r"[\\/]", path)[-1]
        frame = f"{member} ({file_name}:{line_no})"

    summary = compact_message(message)
    return {
        "time": timestamp,
        "unit": unit,
        "summary": summary,
        "frame": frame,
        "raw": line,
    }

def compact_message(message):
    message = re.sub(r"\s+", " ", message).strip()
    message = re.sub(r"\s+at\s+.*$", "", message)
    message = re.sub(r"^SwedesClanTracker\.[A-Za-z0-9_.]+\[[0-9]+\]\s*", "", message)
    if len(message) > 170:
        return message[:167] + "..."
    return message

def normalize_summary(summary):
    normalized = summary.lower()
    normalized = re.sub(r"\b[0-9a-f]{8,}\b", "#", normalized)
    normalized = re.sub(r"\b\d+\b", "#", normalized)
    return normalized

def summarize_journal_lines(lines, limit):
    groups = []
    by_key = {}
    for line in lines:
        parsed = parse_journal_line(line)
        key = (parsed["unit"], normalize_summary(parsed["summary"]), parsed["frame"])
        if key not in by_key:
            entry = {
                "count": 0,
                "first": parsed["time"],
                "last": parsed["time"],
                "unit": parsed["unit"],
                "summary": parsed["summary"],
                "frame": parsed["frame"],
            }
            by_key[key] = entry
            groups.append(entry)
        entry = by_key[key]
        entry["count"] += 1
        if parsed["time"]:
            entry["last"] = parsed["time"]

    return groups[-limit:]

hostname = socket.gethostname()
checked = dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
uptime = format_seconds(uptime_seconds)
load = first_line(read_file("/proc/loadavg"), "n/a").split()
load_text = " ".join(load[:3]) if load else "n/a"
temp = get_temperature()
temp_text = "n/a" if temp is None else f"{temp:.1f} C"
temp_ok = temp is None or temp < 75

disk = shutil.disk_usage("/")
disk_used_pct = (disk.used / disk.total) * 100 if disk.total else 0
disk_text = f"{disk_used_pct:.0f}% used ({disk.free / (1024**3):.1f} GiB free)"

meminfo = {}
for line in read_file("/proc/meminfo").splitlines():
    parts = line.split()
    if len(parts) >= 2:
        meminfo[parts[0].rstrip(":")] = int(parts[1])
mem_total = meminfo.get("MemTotal", 0)
mem_available = meminfo.get("MemAvailable", 0)
mem_used_pct = 100 - ((mem_available / mem_total) * 100) if mem_total else 0
mem_text = f"{mem_used_pct:.0f}% used ({mem_available / (1024**2):.1f} GiB available)" if mem_total else "n/a"

api_code = get_http_code("http://127.0.0.1:5166/api/dashboard")
api_ok = api_code in ("200", "401")
dashboard_code = get_http_code("http://127.0.0.1/")
dashboard_ok = dashboard_code and dashboard_code.startswith(("2", "3"))

print(paint(f"SwedesClanTracker Pi Health - {hostname}", Color.bold))
print(f"Checked: {paint(checked, Color.cyan)}   Window: {paint(since, Color.cyan)}")
print()
print(paint("System", Color.bold))
print(f"  {status_mark(True)} Uptime:      {paint(uptime, Color.cyan)}")
print(f"  {status_mark(temp_ok)} Temp:        {warn_value(temp_text, temp_ok)}")
print(f"  {status_mark(disk_used_pct < 85)} Disk /:     {warn_value(disk_text, disk_used_pct < 85)}")
print(f"  {status_mark(mem_used_pct < 90)} Memory:      {warn_value(mem_text, mem_used_pct < 90)}")
print(f"  {status_mark(True)} Load avg:    {paint(load_text, Color.cyan)}")
print()
print(paint("Services", Color.bold))
service_ok = True
for service in services:
    ok, row = get_service_row(service)
    service_ok = service_ok and ok
    print(f"  {row}")
print()
print(paint("HTTP", Color.bold))
print(f"  {status_mark(api_ok)} API local:   HTTP {http_value(api_code, api_ok)}")
print(f"  {status_mark(dashboard_ok)} Dashboard:   HTTP {http_value(dashboard_code, dashboard_ok)}")
print()
print(paint("Recent warnings/errors", Color.bold))
journal_exit, errors = get_recent_errors()
if errors:
    for item in errors:
        count_text = f" x{item['count']}" if item["count"] > 1 else ""
        time_text = item["last"] if item["first"] == item["last"] else f"{item['first']} -> {item['last']}"
        print(f"  {status_mark(False)} {paint(time_text, Color.cyan)}{paint(count_text, Color.yellow)}")
        print(f"     {item['unit']}: {paint(item['summary'], Color.red)}")
        if item["frame"]:
            print(f"     frame: {paint(item['frame'], Color.dim)}")
else:
    print(f"  OK No warning/error journal entries in the selected window.")

overall_ok = service_ok and temp_ok and disk_used_pct < 85 and mem_used_pct < 90 and api_ok and dashboard_ok and not errors
print()
overall_text = paint("OK", Color.green) if overall_ok else paint("CHECK", Color.red)
print(f"Overall: {overall_text}")
sys.exit(0 if overall_ok else 2)
"@
    $pythonB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($python))
    $remoteCommand = "printf '%s' '$pythonB64' | base64 -d | python3"

    $result = Invoke-Ssh -HostOrIp $HostOrIp -User $User -KeyPath $KeyPath -KnownHostsPath $KnownHostsPath -RemoteCommand $remoteCommand
    if ($result.Output) {
        $result.Output | Out-Host
    }

    if ($result.ExitCode -eq 0 -or $result.ExitCode -eq 2) {
        Pause-IfRequested -NoPause:$NoPause
        exit $result.ExitCode
    }

    Write-OpResult -Success $false -Step "Pi health check failed" -Details "Exit code: $($result.ExitCode)" -NextStep "Run test-pi-ssh.ps1, then retry check-pi-health.ps1."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
catch {
    Write-OpResult -Success $false -Step "Pi health check error" -Details $_.Exception.Message -NextStep "Confirm SSH credentials and Pi availability."
    Pause-IfRequested -NoPause:$NoPause
    exit 1
}
