# Brassican rank policy

This document is the permanent policy reference for rank eligibility and
standing. The completed 2026-11-01 transition is retained separately in the
[migration archive](../tools/migrations/2026-11-01-brassican/README.md) for audit
and recovery; it is outside the normal application build and sync workflow.

## Canonical ranks

The canonical order, from lowest to highest, is:

`Brassican`, `Commander`, `Lieutenant`, `Captain`, `General`, `Colonel`,
`Brigadier`, `Admiral`, `Marshal`, `Beast`.

`Brassican` is an ordinary, assignable clan rank. It is the default and
fallback when no higher requirement matches. `Officer` and `Astral` are not
active ranks. Legacy database values are handled by the explicit transition
mapping; they are not silently reintroduced into the active policy.

## Eligibility rules

Every metric comparison is inclusive. A rank matches when **any** condition in
its row is true, and the evaluator returns the highest matching rank.

| Rank | Inclusive eligibility rule |
| --- | --- |
| Brassican | Default when no higher rank matches |
| Commander | Total level >= 2200 |
| Lieutenant | EHB >= 750 **OR** EHP >= 1000 |
| Captain | EHB >= 1000 **OR** EHP >= 1500 **OR** pets >= 10 **OR** collections >= 800 |
| General | EHB >= 1250 **OR** EHP >= 1750 **OR** pets >= 15 **OR** collections >= 900 |
| Colonel | EHB >= 1500 **OR** EHP >= 2000 **OR** pets >= 25 **OR** collections >= 1050 |
| Brigadier | EHB >= 2000 **OR** EHP >= 2500 **OR** pets >= 35 **OR** collections >= 1200 |
| Admiral | EHB >= 3000 **OR** EHP >= 4000 **OR** pets >= 45 **OR** collections >= 1350 |
| Marshal | EHB >= 4000 **OR** EHP >= 5000 **OR** pets >= 55 **OR** collections >= 1450 |
| Beast | EHB >= 5000 **OR** EHP >= 7000 **OR** pets >= 65 **OR** collections >= 1550 |

There is no total-level prerequisite for ranks above Commander. For example,
an account can qualify for Beast through EHB, EHP, pets, or collections even
when its total level is below 2200. Commander is simply one independent rule
in the same highest-match evaluation.

The evaluator starts at Brassican and walks the canonical order. This makes
the fallback deterministic and ensures that a mixed-stat snapshot receives
the highest rank it has earned rather than the first matching rank.

## Eligibility versus standing

`EligibleRank` is the rank justified by the latest accepted snapshot. It is a
calculated observation, not an instruction to demote the player.

`CurrentRank` is the stored standing. Sync may refresh `EligibleRank` and may
create a promotion candidate when eligibility is higher than the standing,
but it must not lower `CurrentRank` automatically. Existing standing above
current eligibility is grandfathered indefinitely until an operator performs
an explicit, separately reviewed change. A temporary stat drop is therefore
not an earned demotion.

Promotion approval and any in-game/WOM/Discord role change are separate
workflow actions. A planned cutover target or report row is not proof that a
live role has already changed. When the authoritative game role is not
verified, the manual change is deferred rather than guessed.

## WOM and legacy values

The following distinctions are intentional:

- `Member` is a Wise Old Man role, not an exact `Brassican` role and not an
  alias for any clan rank.
- Legacy WOM `Recruit` remains a protected/special role. It must not be
  treated as ordinary Brassican.
- Other special or unknown WOM roles remain protected from automatic rank
  alignment.
- `Brassican` itself is not special. A Brassican player with a WOM `Member`
  role has a real mismatch that remains visible for review.

The normal role vocabulary and configuration keys contain only the canonical
assignable ranks. Historical `Recruit`, `Officer`, and `Astral` values may
still appear in retained records, restored backups, old cards, lifecycle
history, or audit exports. Compatibility code
for those records must remain narrow and must never make a retired value
assignable again.

## Grandfathering and manual presentation

Standing protection continues during normal operation:

- sync never performs automatic rank downgrades;
- protected/special/ignored/identity-uncertain rows stay in review; and
- the actual game roster remains authoritative for in-game assignments.

Rank imagery is a separate presentation concern. Use the existing text or
generic presentation where an icon is unavailable. Images do not change rank
evaluation, role IDs, or stored standing.

## Invariants for future changes

Any future rank-policy change must preserve or explicitly review these
invariants:

1. the canonical order and all thresholds are covered by boundary tests;
2. every metric rule states whether it is an inclusive OR condition;
3. the highest matching rank wins, with Brassican as the deterministic default;
4. no total-level gate is added to higher ranks unless this policy is
   deliberately changed;
5. eligibility updates never silently lower current standing;
6. Member and legacy/special WOM values are not silently equated with
   Brassican; and
7. API contracts, database schema, stat sources, and sync cadence remain
   unchanged unless a separate change is approved.
