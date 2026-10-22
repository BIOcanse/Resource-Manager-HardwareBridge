# Resource Manager Hardware Bridge

Hardware Bridge is a separate helper used by [Resource Manager](https://github.com/BIOcanse/Resource-Manager) for supported AMD CPU power, current and temperature limits and Curve Optimizer operations. It uses [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) and communicates with its caller through newline-delimited JSON on standard input and output. The repository contains the helper's source and a ZenStates-Core submodule pointer; prebuilt Windows packages are available under [Releases](https://github.com/BIOcanse/Resource-Manager-HardwareBridge/releases).

## Interface

Each request includes an `id` and an `op`. The response carries the same `id`, an `ok` field, and either operation data or an `error` message.

| Operation | Purpose |
| --- | --- |
| `describe` | Report detected CPU capabilities and whether hardware access is available. |
| `read` | Read supported power-limit, current-limit and Curve Optimizer values. |
| `set-power-limit` | Set one supported `stapm`, `slow`, or `fast` power limit. |
| `set-current-limit` | Set one supported `tdc-vdd`, `tdc-soc`, `edc-vdd`, or `edc-soc` current limit. |
| `set-temperature-limit` | Set the Tctl temperature limit. |
| `set-pbo-scalar` | Set the PBO scalar. |
| `set-curve-optimizer` | Set an all-core Curve Optimizer offset when supported. |
| `set-igpu-curve-optimizer` | Set an integrated-GPU Curve Optimizer offset when supported. |
| `dump-power-table` | Return a bounded raw power-table sample for diagnostics; optional `offset` and `count` select the range. |

Every `set-` request carries a `value` and its `unit` (`W`, `mW`, `A`, `mA`, `°C`, `×`, or `档` for Curve Optimizer steps), plus `which` where the table lists one. The bridge converts only between units of the same kind, such as `A` and `mA`, and rejects a missing or mismatched unit instead of guessing.

The helper reports the hardware result and readback; policy decisions remain with the caller. Support varies by processor, firmware, and available kernel-access driver. Hardware writes can cause instability, so check `describe` before issuing them.

## Build

Install the .NET 10 SDK and initialize the pinned submodule:

```powershell
git submodule update --init
dotnet build src/ResourceManager.HardwareBridge/ResourceManager.HardwareBridge.csproj -c Release
```

The helper targets Windows x64 and needs administrator privileges for hardware access. The release package is a self-contained single file, so no separate .NET runtime is needed; `--runtime-check` confirms it starts without opening any hardware.

This helper is licensed under [GPL-3.0-or-later](LICENSE). ZenStates-Core is developed by [irusanov](https://github.com/irusanov/ZenStates-Core) and retains its own copyright and license notices in the submodule.

## Kernel access

This Resource Manager build uses PawnIO only. It neither loads nor installs InpOut/WinIo. AOD/APOB and raw physical-memory diagnostics are not exposed by the bridge. See `external/ZenStates-Core/RESOURCE-MANAGER-CHANGES.md` for the local GPL source modifications. No installed third-party driver service is removed or reconfigured.
