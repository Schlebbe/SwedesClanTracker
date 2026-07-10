# Frontend visual materials

The current visual system uses a restrained dark stone palette with muted brass accents. It does not depend on copied official OSRS interface assets.

## Usage rules

- Prefer solid tinted surfaces and one-pixel borders for readability and performance.
- Use local, permissively licensed or generated decorative assets only when they improve hierarchy.
- Keep textures subtle enough that table text, status labels, and timestamps remain readable.
- Avoid large background images, noisy overlays, heavy bevel stacks, and decorative charts.
- Visual treatment must never imply that unsupported game statistics exist.

## Current implementation

The shared stylesheet in `swedesclantracker-frontend/src/styles/osrs-theme.css` owns palette, panel, button, table, status, responsive, and authentication styles. Future visual work should update those tokens and components rather than reintroducing page-specific mockup styling.
