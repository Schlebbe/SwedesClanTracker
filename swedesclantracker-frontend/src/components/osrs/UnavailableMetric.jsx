export function UnavailableMetric({ label = "Not tracked yet", reason = "Requires enhanced sync support.", className = "" }) {
  return (
    <div className={["osrs-unavailable-metric", className].filter(Boolean).join(" ")}>
      <span>{label}</span>
      <small>{reason}</small>
    </div>
  );
}
