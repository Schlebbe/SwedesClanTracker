# OSRS Texture / Visual Depth Research Notes

Generated: `2026-06-09T14:27:51+00:00`

## Goal

The current frontend can create dark panels and gold borders, but the reference images have more visual depth:
- stone-like surfaces;
- aged brass/bronze bevels;
- subtle paper/noise overlays;
- worn panel interiors;
- less flat single-color borders.

Improve this without:
- official OSRS assets;
- heavy dependencies;
- Raspberry Pi performance cost;
- licensing uncertainty;
- hurting readability.

## Recommendation

Use this order:

1. CSS-only procedural texture first.
2. Small inline SVG data-URI noise/pattern overlays if needed.
3. Static local tile assets only if a specific texture is worth the license/vendor cost.
4. Avoid runtime texture libraries unless they clearly pay for themselves.

## Option A — CSS + inline SVG data URI overlays

Recommended first implementation.

Use:
- base dark color;
- radial/linear gradients for vignette and bevel;
- repeated SVG noise/pattern data URI;
- inset shadows;
- pseudo-element overlay with low opacity;
- layered gradients or `border-image` for brass edges.

Example:

```css
:root {
  --osrs-noise-svg: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='64' height='64' viewBox='0 0 64 64'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='64' height='64' filter='url(%23n)' opacity='.28'/%3E%3C/svg%3E");
}

.osrs-stone-surface {
  background:
    linear-gradient(180deg, rgba(255, 235, 170, 0.045), rgba(0, 0, 0, 0.18)),
    radial-gradient(circle at 20% 0%, rgba(215, 181, 109, 0.08), transparent 35%),
    var(--osrs-noise-svg),
    var(--osrs-panel);
  background-blend-mode: overlay, soft-light, multiply, normal;
  box-shadow:
    inset 0 1px 0 rgba(255, 235, 170, 0.12),
    inset 0 -1px 0 rgba(0, 0, 0, 0.55),
    0 14px 30px rgba(0, 0, 0, 0.32);
}
```

Guidelines:
- Keep texture opacity low.
- Do not place heavy noise behind table text.
- Texture shell, panels, headers, and buttons more than table bodies.
- Keep status pills mostly flat for readability.

## Option B — Static pattern tiles from Subtle Patterns / Toptal

Good if CSS-only is not enough.

Use:
- 1–3 tiny PNG/GIF tile assets;
- store under `swedesclantracker-frontend/src/assets/textures/`;
- add license/source notes;
- apply at low opacity through CSS.

Good categories:
- dark denim / dark fabric;
- dark wall / concrete;
- black paper / black linen;
- subtle noise;
- parchment/paper for rare highlight surfaces.

Pros:
- closer to the generated references.
- easy to preview and tune.

Cons:
- asset/license management.
- can make text noisy.
- must avoid official OSRS assets.

## Option C — Transparent Textures

Useful as an exploration source because it lists many downloadable texture tiles and generated CSS snippets.

Good candidate names visible in the catalog:
- Asfalt Dark
- Black Linen
- Black Paper
- Dark Wall
- Dark Wood
- Concrete Wall
- Buried
- Cardboard / paper textures

Use cautiously:
- verify license/attribution for selected pattern;
- document source;
- keep tile opacity subtle.

## Option D — PatternFills

MIT-licensed SVG pattern collection that can output CSS/SVG fills.

Best use:
- do not add runtime/build dependency at first;
- copy/adapt one small SVG pattern if needed and keep attribution/license;
- use for subtle crosshatch/geometric fills, not realistic stone.

Pros:
- MIT license.
- SVG/vector.
- lightweight.

Cons:
- more geometric than stone/parchment.
- can look too modern if overused.

## Option E — GeoPattern

MIT-licensed SVG pattern generator that outputs CSS-ready data URLs.

Best use:
- not recommended as a core runtime dependency;
- maybe useful later for deterministic clan/member banners;
- generate static output offline rather than adding runtime dependency.

## What not to do

Avoid:
- official OSRS UI textures/assets;
- WebGL/3D texture libraries;
- runtime procedural dependencies;
- noisy backgrounds behind dense table text;
- large photographic textures;
- unverified asset licenses;
- dynamic texture generation on every render;
- making the UI look like a game client screenshot rather than an admin web app.

## Suggested CSS texture system

Add utility classes:
- `.osrs-texture-stone`
- `.osrs-texture-parchment`
- `.osrs-texture-brass`
- `.osrs-texture-table`

Use in:
- `StonePanel`: stone texture by default.
- `BeveledButton`: brass edge/highlight texture.
- `AppShell`: global dark noise/vignette.
- `DataTable`: extremely subtle table texture.
- `StatusPill`: mostly flat.

## Suggested Codex texture prompt

```text
Implement a CSS-only OSRS texture/depth pass.

Use only CSS gradients, inset shadows, pseudo-elements, layered borders, and inline SVG data-URI noise/patterns.
Do not add external dependencies.
Do not add downloaded assets yet.
Do not use official OSRS assets.
Keep texture subtle and preserve readability.

Scope:
- improve AppShell background depth;
- improve StonePanel stone/parchment feeling;
- improve BeveledButton brass/edge treatment;
- improve sidebar/topbar depth;
- improve table header/panel header depth;
- keep table body rows readable.

Run npm run build and npm run lint.
```

## Suggested later asset prompt

```text
Research and propose 2-3 tiny local texture assets for the OSRS theme.

Requirements:
- license must permit project use;
- source and attribution must be documented;
- assets must be small/tileable;
- no official OSRS assets;
- no large photographic textures.

Do not add assets until the user approves the specific files and licenses.
```
