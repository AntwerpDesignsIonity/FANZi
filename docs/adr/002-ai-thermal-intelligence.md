# ADR-002: AI Thermal Intelligence Engine

## Status
Accepted

## Context
Static fan curves react to current temperature but cannot anticipate thermal spikes (e.g., loading a game causes a 15C jump before the fan ramps up). We need predictive fan control.

## Decision
Implement a local AI engine with three components:
1. **ThermalPredictor** — PD-control with exponential smoothing, predicts temperature 30s ahead
2. **AnomalyDetector** — Statistical outlier detection (2.5σ) for temperature spikes and fan stalls
3. **SmartFanCurveEngine** — Combines prediction + custom curves + safety overrides

## Consequences
- Fans pre-emptively ramp before spikes, reducing thermal throttling
- Fan stall detection alerts users to hardware failures
- No external API calls — all inference is local, zero latency
- Minimal CPU overhead: ~0.01ms per tick computation
- Learning window: 10 samples (~30s) before predictions become active
