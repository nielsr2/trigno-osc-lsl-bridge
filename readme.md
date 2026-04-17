# Delsys Wireless Trigno EMG System -> LSL & OSC 

Console bridge between the Delsys Trigno SDK TCP data server and an OSC receiver. The application connects to the Trigno server on `localhost`, reads sensor streams, and forwards IMU EMG samples as OSC messages to `127.0.0.1:7001`.

## What It Does

- Connects to the Delsys Trigno SDK command server on port `50040`
- Opens four TCP data streams for EMG, ACC, IMU EMG, and IMU AUX
- Detects connected sensor types for up to 16 sensors
- Sends IMU EMG samples as OSC messages using the address pattern `/{sensorIndex}`

## Requirements

- Windows
- .NET Framework 4.8
- MSBuild or Visual Studio Build Tools
- Delsys Trigno SDK / Trigno Control Utility installed

`dotnet build` is not sufficient here because this is a classic .NET Framework project.

## Install Trigno SDK

1. Install the Trigno Control Utility from Delsys:
	[https://delsys.com/support/trigno-sdk/](https://delsys.com/support/trigno-sdk/)
2. If needed, use this alternative download page:
	[https://www.adinstruments.com/support/downloads/windows/trigno-control-utility](https://www.adinstruments.com/support/downloads/windows/trigno-control-utility)
3. Download the latest Trigno SDK installer from the Delsys support page.
4. Review the SDK user guide if needed:
	[https://delsys.com/downloads/USERSGUIDE/MAN-025-3-5-Trigno-SDK.pdf](https://delsys.com/downloads/USERSGUIDE/MAN-025-3-5-Trigno-SDK.pdf)

## Install The Driver

Run this in an elevated PowerShell session:

```powershell
pnputil /add-driver "C:\Program Files (x86)\Delsys, Inc\Trigno SDK\Drivers\Trigno\SiUSBXp.inf" /install
```

## Build

Release build:

```powershell
msbuild G702-Trigno-Console.sln /p:Configuration=Release
```

Debug build:

```powershell
msbuild G702-Trigno-Console.sln /p:Configuration=Debug
```

Release output is written to `builds/net48/`.

## Run

```powershell
.\builds\net48\G702-Trigno-Console.exe
```

Before starting the console app, make sure the Trigno Control Utility and SDK data server are available locally.

## Network Behavior

### Command Channel

- TCP `localhost:50040`
- Used for ASCII commands such as `START`, `STOP`, `QUIT`, `RATE?`, and `SENSOR n TYPE?`

### Data Channels

- `50041`: standard EMG
- `50042`: standard ACC
- `50043`: IMU EMG
- `50044`: IMU AUX

Each data channel is read on its own background thread.

### OSC Output

- Target host: `127.0.0.1`
- Target port: `7001`
- Address format: `/{sensorIndex}`

The application uses CoreOSC for message serialization and `UdpClient` for transport.

## Notes

- There are no automated tests in this repository.
- The application currently lives primarily in `Program.cs`.
- Unknown sensor model strings fall back safely to `NoSensor` during discovery.

## Related Projects

This workspace also includes:

- `osc-tester/` for OSC testing
- `trigno-lsl/` for LSL-related output
