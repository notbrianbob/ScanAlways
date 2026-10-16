# Measured results

Numbers from running the demos in [README.md](README.md). They are
measurements of the machines listed, made with the method described there,
and they say nothing about how well WatchLattice protects against real
malware: no malware was run.

Rerun them on your own machine; the numbers that matter are the ones for
your hardware and your Defender configuration.

## Summary

- **Decoy CPU.** At its paced rates the decoy's CPU cost is below what the
  per-second sampling can resolve (the paced rows sit within the noise of the
  idle baseline). Measured directly with the cap lifted, one file costs about
  0.04 ms of CPU on the Linux container, which works out to roughly 0.01% of
  one core at `Balanced` (3 files/s) and 0.03% at `FullScan` (8 files/s).
- **Decoy reads.** About 100 KiB/s of logical reads at `Balanced` and
  250 KiB/s at `FullScan`, as the hub defaults intend (up to 64 KiB from each
  file, 3 or 8 files a second).
- **Canary watcher.** No measurable CPU while armed and idle. Overwrite,
  rename and delete each raised an alert within a few milliseconds; the
  control file in the same folder raised none.
- **Defender watchdog.** Readouts match the documented rules for each recorded
  state, and alerts fire once on change, not on every check.

## Run 1: Linux container


Environment:

- Date (UTC): 2026-10-16 08:49
- OS: Ubuntu 24.04.5 LTS (X64)
- CPU: Intel(R) Xeon(R) Processor @ 2.10GHz, 4 logical cores
- Runtime: .NET 8.0.31
- Host: Linux cloud container (no Defender; the live watchdog readout and the Windows hub script do not apply)

### Always-scanning decoy

#### Setup

- Corpus: 2,000 files, 274.60 MiB, seed 1337, in 38 folders
- Window: 60 s per intensity, sampled every 1000 ms, after a 15 s baseline
- I/O counter: /proc/self/io (rchar, syscr)
- Engine settings: the hub defaults (`MaxFilesPerSecond` 8, `BytesReadPerFile` 64 KiB, `MaxFileSizeBytes` 256 MiB)

#### Measured overhead

| Window | CPU (% of one core) | CPU (% of machine) | Logical reads | Read calls/s | Files opened | Peak working set | Peak managed heap |
|---|---:|---:|---:|---:|---:|---:|---:|
| Baseline (harness idle) | 1.07 | 0.267 | 7.0 KiB/s | 39.1 | 0 | 63.29 MiB | 14.20 MiB |
| Decoy Balanced | 0.75 | 0.187 | 101.2 KiB/s | 41.1 | 179 (3.0/s) | 74.39 MiB | 16.44 MiB |
| Decoy FullScan | 0.90 | 0.225 | 254.8 KiB/s | 46.6 | 480 (8.0/s) | 80.85 MiB | 19.20 MiB |

Noise: CPU use per 1000 ms interval varies by ±1.33% of one core on the idle baseline (one standard deviation), from the .NET runtime and the sampler itself, so each window's mean is only known to about ±0.34% (standard error). A decoy row within that of the baseline means the decoy's CPU cost is too small for this method to resolve; the cost-per-file run below measures it directly.

Working set is the whole harness process (the .NET runtime included), so read it as an upper bound for the module.

#### Cost per file

With the pacing cap lifted for 15 s, the engine opened 361,085 files (24073/s) and read 10573.80 MiB, using 14830 ms of CPU.
After subtracting the baseline rate, that is **0.041 ms of CPU per file**. At the paced rates this predicts:

| Intensity | Files/s | Predicted CPU (% of one core) |
|---|---:|---:|
| Idle | 1 | 0.004 |
| Balanced | 3 | 0.012 |
| FullScan | 8 | 0.033 |

The corpus was written just before the run, so its files are in the OS file cache: these are CPU costs with warm-cache reads. On a cold cache each file read also waits on the disk, which adds latency but little CPU.

#### What it emits

First lines of the scanner-style log (`scan-activity.log`), copied verbatim:

```text
[2026-10-16 08:46:15] Starting Balanced protection sweep of <corpus>
[2026-10-16 08:46:21] Scanning (signature): <corpus>/folder06/file00349.docx
[2026-10-16 08:46:21] Scan in progress — 19 items checked, 0 threats found, 551.3 KB read, 3.1 items/s
[2026-10-16 08:46:27] Indexing (signature): <corpus>/folder06/file01287.docx
[2026-10-16 08:46:27] Scan in progress — 37 items checked, 0 threats found, 1.1 MB read, 3.0 items/s
[2026-10-16 08:46:33] Indexing (emulation): <corpus>/folder06/file01789.exe
[2026-10-16 08:46:33] Scan in progress — 55 items checked, 0 threats found, 1.8 MB read, 3.0 items/s
[2026-10-16 08:46:39] Inspecting (emulation): <corpus>/folder06/folder03/folder04/file01036.exe
```


