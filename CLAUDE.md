# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Console bridge between the **Delsys Trigno SDK** (TCP data server) and an **OSC receiver**. It connects to the Trigno server on `localhost`, streams sensor data (EMG, ACC, IMU EMG, IMU AUX), and forwards samples as OSC messages to `127.0.0.1:7001`. Part of the MED 702 project at Aalborg Universitet.

## Build / Run

https://delsys.com/downloads/USERSGUIDE/MAN-025-3-5-Trigno-SDK.pdf


https://delsys.com/support/trigno-sdk/ 

under ' installer' get latest version (3.6)

https://www.adinstruments.com/support/downloads/windows/trigno-control-utility

RUN THIS COMMAND WITH ADMIN POWERSHELL:

 pnputil /add-driver "C:\Program Files (x86)\Delsys, Inc\Trigno SDK\Drivers\Trigno\SiUSBXp.inf" /install



Targets `.NET Framework 4.8`. Requires MSBuild / Visual Studio Build Tools (the `dotnet` CLI alone doesn't build classic `.NET Framework` projects).

```bash
# Build (Release output goes to ./builds/net48/)
msbuild G702-Trigno-Console.sln /p:Configuration=Release

# Debug build
msbuild G702-Trigno-Console.sln /p:Configuration=Debug

# Run the built exe
./builds/net48/G702-Trigno-Console.exe
```

There are no tests in this repo.

## External dependency — Rug.Osc

`Rug.Osc.dll` is committed at `lib/Rug.Osc.dll` and referenced from the csproj via a repo-relative `<HintPath>lib\Rug.Osc.dll</HintPath>` with `<Private>True</Private>` so MSBuild copies it next to the exe. No manual setup needed on fresh clones.

## Architecture

The entire application lives in `Program.cs` (one `Program` class, ~500 lines). Key shape:

- **Command channel** — a single TCP connection to port `50040` used for line-based ASCII commands (`START`, `STOP`, `QUIT`, `TRIGGER?`, `SENSOR n TYPE?`, `RATE?`, `UPSAMPLE OFF`). `SendCommand()` writes a command + blank terminator line and reads one response line + blank line.
- **Data channels** — four separate TCP connections opened on `start()`, each drained by its own background thread reading raw `float` samples with `BinaryReader.ReadSingle()`:
  - Port `50041` → `EmgWorker` → standard EMG (1 float × 16 sensors per frame)
  - Port `50042` → `AccWorker` → standard ACC (3 floats × 16 sensors: X,Y,Z)
  - Port `50043` → `ImuEmgWorker` → IMU-EMG (1 float × 16); **this worker also emits OSC** per sample as `/{sensorIndex}` to `127.0.0.1:7001`
  - Port `50044` → `ImuAuxWorker` → IMU AUX (9 floats × 16: acc X/Y/Z, gyro X/Y/Z, mag X/Y/Z)
- **Sensor discovery** — on `Connect()`, the code queries `SENSOR i TYPE?` for i=1..16 and maps responses via the `sensorList` dictionary into the `SensorTypes` enum (`SensorTrigno`, `SensorTrignoImu`, `SensorTrignoMiniHead`, `NoSensor`). `sensorList` is seeded with a starter set of Delsys model strings — extend it as new sensor types are encountered. Unknown / unmapped responses fall through to `NoSensor` via `TryGetValue`, so new hardware can't crash the connect flow.
- **Data buffers** — each sensor stream has both a per-sensor `List<float>[16]` (accumulated samples) and a scalar `float[16]` (latest sample). CSV header builders (`csvStandardSensors`, `csvIMSensors`) are assembled in `start()` but no file writing is wired up.
- **State flags** — `connected` gates command sending; `running` gates the worker loops. Worker threads are `IsBackground = true` and exit when `running` goes false.

### Entry point

`Main()` instantiates a `Program`, runs `Connect() → start() → (loop) → quit()`, and installs a `Console.CancelKeyPress` handler that flips `running = false` so the worker threads exit cleanly within ~1 s (the per-stream `ReadTimeout`). WinForms leftovers (commented `using System.Windows.Forms`, `connectButton.Enabled = ...`) remain inside the private methods — harmless but worth knowing if you port to a GUI later.

### OSC output

A single static `OscSender` (`127.0.0.1:7001`) is shared by all threads. `Osend()` swallows exceptions silently — if OSC delivery looks broken, add logging there first.
