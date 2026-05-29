export function toneClass(tone) {
  if (tone === "success") return "tone tone-success";
  if (tone === "warning") return "tone tone-warning";
  if (tone === "danger") return "tone tone-danger";
  if (tone === "info") return "tone tone-info";
  return "tone";
}
