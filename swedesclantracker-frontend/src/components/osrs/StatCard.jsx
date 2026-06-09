import { UnavailableMetric } from "./UnavailableMetric";

export function StatCard({
  label,
  value,
  detail,
  trend,
  icon,
  tone = "neutral",
  available = true,
  unavailableReason = "Requires enhanced sync support.",
  className = "",
}) {
  const classes = ["osrs-stat-card", tone !== "neutral" ? `osrs-stat-card-${tone}` : "", className].filter(Boolean).join(" ");

  return (
    <article className={classes}>
      <header>
        {icon ? <span className="osrs-stat-icon" aria-hidden="true">{icon}</span> : null}
        <span>{label}</span>
      </header>
      {available ? (
        <>
          <strong>{value ?? "-"}</strong>
          {detail ? <p>{detail}</p> : null}
          {trend ? <small>{trend}</small> : null}
        </>
      ) : (
        <UnavailableMetric reason={unavailableReason} />
      )}
    </article>
  );
}