### Canary file watcher

#### Setup

- Planted 3 canaries in a throwaway `Documents` folder:
  - `!000_Financial_Records.docx` (24.0 KiB)
  - `!000_Passwords_Backup.xlsx` (24.0 KiB)
  - `zzz_Tax_Return_Archive.pdf` (24.0 KiB)
- Windows: 30 s before arming (baseline) and 30 s armed with nothing happening, sampled every 1000 ms
- I/O counter: /proc/self/io (rchar, syscr)

#### Idle overhead

| Window | CPU (% of one core) | Logical reads | Peak working set |
|---|---:|---:|---:|
| Baseline (harness idle, nothing armed) | 0.133 | 6.7 KiB/s | 206.58 MiB |
| Watcher armed, nothing happening | 0.200 | 7.2 KiB/s | 210.96 MiB |

The difference between the two rows is the armed watcher's cost. Its integrity re-hash runs every 60 s by default, so a window shorter than that may not include one.

#### Tripping the canaries

| Action | Expected | Result | Time to alert |
|---|---|---|---:|
| Create and edit an ordinary file next to the canaries (control) | no alert | no alert | n/a |
| Overwrite the first 4 KiB of a canary with random bytes (in-place encryption) | alert | alert: Modified (content written) | 6.9 ms |
| Rename a canary to `.locked` (encrypt-and-rename) | alert | alert: Renamed (renamed to !000_Passwords_Backup.xlsx.locked) | 1.1 ms |
| Delete a canary | alert | alert: Deleted (file deleted) | 4.4 ms |

Every step behaved as expected.

#### What it emits

The canary alert log (`canary-alerts.log`), copied verbatim:

```text
[2026-10-16 08:49:01] Canary watcher armed: 3 canaries in 1 folders
[2026-10-16 08:49:34] CANARY TRIPPED: Modified <Documents>/!000_Financial_Records.docx (content written)
[2026-10-16 08:49:35] CANARY TRIPPED: Renamed <Documents>/!000_Passwords_Backup.xlsx (renamed to !000_Passwords_Backup.xlsx.locked)
[2026-10-16 08:49:35] CANARY TRIPPED: Deleted <Documents>/zzz_Tax_Return_Archive.pdf (file deleted)
```


### Defender health watchdog

#### Readouts for recorded states

Each state is a `Get-MpComputerStatus` result run through the real evaluator with the default thresholds.

| State | Health | Findings |
|---|---|---|
| Healthy | **Healthy** | none |
| Real-time protection switched off | **Critical** | [Critical] Real-time protection is OFF.<br>[Warning] On-access protection is off. |
| Tamper protection off, signatures 3 days old | **Degraded** | [Warning] Tamper protection is OFF, so other programs can change Defender's settings.<br>[Warning] Antivirus signatures are 3 days old (last updated 2026-10-13 12:00 UTC). |
| Signatures 9 days old | **Critical** | [Critical] Antivirus signatures are 9 days old (last updated 2026-10-07 12:00 UTC). |
| Another antivirus is primary (passive mode) | **Degraded** | [Warning] Defender is in Passive Mode; another antivirus product is providing real-time protection. |
| Status cannot be read | **Unknown** | [Warning] Could not read Defender status: Get-MpComputerStatus failed (exit 1): module not found |

#### Alerting on change

Five checks in a row: healthy, real-time protection turned off, still off, back on, healthy.
The watchdog log shows one alert when the problem appears and one line when it clears:

```text
check 1: Healthy  Defender healthy: real-time protection on, tamper protection on, signatures current.
check 2: Critical Defender critical: Real-time protection is OFF.
check 3: Critical Defender critical: Real-time protection is OFF.
check 4: Healthy  Defender healthy: real-time protection on, tamper protection on, signatures current.
check 5: Healthy  Defender healthy: real-time protection on, tamper protection on, signatures current.
```

`defender-watchdog.log`, copied verbatim:

```text
[2026-10-16 08:49:36] DEFENDER ALERT [Critical] Real-time protection is OFF.
[2026-10-16 08:49:36] Defender recovered: Real-time protection is OFF. is no longer reported.
```
