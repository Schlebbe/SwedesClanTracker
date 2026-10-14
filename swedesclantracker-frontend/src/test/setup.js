import "@testing-library/jest-dom/vitest";

Object.defineProperty(window, "scrollTo", { value: () => {}, writable: true });
Object.defineProperty(window.HTMLCanvasElement.prototype, "getContext", { value: () => ({ measureText: () => ({ width: 0 }) }), writable: true });
const originalGetComputedStyle = window.getComputedStyle.bind(window);
window.getComputedStyle = (element, pseudoElement) => {
  if (pseudoElement) return { content: "none", getPropertyValue: () => "" };
  return originalGetComputedStyle(element);
};
