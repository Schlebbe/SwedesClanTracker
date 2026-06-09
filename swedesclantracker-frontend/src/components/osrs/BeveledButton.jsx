const variantClassByName = {
  primary: "osrs-button osrs-button-primary",
  secondary: "osrs-button osrs-button-secondary",
  ghost: "osrs-button osrs-button-ghost",
  danger: "osrs-button osrs-button-danger",
  disabled: "osrs-button osrs-button-disabled",
};

export function BeveledButton({
  children,
  variant = "secondary",
  type = "button",
  disabled = false,
  loading = false,
  className = "",
  ...props
}) {
  const resolvedVariant = disabled || loading ? "disabled" : variant;
  const classes = [variantClassByName[resolvedVariant] ?? variantClassByName.secondary, className].filter(Boolean).join(" ");

  return (
    <button type={type} className={classes} disabled={disabled || loading} {...props}>
      {loading ? "Working..." : children}
    </button>
  );
}
