export function IconGlyph({ name = "default", className = "", label }) {
  const classes = ["osrs-glyph", `osrs-glyph-${name}`, className].filter(Boolean).join(" ");

  return (
    <span className={classes} aria-hidden={label ? undefined : "true"} aria-label={label}>
      <span />
    </span>
  );
}
