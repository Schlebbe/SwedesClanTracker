const toneClassByName = {
  success: "osrs-pill osrs-pill-success",
  warning: "osrs-pill osrs-pill-warning",
  danger: "osrs-pill osrs-pill-danger",
  info: "osrs-pill osrs-pill-info",
  neutral: "osrs-pill osrs-pill-neutral",
  unavailable: "osrs-pill osrs-pill-unavailable",
};

export function StatusPill({ children, tone = "neutral", loading = false, className = "" }) {
  const classes = [toneClassByName[tone] ?? toneClassByName.neutral, className].filter(Boolean).join(" ");

  return (
    <span className={classes} data-loading={loading ? "true" : undefined}>
      {children}
    </span>
  );
}
