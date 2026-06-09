export function EmptyFeatureState({
  title = "Nothing to show",
  message = "No records are available for this view.",
  action = null,
  tone = "neutral",
  className = "",
}) {
  const classes = ["osrs-empty-state", tone !== "neutral" ? `osrs-empty-state-${tone}` : "", className].filter(Boolean).join(" ");

  return (
    <div className={classes}>
      <strong>{title}</strong>
      <p>{message}</p>
      {action ? <div className="osrs-empty-action">{action}</div> : null}
    </div>
  );
}
