# ADR-005: Pump/AIO Recognition as Primary CPU Cooling

## Status
Accepted

## Context
Many modern systems use AIO liquid coolers or pump headers (W_PUMP) as the primary CPU cooling device. The original design excluded pumps from the "CPU fan" detection, showing "No CPU fan detected" on systems with liquid cooling.

## Decision
- `IsCpuFanSnapshot` now accepts `FanDeviceKind.Pump` and `FanDeviceKind.AioCooler` as valid CPU cooling devices
- Fallback priority: explicit CPU fan > pump/AIO header > single channel
- Pump headers (W_PUMP, wpump) are classified and displayed with appropriate badges
- AI fan control applies to pumps the same as fans (duty % control)

## Consequences
- Systems with AIO coolers correctly identify and control the pump
- UI shows pump-specific labeling (PUMP badge) instead of generic "fan"
- Existing fan-only systems are unaffected (pump path only activates when DeviceKind matches)
- Users with both a CPU fan and a pump see the pump prioritized (pumps are more critical)
