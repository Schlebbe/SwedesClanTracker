import { generatedIconSources } from "./generatedIconSources";

export function IconGlyph({ name = "default", className = "", label }) {
  const resolvedName = String(name || "default").toLowerCase();
  const src = generatedIconSources[resolvedName];
  const classes = [
    "osrs-glyph",
    src ? "osrs-image-icon" : "",
    `osrs-glyph-${resolvedName}`,
    className,
  ].filter(Boolean).join(" ");

  return (
    <span className={classes} aria-hidden={label ? undefined : "true"} aria-label={label}>
      {src ? <img src={src} alt="" draggable="false" /> : <span />}
    </span>
  );
}
