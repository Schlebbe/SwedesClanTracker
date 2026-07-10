# SwedesClanTracker frontend design guide

## Design goal

The frontend should feel like a focused clan operations console: dark stone surfaces, restrained brass accents, compact information density, and clear states. It should be recognisably OSRS-inspired without copying official game UI or assets.

This is a substantial product redesign, not a dark reskin of the previous dashboard. The old KPI mockup, placeholder modules, fake quick actions, and visual-target markers are removed.

## Interface principles

- Real API data is the visual source of truth.
- The dashboard answers health, roster, and work-queue questions quickly.
- Tables are used for dense scanning; decision details use panels and cards.
- Status is communicated with readable labels and tone, not color alone.
- Buttons must describe an available action. Unsupported actions are not rendered.
- Destructive or high-impact actions require a distinct treatment and confirmation when introduced.
- Long RSNs, missing values, stale timestamps, loading, and empty states remain readable.
- Focus states and keyboard navigation are part of every interactive surface.

## Visual system

- Dark, tinted stone palette with muted gold for hierarchy and links.
- One-pixel borders and small radii; avoid ornamental bevel stacks and heavy gradients.
- Compact panels with clear headers and consistent spacing.
- System-first typography for fast loading on the Raspberry Pi.
- Lightweight generated/custom icons may support navigation, but official/copyrighted OSRS assets are not used.

## Current navigation

Dashboard, Clan members, Player profiles, Review queues, Activity log, and Readiness are all backed by existing API data. Navigation should not advertise future surfaces as disabled mock controls.

## Future design rule

New UI work must first identify the API field that supplies each displayed value. If the field does not exist, the design should omit it or label it as unavailable rather than fill the space with invented metrics.
