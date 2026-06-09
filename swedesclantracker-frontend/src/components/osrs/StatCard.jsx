import { UnavailableMetric } from "./UnavailableMetric";
import { IconGlyph } from "./IconGlyph";

export function StatCard({
  label,
  value,
  detail,
  trend,
  icon,
  tone = "neutral",
  available = true,
  unavailableReason = "Requires enhanced sync support.",
  variant = "default",
  className = "",
}) {
  const classes = [
    "osrs-stat-card",
    variant !== "default" ? `osrs-stat-card-${variant}` : "",
    tone !== "neutral" ? `osrs-stat-card-${tone}` : "",
    !available ? "osrs-stat-card-unavailable" : "",
    className,
  ].filter(Boolean).join(" ");

  return (
    <article className={classes}>
      <header>
        {icon ? <IconGlyph name={icon} className="osrs-stat-icon" /> : null}
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
