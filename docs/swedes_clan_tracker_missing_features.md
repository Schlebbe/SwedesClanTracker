# Data and workflow coverage

This document records the boundary between the current frontend and data that is not yet collected. It replaces the former mockup gap list.

## Available today

The frontend can use current API responses for tracker status, roster membership, clan rank, lifecycle status, last sync/seen timestamps, stale flags, rank mismatch and promotion flags, administrative queue cases, player lifecycle events, clan-log activity, and readiness/configuration signals.

## Not collected today

The current persisted snapshot/API model does not provide total XP, combat level, per-skill values, boss or raid KC, weekly XP gains, drop/split data, collection-log item detail, confidence metrics, or competition/league data. These values must not appear as invented numbers or decorative charts.

## Safe frontend behavior

- Omit unsupported metrics from the main surfaces.
- Use an empty or unavailable state only when it clarifies a real product boundary.
- Do not create a frontend-only substitute for data that belongs in the sync and persistence pipeline.
- If richer data becomes necessary, propose an additive API projection first and obtain approval before touching Core or Worker.

## Current next steps

Validate the redesigned surfaces against the live API, then prioritize the smallest read-only API projection that removes a demonstrated workflow limitation. Keep sync and lifecycle behavior unchanged.
